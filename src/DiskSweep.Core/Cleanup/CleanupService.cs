/*
 * DiskSweep
 * Copyright (c) 2026 Miloš Milinkov. All rights reserved.
 * Proprietary and confidential. Use, copying, modification or distribution
 * without written permission is prohibited. See the LICENSE file.
 */

using DiskSweep.Core.IO;
using DiskSweep.Core.Logging;
using DiskSweep.Core.Models;
using DiskSweep.Core.Safety;
using DiskSweep.Core.Scanning;

namespace DiskSweep.Core.Cleanup;

public enum CleanupDestination { RecycleBin, Quarantine }

public enum CleanupStatus { Moved, Skipped, Failed }

public sealed record CleanupOutcome(ScanItem Item, CleanupStatus Status, string Detail, long Bytes = 0);

/// <summary>What the pre-move check says about one item: why it must stay put (or null), and its size right now.</summary>
public readonly record struct PreflightResult(string? Refusal, long SizeBytes)
{
    public bool CanMove => Refusal is null;
}

public sealed class CleanupReport
{
    public required CleanupDestination Destination { get; init; }
    public required IReadOnlyList<CleanupOutcome> Outcomes { get; init; }
    public bool WasCancelled { get; init; }

    public IEnumerable<CleanupOutcome> Moved => Outcomes.Where(o => o.Status == CleanupStatus.Moved);
    public IEnumerable<CleanupOutcome> NotMoved => Outcomes.Where(o => o.Status != CleanupStatus.Moved);
    public long MovedBytes => Moved.Sum(o => o.Bytes);
}

/// <summary>
/// Moves ticked files and folders to the Recycle Bin or to quarantine. Nothing is ever deleted permanently.
/// Every item is checked again right before it moves: the safety guard, a fresh walk for links, cloud files and
/// unreadable parts, whether it changed since the scan, and (for files) whether a program has it open.
/// </summary>
public sealed class CleanupService
{
    /// <summary>FAT drives store times in 2-second steps.</summary>
    private static readonly TimeSpan TimeTolerance = TimeSpan.FromSeconds(2);

    private readonly SafetyGuard _guard;
    private readonly IActionLog _log;
    private readonly QuarantineStore _quarantine;
    private readonly IRecycleBin _recycleBin;

    public CleanupService(SafetyGuard guard, IActionLog log, QuarantineStore? quarantine = null, IRecycleBin? recycleBin = null)
    {
        _guard = guard;
        _log = log;
        _quarantine = quarantine ?? new QuarantineStore();
        _recycleBin = recycleBin ?? new WindowsRecycleBin();
    }

    /// <summary>Moves the items one at a time, re-checking each right before it moves. Progress goes to <paramref name="status"/>.</summary>
    public CleanupReport Run(IReadOnlyList<ScanItem> items, CleanupDestination destination,
        ScanStatus? status = null, CancellationToken token = default)
    {
        string where = destination == CleanupDestination.RecycleBin ? "the Recycle Bin" : "quarantine";
        _log.Info($"Cleanup started: {items.Count} item(s) to {where}.");
        status?.Plan(1);
        status?.BeginPhase($"Moving items to {where}", 1);
        var outcomes = new List<CleanupOutcome>();
        bool cancelled = false;

        for (int i = 0; i < items.Count; i++)
        {
            if (token.IsCancellationRequested)
            {
                cancelled = true;
                foreach (var rest in items.Skip(i)) outcomes.Add(new CleanupOutcome(rest, CleanupStatus.Skipped, "Cleanup was stopped before this item."));
                break;
            }

            var item = items[i];
            if (status is not null)
            {
                status.CurrentPath = item.Path;
                status.SetPhaseProgress((double)i / items.Count);
            }

            var outcome = CleanOne(item, destination);
            outcomes.Add(outcome);
            status?.AddScanned();
            _log.Info($"Cleanup {outcome.Status.ToString().ToUpperInvariant()}: {item.Path} | {outcome.Detail}");
        }
        status?.Complete();

        var report = new CleanupReport { Destination = destination, Outcomes = outcomes, WasCancelled = cancelled };
        _log.Info($"Cleanup finished: {report.Moved.Count()} moved ({PathUtil.FormatBytes(report.MovedBytes)}), " +
                  $"{report.NotMoved.Count()} not moved{(cancelled ? ", stopped by the user" : "")}.");
        _log.Flush();
        return report;
    }

    /// <summary>
    /// Runs every check that happens right before a move, without moving anything, so a caller can show the
    /// same refusals the move would give. Never throws.
    /// </summary>
    public PreflightResult Preflight(ScanItem item)
    {
        try
        {
            string? why = CheckBeforeMove(item, out _, out long size);
            return new PreflightResult(why, why is null ? size : 0);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Error($"Pre-move check of {item.Path} failed unexpectedly", ex);
            return new PreflightResult($"Unexpected error while checking it: {ex.Message}", 0);
        }
    }

