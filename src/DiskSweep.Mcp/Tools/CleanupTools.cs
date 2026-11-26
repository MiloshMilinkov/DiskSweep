/*
 * DiskSweep
 * Copyright (c) 2026 Miloš Milinkov. All rights reserved.
 * Proprietary and confidential. Use, copying, modification or distribution
 * without written permission is prohibited. See the LICENSE file.
 */

using System.ComponentModel;
using ModelContextProtocol.Server;

namespace DiskSweep.Mcp.Tools;

/// <summary>Proposing a cleanup (changes nothing) and carrying out an approved plan (moves into quarantine only).</summary>
[McpServerToolType]
public sealed class CleanupTools(CleanupPlanner planner, PlanExecutor executor)
{
    [McpServerTool(Name = "propose_cleanup", Title = "Propose a cleanup plan", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Builds a cleanup plan from item ids returned by list_candidates. Re-runs every safety check and returns a plan id, " +
                 "the total size, and each refused item with the reason. Only Safe and Review files and folders qualify, at most 100 items " +
                 "and 10 GB per plan, and the plan expires after 10 minutes. Show the plan to the user before calling quarantine_plan. " +
                 "Can't: move or delete anything, take paths, include Risky items, programs, registry entries or protected paths, " +
                 "or use items from a scan older than 60 minutes.")]
    public ProposalResult ProposeCleanup([Description("Item ids from list_candidates, such as [\"3-17\", \"3-18\"].")] string[] itemIds) =>
        planner.Propose(itemIds);

    [McpServerTool(Name = "quarantine_plan", Title = "Move an approved plan to quarantine", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Moves the items of one plan from propose_cleanup into DiskSweep's quarantine on the same drive, after the user clicks " +
                 "Yes in a Windows confirmation dialog that DiskSweep shows. Every item is checked again right before it moves, and " +
                 "anything that changed stays where it is. Everything moved can be put back with restore_from_quarantine or the DiskSweep app. " +
                 "Can't: delete anything, use the Recycle Bin, skip the user's confirmation, take paths or item ids, use a plan twice, " +
                 "use a plan older than 10 minutes, or use a plan from another session.")]
    public Task<QuarantineRunResult> QuarantinePlan(
        [Description("The planId returned by propose_cleanup.")] string planId,
        McpServer server,
        CancellationToken cancellationToken) =>
        // The confirmation dialog blocks until the person answers, so it gets its own thread.
        Task.Run(() => executor.Execute(planId, server.ClientInfo?.Name ?? "An MCP client", cancellationToken), CancellationToken.None);
}
