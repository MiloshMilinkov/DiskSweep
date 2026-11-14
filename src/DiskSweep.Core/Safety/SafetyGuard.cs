/*
 * DiskSweep
 * Copyright (c) 2026 Miloš Milinkov. All rights reserved.
 * Proprietary and confidential. Use, copying, modification or distribution
 * without written permission is prohibited. See the LICENSE file.
 */

using DiskSweep.Core.IO;

namespace DiskSweep.Core.Safety;

public readonly record struct SafetyVerdict(bool IsAllowed, string Reason)
{
    public static readonly SafetyVerdict Allowed = new(true, "");
    public static SafetyVerdict Blocked(string reason) => new(false, reason);
}

/// <summary>Reads attributes without following links. Swappable so the rules can be tested without touching C:\.</summary>
public interface IFileAttributeProbe
{
    /// <summary>The path's own attributes (a link's, not its target's), or null when it doesn't exist or can't be read.</summary>
    FileAttributes? GetAttributes(string path);
}

public sealed class Win32FileAttributeProbe : IFileAttributeProbe
{
    public static readonly Win32FileAttributeProbe Instance = new();
    public FileAttributes? GetAttributes(string path) => FastDirectory.GetAttributes(path, out _);
}

/// <summary>
/// The single gate every selectable or removable path must pass. It is deliberately strict: when in doubt, it blocks.
/// Hard-coded rules can't be switched off by settings or by the user.
/// </summary>
public sealed class SafetyGuard
{
    /// <summary>Protected wherever they appear directly under a drive root.</summary>
    internal static readonly string[] RootProtectedNames =
    [
        "Boot", "Recovery", "System Volume Information", "$Recycle.Bin",
        "pagefile.sys", "hiberfil.sys", "swapfile.sys", "bootmgr", "BOOTNXT",
    ];

    /// <summary>Protected wherever they appear in a path (also covers other Windows installs).</summary>
    internal static readonly string[] SystemSegmentNames = ["System32", "SysWOW64", "WinSxS"];

    internal static readonly string[] ProgramFilesNames = ["Program Files", "Program Files (x86)", "Program Files (Arm)"];

    /// <summary>Repository metadata. Even an empty folder in here can be part of the repository's structure.</summary>
    internal static readonly string[] VersionControlNames = [".git", ".svn", ".hg"];

    private const string VersionControlReason =
        "Version-control data (Git, SVN or Mercurial). Removing anything in here can break the repository.";

    private readonly IFileAttributeProbe _probe;
    private readonly string _windows;
    private readonly string _windowsTemp;
    private readonly string _programData;
    private readonly string _programDataMicrosoft;
    private readonly HashSet<string> _programFilesRoots;
    private readonly HashSet<string> _profileRoots;
    private readonly HashSet<string> _anchors;
    private readonly string[] _appData;
    private readonly string[] _vendorParents;

    /// <summary>Folder → reason; the folder and everything below it are protected.</summary>
    private readonly Dictionary<string, string> _subtrees = new(PathUtil.Comparer);
    private readonly ProtectedPath[] _protectedDrives;
    private readonly Dictionary<string, string> _files = new(PathUtil.Comparer);

    /// <summary>Every protected path, sorted, so "does this folder contain something protected?" is a binary search.</summary>
    private readonly string[] _everythingProtectedSorted;

    public ProtectionLayout Layout { get; }

    public SafetyGuard(ProtectionLayout layout, IFileAttributeProbe? probe = null)
    {
        Layout = layout;
        _probe = probe ?? Win32FileAttributeProbe.Instance;

        _windows = Norm(layout.WindowsDirectory);
        _windowsTemp = Path.Join(_windows, "Temp");
        _programData = Norm(layout.ProgramDataDirectory);
        _programDataMicrosoft = Path.Join(_programData, "Microsoft");
        _programFilesRoots = NormSet(layout.ProgramFilesDirectories);
        _profileRoots = NormSet(layout.UserProfileDirectories);
        _anchors = NormSet(layout.ScanAnchorDirectories.Concat(layout.TempDirectories).Concat(layout.AppDataDirectories));
        _appData = NormSet(layout.AppDataDirectories).ToArray();

        var subtrees = new List<ProtectedPath>();
        subtrees.AddRange(layout.InstalledProgramDirectories.Select(p => p with { Path = Norm(p.Path) }));
        subtrees.AddRange(layout.OtherProtectedDirectories.Select(p => p with { Path = Norm(p.Path) }));
        subtrees.AddRange(NormSet(layout.CloudSyncRoots).Select(p => new ProtectedPath(p, "Cloud-synced folder (OneDrive, Dropbox, Google Drive or iCloud). DiskSweep leaves these alone because deleting here also deletes from the cloud.")));
        subtrees.AddRange(NormSet(layout.AppOwnDirectories).Select(p => new ProtectedPath(p, "DiskSweep's own folder.")));
        subtrees.AddRange(NormSet(layout.UserExclusions).Select(p => new ProtectedPath(p, "On your exclusion list.")));

        // A protected drive root (for example a Google Drive virtual drive) protects the whole drive.
        _protectedDrives = subtrees.Where(s => s.Path.Length <= 3).ToArray();
        foreach (var s in subtrees.Where(s => s.Path.Length > 3)) _subtrees.TryAdd(s.Path, s.Reason);
        foreach (var f in layout.ProtectedFiles) _files.TryAdd(Norm(f.Path), f.Reason);

        // Most specific first, so Local\Programs\X is judged by "X", not by "Programs".
        _vendorParents = _programFilesRoots.Append(_programData).Concat(NormSet(layout.ScanAnchorDirectories))
            .Distinct(PathUtil.Comparer).OrderByDescending(p => p.Length).ToArray();

        _everythingProtectedSorted = new[] { _windows, _programDataMicrosoft, _programData }
            .Concat(_programFilesRoots).Concat(_profileRoots).Concat(_anchors)
            .Concat(_subtrees.Keys).Concat(_files.Keys)
            .Where(p => p.Length > 3)
            .Distinct(PathUtil.Comparer)
            .Order(PathUtil.Comparer)
            .ToArray();
    }

