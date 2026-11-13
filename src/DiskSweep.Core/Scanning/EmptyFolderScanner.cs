using DiskSweep.Core.IO;
using DiskSweep.Core.Models;

namespace DiskSweep.Core.Scanning;

/// <summary>
/// Finds folders that hold no files at all (empty subfolders allowed) and reports only the top-most one.
/// A folder counts as content when it is new, a link, unreadable or protected, so its parents are never reported.
/// </summary>
public sealed class EmptyFolderScanner : IScanner
{
    public ScanCategory Category => ScanCategory.EmptyFolders;
    public string PhaseName => "Looking for empty folders";
    public double Weight => 50;

    private sealed class Frame
    {
        public required FsEntry Self;
        public required List<FsEntry> Children;
        public int Next;
        public bool HasContent;
        public bool HiddenOrSystem;
        /// <summary>A parent holds program files (.exe, .dll…), so this is probably inside an application's folder.</summary>
        public bool InProgramFolder;
        /// <summary>This folder itself holds program files. Worked out once, when its listing is read.</summary>
        public bool HoldsPrograms;
        public readonly List<Frame> EligibleChildren = [];
    }

    private static bool HoldsProgramFiles(List<FsEntry> entries)
    {
        foreach (var e in entries)
            if (!e.IsDirectory && FileKinds.IsExecutable(e.Name)) return true;
        return false;
    }

    public void Scan(ScanContext ctx) => ScanTree(ctx, ctx.DriveRoot);

    internal void ScanTree(ScanContext ctx, string root)
    {
        var threshold = ctx.NowUtc.AddDays(-ctx.Options.EmptyFolderMinAgeDays);
        long usedBytes = Math.Max(1, ctx.Request.Drive.UsedBytes);
        long bytesSeen = 0;

        var rootChildren = new List<FsEntry>();
        if (!FastDirectory.TryList(root, rootChildren, out int rootError))
        {
            ctx.Skips.Add(root, FastDirectory.DescribeError(rootError));
            return;
        }

        var stack = new Stack<Frame>();
        stack.Push(new Frame
        {
            Self = new FsEntry(Path.GetFileName(root), root, FileAttributes.Directory, 0, DateTime.MinValue, DateTime.MinValue, DateTime.MinValue, 0),
            Children = rootChildren,
            HasContent = true, // the scan root itself is never reported
            // A drive root is never an app's folder (C:\ holds pagefile.sys and friends), so it doesn't mark its children.
            HoldsPrograms = root.Length > 3 && HoldsProgramFiles(rootChildren),
        });

        while (stack.Count > 0)
        {
            ctx.Token.ThrowIfCancellationRequested();
            var frame = stack.Peek();

            if (frame.Next < frame.Children.Count)
            {
                var e = frame.Children[frame.Next++];
                ctx.Status.AddScanned();

                if (!e.IsDirectory)
                {
                    frame.HasContent = true; // any file counts, including desktop.ini and hidden files
                    bytesSeen += e.Size;
                    continue;
                }

                if (ctx.IsTempRoot(e.FullPath) || ctx.ShouldSkipDirectory(e))
                {
                    frame.HasContent = true; // temp folders belong to the temp category; links and protected areas are content
                    continue;
                }
                if (ctx.Guard.IsInOsOrVendorFolder(e.FullPath))
                {
                    ctx.Skips.Add(e.FullPath, "Windows, Microsoft or hardware-vendor folder; its empty folders may be needed.");
                    frame.HasContent = true;
                    continue;
                }

                var children = new List<FsEntry>();
                if (!FastDirectory.TryList(e.FullPath, children, out int error))
                {
                    ctx.Skips.Add(e.FullPath, FastDirectory.DescribeError(error));
                    frame.HasContent = true;
                    continue;
                }

                ctx.Status.CurrentPath = e.FullPath;
                ctx.Status.SetPhaseProgress(Math.Min(0.98, (double)bytesSeen / usedBytes));
                stack.Push(new Frame
                {
                    Self = e,
                    Children = children,
                    HiddenOrSystem = e.IsHiddenOrSystem,
                    InProgramFolder = frame.InProgramFolder || frame.HoldsPrograms,
                    HoldsPrograms = HoldsProgramFiles(children),
                });
                continue;
            }

            stack.Pop();
            bool oldEnough = frame.Self.CreationUtc < threshold && frame.Self.LastWriteUtc < threshold;
            bool eligible = !frame.HasContent && oldEnough && ctx.Guard.CheckRules(frame.Self.FullPath).IsAllowed;

            if (stack.Count == 0)
            {
                foreach (var child in frame.EligibleChildren) Report(ctx, child);
                break;
            }

            var parent = stack.Peek();
            if (eligible)
            {
                parent.EligibleChildren.Add(frame);
            }
            else
            {
                parent.HasContent = true;
                foreach (var child in frame.EligibleChildren) Report(ctx, child);
            }
        }
    }

    private static void Report(ScanContext ctx, Frame f)
    {
        int folders = Count(f, out bool hiddenOrSystem);
        string path = f.Self.FullPath;

        var verdict = ctx.Guard.Check(path);
        if (!verdict.IsAllowed)
        {
            ctx.Skips.Add(path, $"Empty, but protected: {verdict.Reason}");
            return;
        }

        var evidence = new List<string>
        {
            folders == 1 ? "Contains no files and no subfolders." : $"Contains only {folders - 1} empty subfolder(s) and no files.",
            $"Created {f.Self.CreationUtc.ToLocalTime():d}, last changed {f.Self.LastWriteUtc.ToLocalTime():d}.",
        };

        RiskLevel risk = RiskLevel.Safe;
        if (hiddenOrSystem)
        {
            risk = RiskLevel.Review;
            evidence.Add("Review: it (or a folder inside it) is marked hidden or system.");
        }
        if (ctx.Guard.IsInAppArea(path))
        {
            risk = RiskLevel.Review;
            evidence.Add("Review: it's in Program Files, ProgramData or AppData, where apps sometimes expect empty folders to exist.");
        }
        else if (PathUtil.Segments(path).Any(s => s.StartsWith('.')))
        {
            risk = RiskLevel.Review;
            evidence.Add("Review: it's inside a folder whose name starts with a dot, which is where tools keep their settings.");
        }
        else if (f.InProgramFolder)
        {
            risk = RiskLevel.Review;
            evidence.Add("Review: it's inside a folder that contains programs (.exe or .dll files), so an app may expect it.");
        }
        else if (ctx.IsSystemDrive && !ctx.Guard.IsInUserProfile(path))
        {
            risk = RiskLevel.Review;
            evidence.Add("Review: it's on the system drive outside your user folders, where Windows and hardware vendors keep their own folders.");
        }

        ctx.Add(new ScanItem
        {
            Category = ScanCategory.EmptyFolders,
            Kind = ItemKind.Folder,
            Path = path,
            DisplayName = f.Self.Name,
            SizeBytes = 0,
            LastModifiedUtc = f.Self.LastWriteUtc,
            Reason = folders == 1 ? "Empty folder" : $"Empty folder tree ({folders} folders)",
            Risk = risk,
            Evidence = evidence,
        });
    }

    private static int Count(Frame f, out bool hiddenOrSystem)
    {
        hiddenOrSystem = f.HiddenOrSystem;
        int total = 1;
        foreach (var c in f.EligibleChildren)
        {
            total += Count(c, out bool h);
            hiddenOrSystem |= h;
        }
        return total;
    }
}
