/*
 * DiskSweep
 * Copyright (c) 2026 Miloš Milinkov. All rights reserved.
 * Proprietary and confidential. Use, copying, modification or distribution
 * without written permission is prohibited. See the LICENSE file.
 */

using DiskSweep.Core.Cleanup;
using DiskSweep.Core.IO;
using ModelContextProtocol;

namespace DiskSweep.Mcp;

public sealed record QuarantineListing(string Id, string OriginalPath, bool IsFolder, long SizeBytes, string Size,
    DateTime QuarantinedUtc, string State, bool CanRestore);

public sealed record RestoreOutcome(string Id, bool Restored, string Detail);

/// <summary>
/// Lists and restores DiskSweep's quarantine (items moved by this server or by the app). Restoring goes through
/// <see cref="QuarantineRestorer.Restore"/>: it never overwrites, never goes through a link and never goes into Windows.
/// </summary>
public sealed class QuarantineBrowser(SweepSession session)
{
    public IReadOnlyList<QuarantineListing> List() =>
        QuarantineStore.LoadAll(session.Env.QuarantineRoots())
            .SelectMany(batch => batch.Entries
                .Where(e => e.State is QuarantineState.Quarantined or QuarantineState.Pending)
                .Select(e => Listing(batch, e)))
            .ToList();

    public IReadOnlyList<RestoreOutcome> Restore(IReadOnlyList<string> entryIds)
    {
        var ids = (entryIds ?? []).Select(i => (i ?? "").Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        int max = session.Env.Limits.MaxRestoreEntries;
        if (ids.Count > max) throw new McpException($"At most {max} items can be restored per call.");

        // Read the journals fresh: the app (or an earlier call) may have restored or moved things since the listing.
        var batches = QuarantineStore.LoadAll(session.Env.QuarantineRoots());
        var restorer = new QuarantineRestorer(session.Env.Log, RefusingRecycleBin.Instance);
        var outcomes = new List<RestoreOutcome>();
        foreach (string id in ids)
        {
            if (!session.TryResolveQuarantineId(id, out string folder, out int slot))
            {
                outcomes.Add(new(id, false, $"Unknown quarantine id \"{id}\". Use ids from list_quarantine."));
                continue;
            }
            var batch = batches.FirstOrDefault(b => PathUtil.AreSame(b.Folder, folder));
            var entry = batch?.Entries.FirstOrDefault(e => e.Slot == slot);
            if (batch is null || entry is null)
            {
                outcomes.Add(new(id, false, "It's no longer in the quarantine folder."));
                continue;
            }

            string? error = restorer.Restore(batch, entry);
            outcomes.Add(error is null ? new(id, true, $"Restored to {entry.RestoredTo}.") : new(id, false, error));
        }
        session.Env.Log.Info($"MCP: restore of {ids.Count} item(s): {outcomes.Count(o => o.Restored)} restored.");
        return outcomes;
    }

    private QuarantineListing Listing(QuarantineBatch batch, QuarantineEntry e)
    {
        bool inQuarantine = QuarantineRestorer.IsInQuarantine(batch, e);
        string state = e.State == QuarantineState.Quarantined ? "In quarantine"
            : inQuarantine ? "In quarantine (the move was interrupted)"
            : "Interrupted before moving: still in its original place";
        return new QuarantineListing(session.QuarantineIdFor(batch.Folder, e.Slot), e.OriginalPath, e.IsDirectory, e.SizeBytes,
            PathUtil.FormatBytes(e.SizeBytes), e.QuarantinedUtc, state, inQuarantine);
    }
}
