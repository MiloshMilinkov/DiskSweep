/*
 * DiskSweep
 * Copyright (c) 2026 Miloš Milinkov. All rights reserved.
 * Proprietary and confidential. Use, copying, modification or distribution
 * without written permission is prohibited. See the LICENSE file.
 */

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using DiskSweep.Core.Models;
using DiskSweep.Core.Safety;

namespace DiskSweep.Mcp;

/// <summary>One item in a plan, with its size as the pre-move check measured it.</summary>
public sealed record PlanEntry(string ItemId, ScanItem Item, long SizeBytes);

/// <summary>A proposed cleanup. Only <see cref="PlanStore"/> makes these, and each can be taken once.</summary>
public sealed record CleanupPlan(string Id, SafetyGuard Guard, IReadOnlyList<PlanEntry> Entries, DateTimeOffset ExpiresAt)
{
    public long TotalBytes => Entries.Sum(e => e.SizeBytes);
}

/// <summary>
/// Plans issued in this server session, in memory only. An id is a random 128-bit value, a plan expires after
/// <see cref="PlanLimits.PlanLifetime"/>, and taking a plan uses it up whatever happens next.
/// </summary>
public sealed class PlanStore(SweepEnvironment env)
{
    private const string NotIssued = "This plan id was not issued by this DiskSweep session.";
    private const string Used = "This plan was already used. Propose a new one.";

    private readonly ConcurrentDictionary<string, CleanupPlan> _open = new();
    private readonly ConcurrentDictionary<string, string> _closed = new();

    private string Expired => $"This plan expired (plans last {env.Limits.PlanLifetime.TotalMinutes:0} minutes). Propose a new one.";

    public CleanupPlan Issue(SafetyGuard guard, IReadOnlyList<PlanEntry> entries)
    {
        var now = env.Clock.GetUtcNow();
        foreach (var old in _open.Values.Where(p => now >= p.ExpiresAt).ToList())
            if (_open.TryRemove(old.Id, out _)) _closed[old.Id] = Expired;

        var plan = new CleanupPlan(Guid.NewGuid().ToString("N"), guard, entries, now + env.Limits.PlanLifetime);
        _open[plan.Id] = plan;
        return plan;
    }

    /// <summary>Takes the plan out of the store (atomically, so two calls can't both get it). Returns why not otherwise.</summary>
    public bool TryTake(string planId, [NotNullWhen(true)] out CleanupPlan? plan, out string refusal)
    {
        string id = (planId ?? "").Trim();
        if (_open.TryRemove(id, out plan))
        {
            if (env.Clock.GetUtcNow() < plan.ExpiresAt)
            {
                _closed[id] = Used;
                refusal = "";
                return true;
            }
            _closed[id] = refusal = Expired;
            plan = null;
            return false;
        }
        refusal = _closed.TryGetValue(id, out string? why) ? why : NotIssued;
        return false;
    }
}