    private static string Norm(string p) => PathUtil.NormalizeLoose(p) ?? p;
    private static HashSet<string> NormSet(IEnumerable<string> paths) =>
        new(paths.Select(PathUtil.NormalizeLoose).OfType<string>(), PathUtil.Comparer);

    /// <summary>Full check: path validation, hard-coded rules, then the file system (links, cloud files, parents).</summary>
    public SafetyVerdict Check(string path)
    {
        if (!PathNormalizer.TryNormalize(path, out string p, out string error)) return SafetyVerdict.Blocked(error);
        var rules = CheckRules(p);
        return rules.IsAllowed ? CheckFileSystem(p) : rules;
    }

    /// <summary>String-only rules. <paramref name="p"/> must already be normalized.</summary>
    public SafetyVerdict CheckRules(string p)
    {
        if (p.Length <= 3) return SafetyVerdict.Blocked("A drive root can never be removed.");

        if (ProtectedSubtreeReason(p) is { } subtreeReason) return SafetyVerdict.Blocked(subtreeReason);

        if (PathUtil.AreSame(p, _windowsTemp))
            return SafetyVerdict.Blocked("The Windows\\Temp folder itself is protected. Only what's inside it can be cleaned.");
        if (IsProgramFilesRoot(p))
            return SafetyVerdict.Blocked("Program Files folder itself. Only leftovers inside it can be selected.");
        if (PathUtil.AreSame(p, _programData))
            return SafetyVerdict.Blocked("The ProgramData folder itself.");
        if (_profileRoots.Contains(p))
            return SafetyVerdict.Blocked("Root of a user profile.");
        if (_anchors.Contains(p))
            return SafetyVerdict.Blocked("This folder holds app data or temp files. Only items inside it can be cleaned.");
        if (_files.TryGetValue(p, out string? fileReason))
            return SafetyVerdict.Blocked(fileReason);

        // Removing a folder removes everything inside it, so a parent of anything protected is protected too.
        if (FirstProtectedInside(p) is { } inner)
            return SafetyVerdict.Blocked($"Contains a protected item: {inner}");

        return SafetyVerdict.Allowed;
    }

    /// <summary>
    /// Why this folder and everything below it are off-limits, or null. Scanners don't descend into these.
    /// (A folder can be blocked as an item yet still have selectable contents, for example a profile root;
    /// that's <see cref="CheckRules"/>'s job, not this method's.)
    /// </summary>
    public string? ProtectedSubtreeReason(string p)
    {
        foreach (var d in _protectedDrives)
            if (PathUtil.IsOnDrive(p, d.Path)) return d.Reason;

        string[] segments = PathUtil.Segments(p);
        if (segments.Length == 0) return null;

        if (Contains(RootProtectedNames, segments[0]))
            return $"\"{segments[0]}\" at the root of a drive is a protected system item.";
        if (segments[0].Equals(AppPaths.QuarantineFolderName, PathUtil.Comparison))
            return "DiskSweep's quarantine folder. Use Quarantine in DiskSweep to restore items or move them to the Recycle Bin.";
        foreach (string s in segments)
        {
            if (Contains(SystemSegmentNames, s)) return "Windows system folder (System32, SysWOW64 or WinSxS).";
            if (Contains(VersionControlNames, s)) return VersionControlReason;
        }
        if (!PathUtil.IsSameOrUnder(p, _windowsTemp) &&
            (PathUtil.IsSameOrUnder(p, _windows) || segments[0].Equals("Windows", PathUtil.Comparison)))
            return "Windows system folder.";
        if (PathUtil.IsSameOrUnder(p, _programDataMicrosoft))
            return "ProgramData\\Microsoft holds Windows data and is protected.";

        for (string? folder = p; folder is not null && folder.Length > 3; folder = PathUtil.Parent(folder))
            if (_subtrees.TryGetValue(folder, out string? reason)) return reason;
        return null;
    }

