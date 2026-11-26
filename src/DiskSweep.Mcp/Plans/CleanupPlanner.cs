/*
 * DiskSweep
 * Copyright (c) 2026 Miloš Milinkov. All rights reserved.
 * Proprietary and confidential. Use, copying, modification or distribution
 * without written permission is prohibited. See the LICENSE file.
 */

using DiskSweep.Core.Cleanup;
using DiskSweep.Core.IO;

namespace DiskSweep.Mcp;

public sealed record Refusal(string Id, string? Path, string Reason);

public sealed record PlannedItem(string Id, string Path, string Kind, string Risk, long SizeBytes);

public sealed record ProposalResult(string? PlanId, DateTimeOffset? ExpiresAtUtc, IReadOnlyList<PlannedItem> Items, long TotalBytes,
    string TotalSize, IReadOnlyList<Refusal> Refused, string NextStep);

/// <summary>
/// Turns item ids into a plan. Every item goes through the MCP plan rule and Core's pre-move check (selection policy,
/// safety guard, a fresh walk, "changed since the scan", "in use"); the plan caps apply on top. Nothing moves here.
/// </summary>
public sealed class CleanupPlanner(SweepSession session, PlanStore plans)
{
    public ProposalResult Propose(IReadOnlyList<string> itemIds, CancellationToken token = default)
    {
        var limits = session.Env.Limits;
        string capReason = $"Over the plan limit ({limits.MaxItems} items / {PathUtil.FormatBytes(limits.MaxBytes)}). Propose it in another plan.";
        var accepted = new List<(PlanEntry Entry, string Path)>();
        var refused = new List<Refusal>();
        var seen = new HashSet<string>(); // canonical ids, so "3-17" and "3-017" count once
        Core.Safety.SafetyGuard? planGuard = null;
        CleanupService? service = null;
        long total = 0;
        bool capReached = false;

        foreach (string id in itemIds ?? [])
        {
            token.ThrowIfCancellationRequested();
            if (!session.TryGetItem(id, out var found, out string error))
            {
                refused.Add(new(id, null, error));
                continue;
            }
            if (!seen.Add(found.Id)) continue;

            var item = found.Item;
            var guard = found.Scan.Result.Guard;
            capReached |= accepted.Count >= limits.MaxItems;
            string? why = session.StaleReason(found.Scan)
                ?? PlanItemRule.WhyNot(item)
                ?? (capReached ? capReason : null)
                ?? Overlap(found.Path, accepted)
                ?? (guard is null ? Texts.NoGuard : null);
            if (why is not null)
            {
                refused.Add(new(found.Id, item.Path, why));
                continue;
            }

            // TryGetItem only finds items of the latest scan, so one guard (and one service) serves the whole plan.
            planGuard ??= guard!;
            service ??= session.Env.NewCleanupService(planGuard);
            var preflight = service.Preflight(item);
            if (!preflight.CanMove)
            {
                refused.Add(new(found.Id, item.Path, preflight.Refusal!));
                continue;
            }
            if (total + preflight.SizeBytes > limits.MaxBytes)
            {
                capReached = true;
                refused.Add(new(found.Id, item.Path, capReason));
                continue;
            }

            accepted.Add((new PlanEntry(found.Id, item, preflight.SizeBytes), found.Path));
            total += preflight.SizeBytes;
        }

        if (accepted.Count == 0)
            return new ProposalResult(null, null, [], 0, PathUtil.FormatBytes(0), refused, "No plan was made; nothing will move.");

        var plan = plans.Issue(planGuard!, accepted.Select(a => a.Entry).ToList());
        session.Env.Log.Info($"MCP: plan {plan.Id}: {plan.Entries.Count} item(s), {PathUtil.FormatBytes(plan.TotalBytes)}; {refused.Count} refused.");
        return new ProposalResult(plan.Id, plan.ExpiresAt,
            plan.Entries.Select(e => new PlannedItem(e.ItemId, e.Item.Path, e.Item.Kind.ToString(), e.Item.Risk.ToString(), e.SizeBytes)).ToList(),
            plan.TotalBytes, PathUtil.FormatBytes(plan.TotalBytes), refused,
            "Nothing has moved. Show the user this plan. quarantine_plan will ask them to confirm in a Windows dialog.");
    }

    /// <summary>A plan never holds the same path twice, or a folder together with something inside it.</summary>
    private static string? Overlap(string path, List<(PlanEntry Entry, string Path)> accepted)
    {
        foreach (var (_, planned) in accepted)
        {
            if (PathUtil.AreSame(path, planned)) return $"{planned} is already in this plan.";
            if (PathUtil.IsStrictlyUnder(path, planned)) return $"Already covered by {planned}, which is in this plan.";
            if (PathUtil.IsStrictlyUnder(planned, path)) return $"Contains {planned}, which is already in this plan. Propose the folder on its own.";
        }
        return null;
    }
}