    /// <summary>One item; never throws, so a single odd item can't stop the rest of the run or lose its results.</summary>
    private CleanupOutcome CleanOne(ScanItem item, CleanupDestination destination)
    {
        try
        {
            return CleanOneUnguarded(item, destination);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Error($"Cleanup of {item.Path} failed unexpectedly", ex);
            return new CleanupOutcome(item, CleanupStatus.Failed, $"Unexpected error, left where it is: {ex.Message}");
        }
    }

    private CleanupOutcome CleanOneUnguarded(ScanItem item, CleanupDestination destination)
    {
        string? why = CheckBeforeMove(item, out string path, out long size);
        if (why is not null) return new CleanupOutcome(item, CleanupStatus.Skipped, why);

        if (destination == CleanupDestination.Quarantine) return MoveToQuarantine(item, path, size);

        var result = _recycleBin.Recycle(path, size);
        return result.Status switch
        {
            RecycleStatus.Recycled => new CleanupOutcome(item, CleanupStatus.Moved, result.Detail, size),
            RecycleStatus.Refused => new CleanupOutcome(item, CleanupStatus.Skipped, result.Detail),
            _ => new CleanupOutcome(item, CleanupStatus.Failed, result.Detail),
        };
    }

    private CleanupOutcome MoveToQuarantine(ScanItem item, string path, long size)
    {
        QuarantineBatch batch;
        QuarantineEntry entry;
        try
        {
            batch = _quarantine.BatchFor(path);
            entry = batch.Reserve(path, item.Kind == ItemKind.Folder, size);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new CleanupOutcome(item, CleanupStatus.Failed, $"The quarantine folder couldn't be prepared: {ex.Message}");
        }

        string target = batch.PathOf(entry);
        try
        {
            // Same drive, so this is a rename: all-or-nothing, nothing is copied.
            FileMoves.Move(path, target, entry.IsDirectory);
            entry.SetState(QuarantineState.Quarantined);
            batch.Record(entry);
            return new CleanupOutcome(item, CleanupStatus.Moved, $"Moved to quarantine: {target}", size);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            entry.SetState(QuarantineState.Failed, ex.Message);
            try { batch.Record(entry); }
            catch (Exception recordError) when (recordError is IOException or UnauthorizedAccessException)
            {
                _log.Error("Recording a failed quarantine move failed", recordError);
            }
            batch.RemoveEmptySlot(entry);
            return new CleanupOutcome(item, CleanupStatus.Failed, $"Couldn't move it (it may be in use): {ex.Message}");
        }
    }

    /// <summary>
    /// The last line of defence before anything moves. Returns why the item must stay put, or null when it may move.
    /// </summary>
    internal string? CheckBeforeMove(ScanItem item, out string path, out long size)
    {
        path = item.Path;
        size = 0;

        if (item.Kind is not (ItemKind.File or ItemKind.Folder))
            return "Only files and folders are moved; programs are removed with their own uninstaller.";

        // Report-only items, protected paths, links (the item or any parent), cloud files, folders known to contain links.
        var decision = SelectionPolicy.Evaluate(item, _guard);
        if (!decision.CanSelect) return decision.BlockedReason;

        if (!PathNormalizer.TryNormalize(item.Path, out path, out string error)) return error;
        if (FastDirectory.GetAttributes(path, out _) is not { } attributes) return "It no longer exists.";

        bool isDirectory = (attributes & FileAttributes.Directory) != 0;
        if (isDirectory != (item.Kind == ItemKind.Folder)) return "It changed from a file to a folder (or back) since the scan.";
        if (item.LastModifiedUtc is not { } scanned) return "There's no scan date to compare with. Scan again.";

        if (!isDirectory)
        {
            var info = new FileInfo(path);
            if (info.LastWriteTimeUtc > scanned + TimeTolerance || (item.SizeBytes is { } scannedSize && info.Length != scannedSize))
                return "It changed since the scan. Scan again to re-check it.";
            var state = FileLockProbe.Probe(path);
            if (state != LockState.Free) return FileLockProbe.Describe(state) + ".";
            size = info.Length;
            return null;
        }

        // Folders: walk everything inside again, never following links.
        var stats = FolderMeasure.Measure(path, CancellationToken.None);
        if (stats.FirstReparsePoint is not null) return $"It contains a junction, symbolic link or other reparse point: {stats.FirstReparsePoint}";
        if (stats.FirstCloudItem is not null) return $"It contains cloud files: {stats.FirstCloudItem}";
        if (stats.FirstUnreadable is not null) return $"Not everything inside can be read: {stats.FirstUnreadable}";
        if (stats.Truncated) return "It's too large to re-check completely.";
        if (item.Category == ScanCategory.EmptyFolders && stats.FileCount > 0) return "It's no longer empty.";

        if (stats.NewestChangeUtc > scanned + TimeTolerance) return "Something inside changed since the scan. Scan again to re-check it.";

        size = stats.TotalBytes;
        return null;
    }
}

internal static class FileMoves
{
    /// <summary>A rename on the same drive: the item ends up entirely in one place or the other.</summary>
    public static void Move(string from, string to, bool isDirectory)
    {
        if (isDirectory) Directory.Move(from, to);
        else File.Move(from, to);
    }
}
