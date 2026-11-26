using DiskSweep.Core.Models;

namespace DiskSweep.Mcp.Tests;

/// <summary>propose_cleanup: which items a plan may hold, the caps, and that proposing never moves anything.</summary>
public sealed class ProposeCleanupTests : IDisposable
{
    private readonly McpTestBed _bed = new();

    public void Dispose() => _bed.Dispose();

    private PlanStore Plans => _bed.Plans;
    private CleanupPlanner Planner => _bed.Planner;

    [Fact]
    public async Task Safe_and_review_items_make_a_plan_and_nothing_moves()
    {
        var file = _bed.AddFile("a.tmp", content: "aaa");
        _bed.Tree.File(@"f\x.log", "xx");
        var folder = _bed.AddFolder("f");
        await _bed.ScanAsync();

        var r = Planner.Propose([_bed.Id(file), _bed.Id(folder)]);

        Assert.NotNull(r.PlanId);
        Assert.Equal(2, r.Items.Count);
        Assert.Equal(5, r.TotalBytes);
        Assert.Empty(r.Refused);
        Assert.Contains("Nothing has moved", r.NextStep);
        Assert.True(File.Exists(file.Path));
        Assert.True(Directory.Exists(folder.Path));
        Assert.False(Directory.Exists(_bed.QuarantineRoot));
    }

    [Fact]
    public async Task Risky_items_are_refused()
    {
        var risky = _bed.AddFile("risky.tmp", RiskLevel.Risky);
        await _bed.ScanAsync();

        var refusal = Assert.Single(Planner.Propose([_bed.Id(risky)]).Refused);

        Assert.Contains("Risky", refusal.Reason);
    }

    [Fact]
    public async Task Programs_and_registry_keys_are_refused()
    {
        var program = _bed.Add(new ScanItem
        {
            Category = ScanCategory.UnusedPrograms, Kind = ItemKind.Program, Path = _bed.Tree.Dir("app"),
            Reason = "unused", Risk = RiskLevel.Review, UninstallCommand = "uninst.exe",
        });
        var registry = _bed.Add(new ScanItem
        {
            Category = ScanCategory.OrphanedRegistry, Kind = ItemKind.RegistryKey, Path = @"HKCU\Software\Gone",
            Reason = "orphan", Risk = RiskLevel.Review, IsReportOnly = true,
        });
        await _bed.ScanAsync(_bed.DriveRoot);

        var r = Planner.Propose([_bed.Id(program), _bed.Id(registry)]);

        Assert.Null(r.PlanId);
        Assert.Contains("uninstaller", r.Refused[0].Reason);
        Assert.Contains("registry", r.Refused[1].Reason);
    }

    [Fact]
    public async Task Excluded_path_is_refused_by_the_safety_guard()
    {
        var item = _bed.AddFile("mine.tmp");
        _bed.Exclusions = [item.Path];
        await _bed.ScanAsync();

        var refusal = Assert.Single(Planner.Propose([_bed.Id(item)]).Refused);

        Assert.Contains("exclusion list", refusal.Reason);
    }

    [Fact]
    public async Task Version_control_folder_is_refused()
    {
        _bed.Tree.File(@"repo\.git\objects\pack.idx", "p");
        var objects = _bed.AddFolder(@"repo\.git\objects");
        await _bed.ScanAsync();

        var refusal = Assert.Single(Planner.Propose([_bed.Id(objects)]).Refused);

        Assert.Contains("Version-control", refusal.Reason);
    }

