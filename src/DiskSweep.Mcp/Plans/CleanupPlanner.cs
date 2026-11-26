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
    public ProposalResult Propose(IReadOnlyList<string> itemIds)
    {
        var limits = session.Env.Limits;
        var accepted = new List<(PlanEntry Entry, string Path)>();
        var refused = new List<Refusal>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CleanupService? service = null;
        Core.Safety.SafetyGuard? guard = null;
        long total = 0;
        string? capReached = null;

        foreach (string raw in itemIds ?? [])
        {
            string id = (raw ?? "").Trim();
            if (!seen.Add(id)) continue;

            if (!session.TryGetItem(id, out var scan, out var item, out string error))
            {
                refused.Add(new(id, null, error));
                continue;
            }
            string? why = session.IsStale(scan)
                ? $"The scan is older than {limits.MaxScanAge.TotalMinutes:0} minutes. Scan again."
                : PlanItemRule.WhyNot(item) ?? capReached;
            string path = SweepSession.Normalized(item.Path);
            why ??= Overlap(path, accepted);
            if (why is null && scan.Result.Guard is null) why = "This scan has no safety guard. Scan again.";
            if (why is not null)
            {
                refused.Add(new(id, item.Path, why));
                continue;
            }

            guard ??= scan.Result.Guard!;
            service ??= session.Env.NewCleanupService(guard);
            var preflight = service.Preflight(item);
            if (!preflight.CanMove)
            {
                refused.Add(new(id, item.Path, preflight.Refusal!));
                continue;
            }
            if (accepted.Count + 1 > limits.MaxItems || total + preflight.SizeBytes > limits.MaxBytes)
            {
                capReached = $"Over the plan limit ({limits.MaxItems} items / {PathUtil.FormatBytes(limits.MaxBytes)}). Propose it in another plan.";
                refused.Add(new(id, item.Path, capReached));
                continue;
            }

            accepted.Add((new PlanEntry(id, item, preflight.SizeBytes), path));
            total += preflight.SizeBytes;
        }

        if (accepted.Count == 0)
            return new ProposalResult(null, null, [], 0, CandidateItem.SizeText(0), refused, "No plan was made; nothing will move.");

        var plan = plans.Issue(guard!, accepted.Select(a => a.Entry).ToList());
        session.Env.Log.Info($"MCP: plan {plan.Id}: {plan.Entries.Count} item(s), {PathUtil.FormatBytes(total)}; {refused.Count} refused.");
        return new ProposalResult(plan.Id, plan.ExpiresAt,
            plan.Entries.Select(e => new PlannedItem(e.ItemId, e.Item.Path, e.Item.Kind.ToString(), e.Item.Risk.ToString(), e.SizeBytes)).ToList(),
            total, CandidateItem.SizeText(total), refused,
            "Nothing has moved. Show the user this plan. quarantine_plan will ask them to confirm in a Windows dialog.");
    }

    /// <summary>A plan never holds a folder together with something inside it, so nothing is counted (or moved) twice.</summary>
    private static string? Overlap(string path, List<(PlanEntry Entry, string Path)> accepted)
    {
        foreach (var (_, planned) in accepted)
        {
            if (PathUtil.IsStrictlyUnder(path, planned)) return $"Already covered by {planned}, which is in this plan.";
            if (PathUtil.IsStrictlyUnder(planned, path)) return $"Contains {planned}, which is already in this plan. Propose the folder on its own.";
        }
        return null;
    }
}
