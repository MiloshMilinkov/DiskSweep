using DiskSweep.Core.Logging;
using DiskSweep.Core.Models;
using DiskSweep.Core.Safety;
using DiskSweep.Core.Scanning;
using DiskSweep.Core.SystemInfo;

namespace DiskSweep.Core.Tests;

public class EmptyFolderScannerTests : IDisposable
{
    private readonly TempTree _tree = new();

    public void Dispose() => _tree.Dispose();

    internal static ScanContext Context(string driveRoot, ProtectionLayout? layout = null)
    {
        layout ??= TestLayouts.Synthetic();
        var snapshot = new SystemSnapshot
        {
            Profiles = new ProfileSet([], []),
            Programs = [],
            StorePackages = new StorePackageList([], new()),
            CloudRoots = [],
            Layout = layout,
        };
        var drive = new DriveSummary(driveRoot, "Test", "NTFS", DriveType.Fixed, 100L << 30, 50L << 30, false);
        return new ScanContext
        {
            Request = new ScanRequest(drive, new HashSet<ScanCategory> { ScanCategory.EmptyFolders }, new ScanOptions()),
            Snapshot = snapshot,
            Guard = new SafetyGuard(layout, Win32FileAttributeProbe.Instance),
            Status = new ScanStatus(),
            Skips = new SkipLog(NullActionLog.Instance),
            Log = NullActionLog.Instance,
            Token = CancellationToken.None,
        };
    }

    [Fact]
    public void Reports_only_top_most_empty_folders_and_respects_every_exception()
    {
        string a = _tree.Dir(@"a\b\c");                       // nested empties → report "a" only
        string d = _tree.Dir("d");
        _tree.File(@"d\desktop.ini", "[.ShellClassInfo]");    // only desktop.ini → not empty
        _tree.Dir("e");                                       // created just now → skipped
        _tree.Dir(@"f\g");                                    // g is new → f isn't empty either
        string t = _tree.Dir(@"t\inner");                      // target of a junction, itself an empty tree
        _tree.Dir("h");
        _tree.Junction(@"h\link", Path.Combine(_tree.Root, "t")); // a junction is content, never followed
        string hidden = _tree.Dir("hidden");
        File.SetAttributes(hidden, FileAttributes.Directory | FileAttributes.Hidden);

        foreach (string rel in new[] { @"a\b\c", @"a\b", "a", "d", "f", @"t\inner", "t", "h", "hidden" })
            TempTree.Age(Path.Combine(_tree.Root, rel), 30);

        var ctx = Context(Path.GetPathRoot(_tree.Root)!);
        new EmptyFolderScanner().ScanTree(ctx, _tree.Root);

        var reported = ctx.Items.ToDictionary(i => Path.GetRelativePath(_tree.Root, i.Path), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(new[] { "a", "hidden", "t" }, reported.Keys.Order(StringComparer.OrdinalIgnoreCase));

        Assert.Equal("Empty folder tree (3 folders)", reported["a"].Reason);
        Assert.Equal(RiskLevel.Safe, reported["a"].Risk);
        Assert.Equal(RiskLevel.Review, reported["hidden"].Risk);
        Assert.DoesNotContain(ctx.Items, i => i.Path.Contains(@"\h\link", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(ctx.Skips.Snapshot(), s => s.Path.EndsWith(@"h\link", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Empty_folders_next_to_programs_or_inside_dot_folders_are_review()
    {
        _tree.File(@"Games\Racer\racer.exe");
        string plugins = _tree.Dir(@"Games\Racer\plugins");
        string settings = _tree.Dir(@"profile\.tool\cache");
        string mine = _tree.Dir(@"profile\Documents\Empty");
        _tree.Dir(@"repo\.git\refs\tags");
        foreach (string p in new[] { plugins, settings, Path.Combine(_tree.Root, @"profile\.tool"), mine,
                     Path.Combine(_tree.Root, @"repo\.git\refs\tags"), Path.Combine(_tree.Root, @"repo\.git\refs") })
            TempTree.Age(p, 30);

        var ctx = Context(Path.GetPathRoot(_tree.Root)!);
        new EmptyFolderScanner().ScanTree(ctx, _tree.Root);
        var byPath = ctx.Items.ToDictionary(i => Path.GetRelativePath(_tree.Root, i.Path), i => i.Risk, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(RiskLevel.Review, byPath[@"Games\Racer\plugins"]);
        Assert.Equal(RiskLevel.Review, byPath[@"profile\.tool"]); // top-most empty folder, a dot-folder
        Assert.Equal(RiskLevel.Safe, byPath[@"profile\Documents\Empty"]);
        Assert.DoesNotContain(byPath.Keys, k => k.Contains(".git", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Empty_folders_in_app_areas_are_review_not_safe()
    {
        string appData = _tree.Dir(@"profile\AppData");
        string empty = _tree.Dir(@"profile\AppData\Roaming\OldTool\cache");
        foreach (string rel in new[] { @"profile\AppData\Roaming\OldTool\cache", @"profile\AppData\Roaming\OldTool" })
            TempTree.Age(Path.Combine(_tree.Root, rel), 30);

        var layout = TestLayouts.Synthetic();
        var withAppData = new ProtectionLayout
        {
            SystemDrive = layout.SystemDrive,
            WindowsDirectory = layout.WindowsDirectory,
            ProgramFilesDirectories = layout.ProgramFilesDirectories,
            ProgramDataDirectory = layout.ProgramDataDirectory,
            UserProfileDirectories = layout.UserProfileDirectories,
            AppDataDirectories = [appData],
        };
        var ctx = Context(Path.GetPathRoot(_tree.Root)!, withAppData);
        new EmptyFolderScanner().ScanTree(ctx, _tree.Root);

        var item = Assert.Single(ctx.Items);
        Assert.EndsWith(@"Roaming\OldTool", item.Path);
        Assert.Equal(RiskLevel.Review, item.Risk);
    }
}
