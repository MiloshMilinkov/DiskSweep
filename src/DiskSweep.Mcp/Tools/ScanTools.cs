/*
 * DiskSweep
 * Copyright (c) 2026 Miloš Milinkov. All rights reserved.
 * Proprietary and confidential. Use, copying, modification or distribution
 * without written permission is prohibited. See the LICENSE file.
 */

using System.ComponentModel;
using DiskSweep.Core.Models;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace DiskSweep.Mcp.Tools;

/// <summary>The read-only tools. None of them changes anything on disk.</summary>
[McpServerToolType]
public sealed class ScanTools(SweepSession session)
{
    private const int MaxPage = 200;

    [McpServerTool(Name = "scan", Title = "Scan a drive or folder", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Scans one drive (\"C:\") or one folder for clutter: empty folders, program leftovers, unused programs, and old temp, " +
                 "large and downloaded files. Returns counts and sizes by category and risk level; call list_candidates for the items. " +
                 "A folder scan scans its whole drive and keeps what is under that folder, so it takes as long as a drive scan (often 15–60 s). " +
                 "Can't: change, move or delete anything, scan network drives, or look inside protected places (Windows, installed programs, " +
                 "cloud-synced folders, repositories, links).")]
    public async Task<ScanSummary> Scan(
        [Description("A drive letter such as \"C:\", or a full folder path such as \"C:\\Users\\me\\Downloads\".")] string target,
        [Description("Optional. Any of EmptyFolders, Leftovers, UnusedPrograms, TempAndOldFiles. Default: all.")] string[]? categories = null,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var scan = await session.ScanAsync(target, categories, progress, cancellationToken);
        var notes = scan.Result.Notes.ToList();
        if (scan.Folder is not null)
            notes.Add($"Scanned all of {scan.Result.Request.Drive.RootPath} and kept the {scan.Items.Count} item(s) under {scan.Folder}.");

        var breakdown = scan.Items
            .GroupBy(i => (i.Category, i.Risk))
            .OrderBy(g => g.Key.Category).ThenBy(g => g.Key.Risk)
            .Select(g => new CategoryRiskCount(g.Key.Category.ToString(), g.Key.Risk.ToString(), g.Count(), g.Sum(i => i.SizeBytes ?? 0)))
            .ToList();
        long total = scan.Items.Sum(i => i.SizeBytes ?? 0);
        return new ScanSummary(scan.Number, scan.Target, scan.Items.Count, total, Core.IO.PathUtil.FormatBytes(total), breakdown, notes,
            scan.Result.SkippedTotal, Math.Round(scan.Result.Duration.TotalSeconds, 1), Texts.DataNote);
    }

    [McpServerTool(Name = "list_candidates", Title = "List scan results", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Lists items from the latest scan, largest first, with an id, path, size, risk level (Safe, Review or Risky), the reason " +
                 "DiskSweep flagged it, and whether a cleanup plan could take it. Filter by category, risk or folder. " +
                 "Can't: change anything, or list items from earlier scans.")]
    public CandidatePage ListCandidates(
        [Description("Optional: EmptyFolders, Leftovers, OrphanedRegistry, UnusedPrograms or TempAndOldFiles.")] string? category = null,
        [Description("Optional: Safe, Review or Risky.")] string? risk = null,
        [Description("Optional: only items inside this folder.")] string? under = null,
        [Description("How many to return (default 50, at most 200).")] int limit = 50,
        [Description("How many to skip, for paging.")] int offset = 0)
    {
        var scan = session.Latest ?? throw new McpException(Texts.NoScan);
        var c = SweepSession.ParseEnum<ScanCategory>(category, "category");
        var r = SweepSession.ParseEnum<RiskLevel>(risk, "risk");
        string? folder = string.IsNullOrWhiteSpace(under) ? null : SweepSession.Normalized(under);
        offset = Math.Max(0, offset);

        var matches = scan.Items
            .Select((item, index) => (item, index))
            .Where(x => (c is null || x.item.Category == c) && (r is null || x.item.Risk == r) &&
                        (folder is null || scan.IsUnder(x.index, folder)))
            .OrderByDescending(x => x.item.SizeBytes ?? -1)
            .ToList();
        var page = matches.Skip(offset).Take(Math.Clamp(limit, 1, MaxPage))
            .Select(x => CandidateItem.From(scan.IdOf(x.index), x.item))
            .ToList();
        return new CandidatePage(matches.Count, offset, page, Texts.DataNote);
    }

    [McpServerTool(Name = "explain_item", Title = "Explain one item", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Explains one item from the latest scan: why it was flagged, the evidence, and each safety check it passes or fails " +
                 "right now. Can't: change anything.")]
    public ItemExplanation ExplainItem([Description("An item id from list_candidates, such as \"3-17\".")] string itemId)
    {
        if (!session.TryGetItem(itemId, out var found, out string error)) throw new McpException(error);

        var item = found.Item;
        var checks = SafetyChecks.Run(session, found);
        return new ItemExplanation(CandidateItem.From(found.Id, item), item.Confidence, item.Evidence, item.LastUsedUtc,
            item.LastUsedSource, item.LastModifiedUtc, checks, checks.All(x => x.Passed));
    }
}
