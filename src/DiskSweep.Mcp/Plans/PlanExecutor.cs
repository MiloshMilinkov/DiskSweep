/*
 * DiskSweep
 * Copyright (c) 2026 Miloš Milinkov. All rights reserved.
 * Proprietary and confidential. Use, copying, modification or distribution
 * without written permission is prohibited. See the LICENSE file.
 */

using DiskSweep.Core.Cleanup;
using DiskSweep.Core.IO;
using DiskSweep.Core.Models;
using ModelContextProtocol;

namespace DiskSweep.Mcp;

public sealed record MovedItem(string Id, string Path, long SizeBytes, string Detail);

public sealed record QuarantineRunResult(bool Approved, string Message, IReadOnlyList<MovedItem> Moved, IReadOnlyList<Refusal> NotMoved,
    long MovedBytes, string MovedSize);

/// <summary>
/// Carries out one plan: uses it up, asks the person, then hands the items to <see cref="CleanupService"/>, which checks
/// each again right before moving it. The destination is always quarantine; the service's Recycle Bin refuses everything.
/// </summary>
public sealed class PlanExecutor(SweepEnvironment env, PlanStore plans)
{
    public QuarantineRunResult Execute(string planId, string clientName, CancellationToken token)
    {
        if (!plans.TryTake(planId, out var plan, out string refusal)) throw new McpException(refusal);

        if (token.IsCancellationRequested)
            return new QuarantineRunResult(false, "The request was cancelled. Nothing moved.", [], [], 0, PathUtil.FormatBytes(0));

        env.Log.Info($"MCP: asking the user to approve plan {plan.Id} ({plan.Entries.Count} item(s)) for \"{clientName}\".");
        bool approved = env.Approval.Confirm(new ApprovalRequest(clientName, plan.Entries.Count, plan.TotalBytes,
            plan.Entries.Select(e => e.Item.Path).ToList()));

        string? stop = !approved ? "The user declined. Nothing moved."
            : env.Clock.GetUtcNow() >= plan.ExpiresAt ? "The plan expired while the confirmation was open. Nothing moved. Propose a new one."
            : token.IsCancellationRequested ? "The request was cancelled. Nothing moved."
            : null;
        if (stop is not null)
        {
            env.Log.Info($"MCP: plan {plan.Id} not carried out: {stop}");
            return new QuarantineRunResult(false, stop, [], [], 0, PathUtil.FormatBytes(0));
        }

        var notMoved = new List<Refusal>();
        var toMove = new List<PlanEntry>();
        foreach (var entry in plan.Entries)
        {
            if (PlanItemRule.WhyNot(entry.Item) is { } why) notMoved.Add(new(entry.ItemId, entry.Item.Path, why));
            else toMove.Add(entry);
        }

        // Run reports one outcome per item, in the order given (stopped items included), so outcomes line up with toMove.
        var report = env.NewCleanupService(plan.Guard)
            .Run(toMove.Select(e => e.Item).ToList(), CleanupDestination.Quarantine, token: token);

        var moved = new List<MovedItem>();
        foreach (var (entry, outcome) in toMove.Zip(report.Outcomes))
        {
            if (outcome.Status == CleanupStatus.Moved) moved.Add(new(entry.ItemId, outcome.Item.Path, outcome.Bytes, outcome.Detail));
            else notMoved.Add(new(entry.ItemId, outcome.Item.Path, outcome.Detail));
        }

        string movedSize = PathUtil.FormatBytes(report.MovedBytes);
        string message = $"Moved {moved.Count} item(s), {movedSize}, to quarantine." +
                         (notMoved.Count > 0 ? $" {notMoved.Count} item(s) stayed where they are; NotMoved says why." : "") +
                         " Use list_quarantine and restore_from_quarantine to put anything back.";
        env.Log.Info($"MCP: plan {plan.Id}: {message}");
        return new QuarantineRunResult(true, message, moved, notMoved, report.MovedBytes, movedSize);
    }
}
