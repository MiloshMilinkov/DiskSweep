using DiskSweep.Core.IO;
using DiskSweep.Core.Models;
using DiskSweep.Core.Safety;

namespace DiskSweep.Core.Tests;

/// <summary>Uses real junctions and symbolic links on disk to prove they are never selected or walked into.</summary>
public class ReparsePointTests : IDisposable
{
    private readonly TempTree _tree = new();
    // Synthetic layout on Q: so the temp folder isn't protected for any other reason; the real file system is probed.
    private readonly SafetyGuard _guard = new(TestLayouts.Synthetic(), Win32FileAttributeProbe.Instance);

    public void Dispose() => _tree.Dispose();

    [Fact]
    public void A_junction_itself_is_blocked()
    {
        string target = _tree.Dir("target");
        string link = _tree.Junction("link", target);

        Assert.True(_guard.Check(target).IsAllowed);
        var verdict = _guard.Check(link);
        Assert.False(verdict.IsAllowed);
        Assert.Contains("junction", verdict.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Paths_reached_through_a_junction_are_blocked()
    {
        string target = _tree.Dir("target");
        _tree.File(@"target\inner\data.txt");
        string link = _tree.Junction("link", target);

        Assert.True(_guard.Check(Path.Combine(target, @"inner\data.txt")).IsAllowed);
        Assert.False(_guard.Check(Path.Combine(link, @"inner\data.txt")).IsAllowed);
        Assert.False(_guard.Check(Path.Combine(link, "inner")).IsAllowed);
    }

    [Fact]
    public void A_symbolic_link_file_is_blocked_when_windows_allows_creating_one()
    {
        string target = _tree.File("real.txt");
        string? link = _tree.TryFileSymlink("link.txt", target);
        if (link is null) return; // needs Developer Mode or admin; junction tests above cover the rule

        Assert.False(_guard.Check(link).IsAllowed);
    }

    [Fact]
    public void Measuring_a_folder_never_walks_into_a_junction_and_reports_it()
    {
        string outside = _tree.Dir("outside");
        _tree.File(@"outside\big.bin", new string('x', 10_000));
        string folder = _tree.Dir("folder");
        _tree.File(@"folder\small.txt", "abc");
        _tree.Junction(@"folder\link", outside);

        var stats = FolderMeasure.Measure(folder, CancellationToken.None);

        Assert.Equal(1, stats.FileCount);
        Assert.Equal(3, stats.TotalBytes);              // big.bin behind the junction is not counted
        Assert.NotNull(stats.FirstReparsePoint);
    }

    [Fact]
    public void A_folder_that_contains_a_junction_can_never_be_selected()
    {
        string folder = _tree.Dir("leftover");
        _tree.Junction(@"leftover\link", _tree.Dir("elsewhere"));
        var stats = FolderMeasure.Measure(folder, CancellationToken.None);

        var item = new ScanItem
        {
            Category = ScanCategory.Leftovers, Kind = ItemKind.Folder, Path = folder,
            Reason = "test", Risk = RiskLevel.Safe, ContainsReparsePoint = stats.FirstReparsePoint,
        };
        var selectable = new SelectableItem(item, _guard);

        Assert.False(selectable.Decision.CanSelect);
        Assert.False(selectable.IsSelected);
        Assert.False(selectable.TrySetSelected(true));
    }

    [Fact]
    public void A_folder_replaced_by_a_junction_after_the_scan_cannot_be_ticked()
    {
        string folder = _tree.Dir("swap");
        var item = new ScanItem
        {
            Category = ScanCategory.EmptyFolders, Kind = ItemKind.Folder, Path = folder,
            Reason = "test", Risk = RiskLevel.Safe,
        };
        var selectable = new SelectableItem(item, _guard);
        Assert.True(selectable.IsSelected); // Safe → pre-ticked

        selectable.TrySetSelected(false);
        Directory.Delete(folder);
        _tree.Junction("swap", _tree.Dir("target-of-swap"));

        Assert.False(selectable.TrySetSelected(true));
        Assert.False(selectable.IsSelected);
    }

    [Fact]
    public void Directory_listing_reports_links_without_following_them()
    {
        string target = _tree.Dir("t");
        _tree.File(@"t\inside.txt");
        _tree.Junction("j", target);

        var entries = new List<FsEntry>();
        Assert.True(FastDirectory.TryList(_tree.Root, entries, out _));
        var j = Assert.Single(entries, e => e.Name == "j");
        Assert.True(j.IsReparsePoint);
        Assert.True(j.IsDirectory);
    }
}
