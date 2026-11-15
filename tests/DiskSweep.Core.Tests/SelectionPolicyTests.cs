using DiskSweep.Core.Models;
using DiskSweep.Core.Safety;

namespace DiskSweep.Core.Tests;

public class SelectionPolicyTests
{
    private readonly SafetyGuard _guard = TestLayouts.Guard();

    private static ScanItem Item(string path, RiskLevel risk, ItemKind kind = ItemKind.Folder) => new()
    {
        Category = ScanCategory.EmptyFolders, Kind = kind, Path = path, Reason = "test", Risk = risk,
    };

    [Fact]
    public void Only_safe_items_are_preselected()
    {
        Assert.True(new SelectableItem(Item(@"D:\Data\Empty", RiskLevel.Safe), _guard).IsSelected);
        Assert.False(new SelectableItem(Item(@"D:\Data\Empty", RiskLevel.Review), _guard).IsSelected);
        Assert.False(new SelectableItem(Item(@"D:\Data\Empty", RiskLevel.Risky), _guard).IsSelected);
    }

    [Fact]
    public void Review_and_risky_items_can_be_ticked_by_hand()
    {
        var review = new SelectableItem(Item(@"D:\Data\Big.iso", RiskLevel.Review, ItemKind.File), _guard);
        Assert.True(review.TrySetSelected(true));
        var risky = new SelectableItem(Item(@"D:\Data\Old", RiskLevel.Risky), _guard);
        Assert.True(risky.TrySetSelected(true));
    }

    [Fact]
    public void Only_safe_and_review_levels_can_be_ticked_in_bulk()
    {
        Assert.True(SelectionPolicy.AllowsBulkTick(RiskLevel.Safe));
        Assert.True(SelectionPolicy.AllowsBulkTick(RiskLevel.Review));
        Assert.False(SelectionPolicy.AllowsBulkTick(RiskLevel.Risky));
    }

    [Theory]
    [InlineData(@"Q:\Windows\System32")]
    [InlineData(@"Q:\Program Files")]
    [InlineData(@"Q:\Users\alice")]
    [InlineData(@"Q:\Program Files\Contoso\Editor")]
    [InlineData(@"Q:\ProgramData\Microsoft\Crypto")]
    [InlineData(@"Q:\pagefile.sys")]
    public void Protected_items_are_never_selectable_even_if_marked_safe(string path)
    {
        var s = new SelectableItem(Item(path, RiskLevel.Safe), _guard);
        Assert.False(s.Decision.CanSelect);
        Assert.False(s.IsSelected);
        Assert.False(s.TrySetSelected(true));
        Assert.False(s.IsSelected);
        Assert.False(string.IsNullOrEmpty(s.Decision.BlockedReason));
    }

    [Fact]
    public void Registry_items_are_report_only()
    {
        var key = new ScanItem
        {
            Category = ScanCategory.OrphanedRegistry, Kind = ItemKind.RegistryKey,
            Path = @"HKCU\Software\OldVendor", Reason = "test", Risk = RiskLevel.Safe, IsReportOnly = true,
        };
        var s = new SelectableItem(key, _guard);
        Assert.False(s.IsSelected);
        Assert.False(s.TrySetSelected(true));
    }

    [Fact]
    public void Programs_are_selectable_only_through_their_own_uninstaller_and_never_preselected()
    {
        var withUninstaller = new ScanItem
        {
            Category = ScanCategory.UnusedPrograms, Kind = ItemKind.Program,
            Path = @"Q:\Program Files\Contoso\Editor", Reason = "test", Risk = RiskLevel.Review,
            UninstallCommand = "\"Q:\\Program Files\\Contoso\\Editor\\uninstall.exe\"",
        };
        var s = new SelectableItem(withUninstaller, _guard);
        Assert.False(s.IsSelected);
        Assert.True(s.TrySetSelected(true));

        var noUninstaller = new ScanItem
        {
            Category = ScanCategory.UnusedPrograms, Kind = ItemKind.Program,
            Path = @"Q:\Program Files\Other", Reason = "test", Risk = RiskLevel.Review,
        };
        Assert.False(new SelectableItem(noUninstaller, _guard).TrySetSelected(true));
    }

    [Fact]
    public void Unticking_always_works()
    {
        var s = new SelectableItem(Item(@"D:\Data\Empty", RiskLevel.Safe), _guard);
        Assert.True(s.IsSelected);
        s.TrySetSelected(false);
        Assert.False(s.IsSelected);
    }
}