    [Fact]
    public async Task Folder_with_a_junction_is_refused()
    {
        _bed.Tree.File(@"leftover\a.txt", "x");
        var folder = _bed.AddFolder("leftover");
        _bed.Tree.Junction(@"leftover\link", _bed.Tree.Dir("target"));
        await _bed.ScanAsync();

        var refusal = Assert.Single(Planner.Propose([_bed.Id(folder)]).Refused);

        Assert.Contains("junction", refusal.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Files_changed_since_the_scan_are_refused()
    {
        var file = _bed.AddFile("log.txt", content: "one");
        await _bed.ScanAsync();
        File.WriteAllText(file.Path, "two, longer");

        var refusal = Assert.Single(Planner.Propose([_bed.Id(file)]).Refused);

        Assert.Contains("changed since the scan", refusal.Reason);
    }

    [Fact]
    public async Task Item_cap_refuses_the_overflow()
    {
        _bed.Limits = new PlanLimits { MaxItems = 2 };
        var items = Enumerable.Range(0, 4).Select(i => _bed.AddFile($"f{i}.tmp")).ToList();
        await _bed.ScanAsync();

        var r = Planner.Propose(items.Select(_bed.Id).ToList());

        Assert.Equal(2, r.Items.Count);
        Assert.Equal(2, r.Refused.Count);
        Assert.All(r.Refused, x => Assert.Contains("Over the plan limit", x.Reason));
    }

    [Fact]
    public async Task Byte_cap_refuses_the_item_that_would_exceed_it_and_the_rest()
    {
        _bed.Limits = new PlanLimits { MaxBytes = 10 };
        var eight = _bed.AddFile("eight.tmp", content: new string('x', 8));
        var five = _bed.AddFile("five.tmp", content: new string('x', 5));
        var one = _bed.AddFile("one.tmp", content: "x");
        await _bed.ScanAsync();

        var r = Planner.Propose([_bed.Id(eight), _bed.Id(five), _bed.Id(one)]);

        Assert.Equal(8, Assert.Single(r.Items).SizeBytes);
        Assert.Equal(2, r.Refused.Count);
        Assert.All(r.Refused, x => Assert.Contains("Over the plan limit", x.Reason));
    }

    [Fact]
    public async Task Nested_and_duplicate_ids_count_once()
    {
        var inner = _bed.AddFile(@"f\x.tmp", content: "xx");
        var folder = _bed.AddFolder("f");
        await _bed.ScanAsync();

        var folderFirst = Planner.Propose([_bed.Id(folder), _bed.Id(inner), _bed.Id(folder)]);
        Assert.Equal(folder.Path, Assert.Single(folderFirst.Items).Path);
        Assert.Contains("in this plan", Assert.Single(folderFirst.Refused).Reason);
        Assert.Equal(2, folderFirst.TotalBytes);

        var fileFirst = Planner.Propose([_bed.Id(inner), _bed.Id(folder)]);
        Assert.Equal(inner.Path, Assert.Single(fileFirst.Items).Path);
        Assert.Contains("in this plan", Assert.Single(fileFirst.Refused).Reason);
    }

    [Fact]
    public async Task Different_spellings_of_one_id_count_once()
    {
        var file = _bed.AddFile("a.tmp", content: "aa");
        await _bed.ScanAsync();
        string id = _bed.Id(file);
        string[] parts = id.Split('-');

        var r = Planner.Propose([id, $"{parts[0]}-0{parts[1]}", $"+{id}", $" {id} "]);

        Assert.Equal(id, Assert.Single(r.Items).Id);
        Assert.Equal(2, r.TotalBytes);
        Assert.Empty(r.Refused);
    }

    [Fact]
    public async Task Two_scan_items_with_the_same_path_count_once()
    {
        _bed.Tree.Dir("shared");
        var asEmpty = _bed.AddFolder("shared", RiskLevel.Safe, ScanCategory.EmptyFolders);
        var asLeftover = _bed.AddFolder("shared", RiskLevel.Review, ScanCategory.Leftovers);
        await _bed.ScanAsync();

        var r = Planner.Propose([_bed.Id(asEmpty), _bed.Id(asLeftover)]);

        Assert.Single(r.Items);
        Assert.Contains("in this plan", Assert.Single(r.Refused).Reason);
    }

    [Fact]
    public async Task Unknown_ids_and_stale_scans_are_refused()
    {
        var file = _bed.AddFile("a.tmp");
        await _bed.ScanAsync();

        Assert.Contains("Unknown item id", Assert.Single(Planner.Propose(["nope"]).Refused).Reason);

        _bed.Clock.Advance(TimeSpan.FromMinutes(61));
        var r = Planner.Propose([_bed.Id(file)]);
        Assert.Null(r.PlanId);
        Assert.Contains("Scan again", Assert.Single(r.Refused).Reason);
    }

    [Fact]
    public async Task No_plan_when_nothing_is_accepted()
    {
        var risky = _bed.AddFile("risky.tmp", RiskLevel.Risky);
        await _bed.ScanAsync();

        var r = Planner.Propose([_bed.Id(risky)]);
        Assert.Null(r.PlanId);
        Assert.Null(r.ExpiresAtUtc);
        Assert.Contains("No plan was made", r.NextStep);

        Assert.Null(Planner.Propose([]).PlanId);
    }

    [Fact]
    public void PlanStore_refuses_forged_used_and_expired_ids()
    {
        var guard = _bed.NewGuard();
        var plan = Plans.Issue(guard, []);

        Assert.False(Plans.TryTake(Guid.NewGuid().ToString("N"), out _, out string forged));
        Assert.Equal("This plan id was not issued by this DiskSweep session.", forged);
        Assert.False(Plans.TryTake("", out _, out _));

        Assert.True(Plans.TryTake(plan.Id, out var taken, out _));
        Assert.Same(plan, taken);
        Assert.False(Plans.TryTake(plan.Id, out _, out string used));
        Assert.Equal("This plan was already used. Propose a new one.", used);

        var late = Plans.Issue(guard, []);
        _bed.Clock.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));
        Assert.False(Plans.TryTake(late.Id, out _, out string expired));
        Assert.Equal("This plan expired (plans last 10 minutes). Propose a new one.", expired);
        Assert.False(Plans.TryTake(late.Id, out _, out string again));
        Assert.Equal(expired, again);
    }
}
