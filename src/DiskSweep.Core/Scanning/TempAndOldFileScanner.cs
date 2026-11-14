using DiskSweep.Core.IO;
using DiskSweep.Core.Models;

namespace DiskSweep.Core.Scanning;

/// <summary>
/// Temp files older than 2 days (Safe), plus large files and old Downloads files (always Review, never pre-ticked).
/// </summary>
public sealed class TempAndOldFileScanner : IScanner
{
    private const int MaxLockChecksPerFolder = 2_000;

    public ScanCategory Category => ScanCategory.TempAndOldFiles;
    public string PhaseName => "Looking for temp, large and old files";
    public double Weight => 25;

    public void Scan(ScanContext ctx)
    {
        var tempRoots = ctx.Snapshot.Layout.TempDirectories.Where(ctx.IsOnDrive).Distinct(PathUtil.Comparer).ToList();
        for (int i = 0; i < tempRoots.Count; i++)
        {
            ctx.Status.SetPhaseProgress(0.3 * i / Math.Max(1, tempRoots.Count));
            ScanTempRoot(ctx, tempRoots[i]);
        }

        ctx.Status.SetPhaseProgress(0.3);
        ScanLargeAndOld(ctx);
    }

    private static void ScanTempRoot(ScanContext ctx, string root)
    {
        var threshold = ctx.NowUtc.AddDays(-ctx.Options.TempMinAgeDays);
        var entries = new List<FsEntry>();
        if (!FastDirectory.TryList(root, entries, out int error))
        {
            ctx.Skips.Add(root, FastDirectory.DescribeError(error) + (error == Native.NativeMethods.ERROR_ACCESS_DENIED ? " (run as administrator to include it)." : "."));
            return;
        }

        foreach (var e in entries)
        {
            ctx.Token.ThrowIfCancellationRequested();
            ctx.Status.CurrentPath = e.FullPath;
            ctx.Status.AddScanned();

            if (ctx.SkipIfLinkOrCloud(e)) continue;
            if (e.NewestChangeUtc >= threshold) continue; // recent: probably still in use

            if (e.IsDirectory)
            {
                if (PassesSafetyCheck(ctx, e.FullPath)) ReportTempFolder(ctx, e, threshold);
            }
            else if (PassesSafetyCheck(ctx, e.FullPath) && PassesInUseCheck(ctx, e.FullPath))
            {
                ReportTempFile(ctx, e);
            }
        }
    }

    private static bool PassesSafetyCheck(ScanContext ctx, string path)
    {
        var verdict = ctx.Guard.Check(path);
        if (!verdict.IsAllowed) ctx.Skips.Add(path, verdict.Reason);
        return verdict.IsAllowed;
    }

    private static bool PassesInUseCheck(ScanContext ctx, string path)
    {
        var state = FileLockProbe.Probe(path);
        if (state != LockState.Free) ctx.Skips.Add(path, FileLockProbe.Describe(state));
        return state == LockState.Free;
    }

    private static void ReportTempFile(ScanContext ctx, in FsEntry e)
    {
        int age = ctx.AgeDays(e.NewestChangeUtc);
        ctx.Add(new ScanItem
        {
            Category = ScanCategory.TempAndOldFiles,
            Kind = ItemKind.File,
            Path = e.FullPath,
            DisplayName = e.Name,
            SizeBytes = e.Size,
            LastModifiedUtc = e.LastWriteUtc,
            Reason = $"Temp file, unchanged for {age} days",
            Risk = RiskLevel.Safe,
            Evidence = [$"In a temp folder and not changed since {e.NewestChangeUtc.ToLocalTime():d}.", "Not open in any program right now."],
        });
    }