    public bool IsProtectedSubtree(string p) => ProtectedSubtreeReason(p) is not null;

    /// <summary>File-system checks: the item and every parent must exist, be readable and not be a link; cloud files are refused.</summary>
    public SafetyVerdict CheckFileSystem(string p)
    {
        var attributes = _probe.GetAttributes(p);
        if (attributes is null)
            return SafetyVerdict.Blocked("Doesn't exist any more or can't be read.");
        if (FileAttributeFlags.IsReparsePoint(attributes.Value))
            return SafetyVerdict.Blocked("This is a junction, symbolic link or other reparse point. DiskSweep never touches those.");
        if (FileAttributeFlags.IsCloudManaged(attributes.Value))
            return SafetyVerdict.Blocked("Cloud file (OneDrive or similar). DiskSweep leaves these alone.");

        for (string? parent = PathUtil.Parent(p); parent is not null && parent.Length > 3; parent = PathUtil.Parent(parent))
        {
            var a = _probe.GetAttributes(parent);
            if (a is null)
                return SafetyVerdict.Blocked($"A parent folder can't be read: {parent}");
            if (FileAttributeFlags.IsReparsePoint(a.Value))
                return SafetyVerdict.Blocked($"Sits inside a junction or symbolic link: {parent}");
        }
        return SafetyVerdict.Allowed;
    }

    /// <summary>True when <paramref name="p"/> is in Program Files, ProgramData or any AppData folder.</summary>
    public bool IsInAppArea(string p)
    {
        if (ProgramFilesNameAt(p) is not null) return true;
        foreach (string root in _programFilesRoots)
            if (PathUtil.IsSameOrUnder(p, root)) return true;
        if (PathUtil.IsSameOrUnder(p, _programData)) return true;
        foreach (string a in _appData)
            if (PathUtil.IsSameOrUnder(p, a)) return true;
        return false;
    }

    /// <summary>True when <paramref name="p"/> is inside any user profile.</summary>
    public bool IsInUserProfile(string p)
    {
        foreach (string r in _profileRoots)
            if (PathUtil.IsStrictlyUnder(p, r)) return true;
        return false;
    }

    /// <summary>
    /// True when <paramref name="p"/> is inside a Windows, Microsoft or hardware-vendor folder that sits directly in
    /// Program Files, ProgramData or an AppData folder (see <see cref="KnownSystemFolders"/>). Scanners leave these alone.
    /// </summary>
    public bool IsInOsOrVendorFolder(string p)
    {
        if (ProgramFilesNameAt(p) is { } firstEnd && firstEnd < p.Length)
            return KnownSystemFolders.IsNeverFlagged(SegmentAt(p, firstEnd + 1));

        foreach (string parent in _vendorParents)
            if (PathUtil.IsStrictlyUnder(p, parent)) return KnownSystemFolders.IsNeverFlagged(SegmentAt(p, parent.Length + 1));
        return false;
    }

    /// <summary>"X:\Program Files" (or a variant) itself, on any drive, or a Program Files folder from the layout.</summary>
    private bool IsProgramFilesRoot(string p) =>
        _programFilesRoots.Contains(p) || ProgramFilesNameAt(p) == p.Length;

    /// <summary>
    /// When the first folder of <paramref name="p"/> is "Program Files", "Program Files (x86)" or "Program Files (Arm)",
    /// returns the index just past that folder name; otherwise null.
    /// </summary>
    private static int? ProgramFilesNameAt(string p)
    {
        if (p.Length <= 3) return null;
        int end = p.IndexOf('\\', 3);
        if (end < 0) end = p.Length;
        var first = p.AsSpan(3, end - 3);
        foreach (string name in ProgramFilesNames)
            if (first.Equals(name, PathUtil.Comparison)) return end;
        return null;
    }

    private static string SegmentAt(string p, int start)
    {
        int end = p.IndexOf('\\', start);
        return end < 0 ? p[start..] : p[start..end];
    }

    private static bool Contains(string[] names, string value)
    {
        foreach (string n in names)
            if (n.Equals(value, PathUtil.Comparison)) return true;
        return false;
    }

    private string? FirstProtectedInside(string p)
    {
        string prefix = p + "\\";
        int i = Array.BinarySearch(_everythingProtectedSorted, prefix, PathUtil.Comparer);
        if (i < 0) i = ~i;
        return i < _everythingProtectedSorted.Length && _everythingProtectedSorted[i].StartsWith(prefix, PathUtil.Comparison)
            ? _everythingProtectedSorted[i]
            : null;
    }
}
