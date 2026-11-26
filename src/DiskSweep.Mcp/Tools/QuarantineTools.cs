/*
 * DiskSweep
 * Copyright (c) 2026 Miloš Milinkov. All rights reserved.
 * Proprietary and confidential. Use, copying, modification or distribution
 * without written permission is prohibited. See the LICENSE file.
 */

using System.ComponentModel;
using DiskSweep.Core.IO;
using ModelContextProtocol.Server;

namespace DiskSweep.Mcp.Tools;

public sealed record QuarantineList(int Count, long TotalBytes, string TotalSize, IReadOnlyList<QuarantineListing> Entries, string DataNote);

public sealed record RestoreReport(int Restored, IReadOnlyList<RestoreOutcome> Outcomes);

/// <summary>Looking at and undoing what DiskSweep put in quarantine.</summary>
[McpServerToolType]
public sealed class QuarantineTools(QuarantineBrowser browser)
{
    [McpServerTool(Name = "list_quarantine", Title = "List quarantine", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Lists what DiskSweep has moved into quarantine on every drive, by this server or by the DiskSweep app, with an id for " +
                 "restore_from_quarantine. Can't: change anything, or show items already restored or moved on to the Recycle Bin.")]
    public QuarantineList ListQuarantine()
    {
        var entries = browser.List();
        long total = entries.Sum(e => e.SizeBytes);
        return new QuarantineList(entries.Count, total, PathUtil.FormatBytes(total), entries, Texts.DataNote);
    }

    [McpServerTool(Name = "restore_from_quarantine", Title = "Restore from quarantine", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Puts quarantined items back where they came from, using ids from list_quarantine. If something now exists at the " +
                 "original path, the item comes back as \"name (restored)\". Can't: overwrite anything, restore into Windows or through " +
                 "a link, delete anything, or restore more than 100 items per call.")]
    public RestoreReport RestoreFromQuarantine([Description("Ids from list_quarantine, such as [\"q1\", \"q2\"].")] string[] entryIds)
    {
        var outcomes = browser.Restore(entryIds);
        return new RestoreReport(outcomes.Count(o => o.Restored), outcomes);
    }
}
