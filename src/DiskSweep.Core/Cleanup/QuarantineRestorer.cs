using DiskSweep.Core.IO;
using DiskSweep.Core.Logging;

namespace DiskSweep.Core.Cleanup;

/// <summary>Puts quarantined items back, or sends them on to the Recycle Bin. Never overwrites and never deletes permanently.</summary>
public sealed class QuarantineRestorer
{
    private readonly IActionLog _log;
    private readonly IRecycleBin _recycleBin;

    public QuarantineRestorer(IActionLog log, IRecycleBin? recycleBin = null)
    {
        _log = log;
        _recycleBin = recycleBin ?? new WindowsRecycleBin();
    }

    /// <summary>Moves the item back to its original path. Returns an error, or null on success.</summary>
    public string? Restore(QuarantineBatch batch, QuarantineEntry entry)
    {
        if (WhyNotActionable(batch, entry, out string stored) is { } notActionable) return notActionable;

        if (PathUtil.NormalizeLoose(entry.OriginalPath) is not { } original || original.Length <= 3)
            return "The original location isn't a valid path.";
        if (!PathUtil.IsOnDrive(original, batch.Folder))
            return $"It came from another drive letter ({PathUtil.DriveRoot(original)}). Move it back by hand from {stored}.";
        // The journal is an ordinary file, so its "original location" is never trusted to point into Windows itself.
        if (WhyNotRestoreTarget(original) is { } badTarget) return badTarget;

        // Never restore through a link: a junction put in place of a parent folder would send the item somewhere else.
        string parent = PathUtil.Parent(original) ?? PathUtil.DriveRoot(original);
        for (string? folder = parent; folder is not null && folder.Length > 3; folder = PathUtil.Parent(folder))
        {
            if (FastDirectory.GetAttributes(folder, out _) is { } a &&
                (FileAttributeFlags.IsReparsePoint(a) || FileAttributeFlags.IsCloudManaged(a)))
                return $"A folder on the way back is now a link or a cloud folder: {folder}. Move it back by hand from {stored}.";
        }

        try
        {
            Directory.CreateDirectory(parent);
            string target = FreeName(original, entry.IsDirectory);
            FileMoves.Move(stored, target, entry.IsDirectory);
            entry.RestoredTo = target;
            Finish(batch, entry, QuarantineState.Restored);
            _log.Info($"Restored from quarantine: {stored} → {target}");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error($"Restore failed: {stored}", ex);
            return $"Couldn't move it back: {ex.Message}";
        }
    }

    /// <summary>Sends a quarantined item to the Recycle Bin (only when the Bin can really take it). Returns an error, or null.</summary>
    public string? SendToRecycleBin(QuarantineBatch batch, QuarantineEntry entry)
    {
        if (WhyNotActionable(batch, entry, out string stored) is { } notActionable) return notActionable;

        var result = _recycleBin.Recycle(stored, entry.SizeBytes);
        if (result.Status != RecycleStatus.Recycled) return result.Detail;

        Finish(batch, entry, QuarantineState.Recycled);
        _log.Info($"Moved from quarantine to the Recycle Bin: {stored} (originally {entry.OriginalPath})");
        return null;
    }

    private static string? WhyNotRestoreTarget(string original)
    {
        string windows = PathUtil.NormalizeLoose(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\Windows";
        if (PathUtil.IsSameOrUnder(original, windows) && !PathUtil.IsStrictlyUnder(original, Path.Join(windows, "Temp")))
            return "Its recorded original location is inside Windows. DiskSweep never restores into Windows; move it by hand if you're sure.";
        if (PathUtil.Segments(original).Any(s => s.Equals("System32", PathUtil.Comparison) ||
                                                  s.Equals("SysWOW64", PathUtil.Comparison) ||
                                                  s.Equals("WinSxS", PathUtil.Comparison)))
            return "Its recorded original location is a Windows system folder. Move it by hand if you're sure.";
        return null;
    }

    /// <summary>
    /// True when the item is sitting in the quarantine folder. That includes "Pending" entries whose move finished
    /// but whose journal line didn't (a crash right after the move).
    /// </summary>
    public static bool IsInQuarantine(QuarantineBatch batch, QuarantineEntry entry) =>
        entry.State is QuarantineState.Quarantined or QuarantineState.Pending && FastDirectory.Exists(batch.PathOf(entry)) == true;

    private static string? WhyNotActionable(QuarantineBatch batch, QuarantineEntry entry, out string stored)
    {
        stored = batch.PathOf(entry);
        if (!IsInQuarantine(batch, entry)) return "It's no longer in the quarantine folder.";

        // Same rule as every other move: never move a link, or a folder with a link inside.
        if (FastDirectory.GetAttributes(stored, out _) is not { } a) return "It can't be read.";
        if (FileAttributeFlags.IsReparsePoint(a)) return "It's a junction or symbolic link now. Move it by hand.";
        if (entry.IsDirectory && FolderMeasure.Measure(stored, CancellationToken.None).FirstReparsePoint is { } link)
            return $"It contains a junction or symbolic link now ({link}). Move it by hand.";
        return null;
    }

    private static void Finish(QuarantineBatch batch, QuarantineEntry entry, QuarantineState state)
    {
        entry.SetState(state);
        batch.Record(entry);
        batch.RemoveEmptySlot(entry);
    }

    /// <summary>The original path, or "name (restored).ext", "name (restored 2).ext"… when something already exists there.</summary>
    internal static string FreeName(string original, bool isDirectory)
    {
        if (FastDirectory.Exists(original) == false) return original;
        string folder = Path.GetDirectoryName(original)!;
        string name = isDirectory ? Path.GetFileName(original) : Path.GetFileNameWithoutExtension(original);
        string extension = isDirectory ? "" : Path.GetExtension(original);
        for (int n = 1; ; n++)
        {
            string candidate = Path.Join(folder, $"{name} (restored{(n == 1 ? "" : $" {n}")}){extension}");
            if (FastDirectory.Exists(candidate) == false) return candidate;
        }
    }
}