    private static void ReportTempFolder(ScanContext ctx, in FsEntry e, DateTime threshold)
    {
        var filesToCheck = new List<string>();
        var stats = FolderMeasure.Measure(e.FullPath, ctx.Token, onFile: file =>
        {
            if (filesToCheck.Count < MaxLockChecksPerFolder) filesToCheck.Add(file);
        });

        // Cheap reasons to leave the folder alone first; the in-use check opens files, so it runs last.
        if (stats.FirstReparsePoint is not null) { ctx.Skips.Add(e.FullPath, $"Contains a link, so it's left alone: {stats.FirstReparsePoint}"); return; }
        if (!stats.IsFullyReadable) { ctx.Skips.Add(e.FullPath, $"Not everything inside can be read: {stats.FirstUnreadable ?? "too many files"}"); return; }
        if (stats.FirstCloudItem is not null) { ctx.Skips.Add(e.FullPath, $"Contains cloud files: {stats.FirstCloudItem}"); return; }
        if (stats.NewestChangeUtc >= threshold) return; // something inside changed recently

        foreach (string file in filesToCheck)
        {
            var state = FileLockProbe.Probe(file);
            if (state == LockState.Free) continue;
            ctx.Skips.Add(e.FullPath, $"{FileLockProbe.Describe(state)}: {file}");
            return;
        }

        DateTime newest = stats.NewestChangeUtc; // includes the folder's own times
        int age = ctx.AgeDays(newest);
        bool allChecked = stats.FileCount <= MaxLockChecksPerFolder;

        var evidence = new List<string>
        {
            $"In a temp folder; nothing inside changed since {newest.ToLocalTime():d}.",
            $"{stats.FileCount:N0} files in {stats.FolderCount:N0} folders.",
            allChecked ? "No file inside is open in any program right now." : $"Only the first {MaxLockChecksPerFolder:N0} files were checked for being in use.",
        };

        ctx.Add(new ScanItem
        {
            Category = ScanCategory.TempAndOldFiles,
            Kind = ItemKind.Folder,
            Path = e.FullPath,
            DisplayName = e.Name,
            SizeBytes = stats.TotalBytes,
            LastModifiedUtc = newest,
            Reason = $"Temp folder, unchanged for {age} days",
            Risk = allChecked ? RiskLevel.Safe : RiskLevel.Review,
            Evidence = evidence,
        });
    }

    private static void ScanLargeAndOld(ScanContext ctx)
    {
        var profiles = ctx.Snapshot.Profiles.Users;
        var downloads = profiles.Select(u => u.DownloadsFolder).ToList();

        // System drive: your personal folders. Other drives: the whole drive (minus protected and cloud folders).
        var roots = ctx.IsSystemDrive
            ? profiles.SelectMany(u => u.PersonalFolders).Where(ctx.IsOnDrive).Distinct(PathUtil.Comparer).ToList()
            : [ctx.DriveRoot];
        roots = roots.Where(r => !roots.Any(o => !PathUtil.AreSame(o, r) && PathUtil.IsStrictlyUnder(r, o))).ToList();

        var oldThreshold = ctx.NowUtc.AddDays(-ctx.Options.OldFileDays);
        var buffer = new List<FsEntry>();

        for (int r = 0; r < roots.Count; r++)
        {
            var stack = new Stack<string>();
            stack.Push(roots[r]);
            while (stack.Count > 0)
            {
                ctx.Token.ThrowIfCancellationRequested();
                string dir = stack.Pop();
                if (!FastDirectory.TryList(dir, buffer, out int error))
                {
                    if (error != Native.NativeMethods.ERROR_PATH_NOT_FOUND) ctx.Skips.Add(dir, FastDirectory.DescribeError(error));
                    continue;
                }
                ctx.Status.CurrentPath = dir;
                ctx.Status.SetPhaseProgress(0.3 + 0.7 * r / roots.Count);

                foreach (var e in buffer)
                {
                    ctx.Status.AddScanned();
                    if (e.IsDirectory)
                    {
                        if (!ctx.IsTempRoot(e.FullPath) && !ctx.ShouldSkipDirectory(e)) stack.Push(e.FullPath);
                        continue;
                    }

                    bool large = e.Size >= ctx.Options.LargeFileBytes;
                    bool old = e.NewestChangeUtc < oldThreshold && IsUnderAny(e.FullPath, downloads);
                    if (!large && !old) continue;

                    if (ctx.SkipIfLinkOrCloud(e) || !PassesSafetyCheck(ctx, e.FullPath) || !PassesInUseCheck(ctx, e.FullPath)) continue;

                    int age = ctx.AgeDays(e.NewestChangeUtc);
                    var reasons = new List<string>();
                    if (large) reasons.Add($"Large file ({PathUtil.FormatBytes(e.Size)})");
                    if (old) reasons.Add($"not changed in {age} days");
                    string reason = string.Join(", ", reasons);
                    reason = char.ToUpperInvariant(reason[0]) + reason[1..];

                    ctx.Add(new ScanItem
                    {
                        Category = ScanCategory.TempAndOldFiles,
                        Kind = ItemKind.File,
                        Path = e.FullPath,
                        DisplayName = e.Name,
                        SizeBytes = e.Size,
                        LastModifiedUtc = e.LastWriteUtc,
                        Reason = reason,
                        Risk = RiskLevel.Review,
                        Evidence =
                        [
                            large ? $"Bigger than {PathUtil.FormatBytes(ctx.Options.LargeFileBytes)}." : "In your Downloads folder.",
                            $"Last changed {e.NewestChangeUtc.ToLocalTime():d}.",
                            "Your own file: check it before removing. Never ticked automatically.",
                        ],
                    });
                }
            }
        }
    }

    private static bool IsUnderAny(string path, List<string> roots)
    {
        foreach (string root in roots)
            if (PathUtil.IsStrictlyUnder(path, root)) return true;
        return false;
    }
}
