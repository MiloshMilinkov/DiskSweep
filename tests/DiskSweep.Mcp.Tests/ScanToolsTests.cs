using DiskSweep.Core.Models;
using DiskSweep.Mcp.Tools;
using ModelContextProtocol;

namespace DiskSweep.Mcp.Tests;

/// <summary>The read-only tools: scan, list_candidates and explain_item.</summary>
public sealed class ScanToolsTests : IDisposable
{
    private readonly McpTestBed _bed = new();

    public void Dispose() => _bed.Dispose();

    private ScanTools Tools => new(_bed.Session);

    [Fact]
    public async Task Scan_summarises_by_category_and_risk()
    {
        _bed.AddFile(@"t\a.tmp", content: "aa");
        _bed.AddFile(@"t\b.tmp", content: "bbb");
        _bed.Tree.File(@"t\old\c.log", "cccc");
        _bed.AddFolder(@"t\old");

        var summary = await Tools.Scan(_bed.Tree.Root);

        Assert.Equal(3, summary.ItemCount);
        Assert.Equal(9, summary.TotalBytes);
        Assert.Contains(new CategoryRiskCount("TempAndOldFiles", "Safe", 2, 5), summary.Breakdown);
        Assert.Contains(new CategoryRiskCount("TempAndOldFiles", "Review", 1, 4), summary.Breakdown);
        Assert.NotEmpty(summary.DataNote);
    }

    [Fact]
    public async Task Folder_scan_keeps_only_items_under_the_folder()
    {
        _bed.AddFile(@"a\one.tmp");
        _bed.AddFile(@"a\sub\two.tmp");
        _bed.AddFile(@"ab\three.tmp");
        _bed.AddFile(@"b\four.tmp");

        var summary = await Tools.Scan(Path.Combine(_bed.Tree.Root, "a"));

        Assert.Equal(2, summary.ItemCount);
        Assert.All(Tools.ListCandidates().Items, i => Assert.Contains(@"\a\", i.Path));
    }

    [Fact]
    public async Task Scan_accepts_drive_and_folder_forms()
    {
        string letter = _bed.DriveRoot[..1];
        await Tools.Scan(letter);
        await Tools.Scan(letter.ToLowerInvariant() + @":\");
        await Tools.Scan(_bed.Tree.Dir("x").Replace('\\', '/'));
    }

    [Theory]
    [InlineData("")]
    [InlineData(@"\\server\share")]
    [InlineData("missing-folder")]
    [InlineData("other-drive")]
    public async Task Scan_refuses_bad_targets(string target)
    {
        target = target switch
        {
            "missing-folder" => Path.Combine(_bed.Tree.Root, "does-not-exist"),
            "other-drive" => (_bed.DriveRoot[0] == 'Z' ? "Y" : "Z") + ":",
            _ => target,
        };

        await Assert.ThrowsAsync<McpException>(() => Tools.Scan(target));
        Assert.Null(_bed.Session.Latest);
    }

    [Fact]
    public async Task Scan_refuses_unknown_categories()
    {
        var ex = await Assert.ThrowsAsync<McpException>(() => Tools.Scan(_bed.Tree.Root, ["Everything"]));
        Assert.Contains("EmptyFolders", ex.Message);
    }

    [Fact]
    public async Task Second_scan_while_one_runs_is_refused()
    {
        var gate = new TaskCompletionSource();
        _bed.Runner = async (request, _, _) =>
        {
            await gate.Task;
            return _bed.BuildResult(request);
        };

        var first = Tools.Scan(_bed.Tree.Root);
        var ex = await Assert.ThrowsAsync<McpException>(() => Tools.Scan(_bed.Tree.Root));
        Assert.Contains("already running", ex.Message);

        gate.SetResult();
        await first;
        await Tools.Scan(_bed.Tree.Root); // free again
    }

    [Fact]
    public async Task List_candidates_filters_sorts_and_pages()
    {
        _bed.AddFile("s1.tmp", content: new string('x', 10));
        _bed.AddFile("s2.tmp", content: new string('x', 30));
        _bed.AddFile("s3.tmp", content: new string('x', 20));
        _bed.AddFile("r1.tmp", RiskLevel.Review, content: new string('x', 100));
        await Tools.Scan(_bed.Tree.Root);

        var safe = Tools.ListCandidates(risk: "Safe");
        Assert.Equal(3, safe.Total);
        Assert.Equal([30L, 20L, 10L], safe.Items.Select(i => i.SizeBytes ?? -1));

        var second = Tools.ListCandidates(risk: "safe", limit: 1, offset: 1);
        Assert.Equal(20, Assert.Single(second.Items).SizeBytes);
        Assert.Equal(4, Tools.ListCandidates(category: "TempAndOldFiles").Total);
        Assert.Throws<McpException>(() => Tools.ListCandidates(risk: "Dangerous"));
    }

    [Fact]
    public async Task List_candidates_returns_at_most_200()
    {
        for (int i = 0; i < 205; i++)
            _bed.Add(new ScanItem
            {
                Category = ScanCategory.TempAndOldFiles, Kind = ItemKind.File, Reason = "test", Risk = RiskLevel.Safe,
                Path = Path.Combine(_bed.Tree.Root, $"f{i}.tmp"), SizeBytes = i,
            });
        await Tools.Scan(_bed.Tree.Root);

        var page = Tools.ListCandidates(limit: 500);

        Assert.Equal(205, page.Total);
        Assert.Equal(200, page.Items.Count);
    }

    [Fact]
    public async Task List_marks_risky_items_programs_and_registry_keys_as_not_plannable()
    {
        var risky = _bed.AddFile("risky.tmp", RiskLevel.Risky);
        var program = _bed.Add(new ScanItem
        {
            Category = ScanCategory.UnusedPrograms, Kind = ItemKind.Program, Path = _bed.Tree.Dir("app"),
            DisplayName = "Old App", Reason = "unused", Risk = RiskLevel.Review, UninstallCommand = "uninst.exe",
        });
        var registry = _bed.Add(new ScanItem
        {
            Category = ScanCategory.OrphanedRegistry, Kind = ItemKind.RegistryKey, Path = @"HKCU\Software\Gone",
            Reason = "orphan", Risk = RiskLevel.Review, IsReportOnly = true,
        });
        await Tools.Scan(_bed.DriveRoot);

        var items = Tools.ListCandidates().Items.ToDictionary(i => i.Id);
        Assert.Contains("Risky", items[_bed.Id(risky)].NotPlannableReason);
        Assert.Contains("uninstaller", items[_bed.Id(program)].NotPlannableReason);
        Assert.Contains("registry", items[_bed.Id(registry)].NotPlannableReason);
        Assert.All(items.Values, i => Assert.False(i.Plannable));
    }

    [Fact]
    public async Task Ids_from_an_older_scan_or_made_up_are_rejected()
    {
        Assert.False(_bed.Session.TryGetItem("1-0", out _, out string before));
        Assert.Contains("scan", before);

        _bed.AddFile("a.tmp");
        var first = await _bed.ScanAsync();
        string oldId = first.IdOf(0);
        await _bed.ScanAsync();

        Assert.False(_bed.Session.TryGetItem(oldId, out _, out string old));
        Assert.Contains("older scan", old);
        Assert.False(_bed.Session.TryGetItem("zzz", out _, out string unknown));
        Assert.Contains("Unknown item id", unknown);
        Assert.False(_bed.Session.TryGetItem("2-99", out _, out _));
        Assert.Throws<McpException>(() => Tools.ExplainItem(oldId));
    }

    [Fact]
    public async Task Explain_passes_every_check_for_a_safe_unchanged_file()
    {
        var item = _bed.AddFile("ok.tmp");
        await _bed.ScanAsync();

        var e = Tools.ExplainItem(_bed.Id(item));

        Assert.Equal(8, e.Checks.Count);
        Assert.All(e.Checks, c => Assert.True(c.Passed, $"{c.Name}: {c.Detail}"));
        Assert.True(e.CanBePlanned);
        Assert.True(File.Exists(item.Path));
    }

    [Fact]
    public async Task Explain_shows_the_safety_guard_refusing_an_excluded_path()
    {
        var item = _bed.AddFile("mine.tmp");
        _bed.Exclusions = [item.Path];
        await _bed.ScanAsync();

        var e = Tools.ExplainItem(_bed.Id(item));

        var rules = Assert.Single(e.Checks, c => c.Name == "Path rules");
        Assert.False(rules.Passed);
        Assert.Contains("exclusion list", rules.Detail);
        Assert.False(e.CanBePlanned);
    }

    [Fact]
    public async Task Explain_shows_risky_items_and_stale_scans_failing()
    {
        var risky = _bed.AddFile("risky.tmp", RiskLevel.Risky);
        await _bed.ScanAsync();

        var e = Tools.ExplainItem(_bed.Id(risky));
        Assert.False(Assert.Single(e.Checks, c => c.Name == "Risk is Safe or Review").Passed);
        Assert.False(e.CanBePlanned);

        _bed.Clock.Advance(TimeSpan.FromMinutes(61));
        e = Tools.ExplainItem(_bed.Id(risky));
        Assert.False(Assert.Single(e.Checks, c => c.Name == "Scan is fresh").Passed);
    }

    [Fact]
    public async Task Explain_does_not_run_file_checks_on_a_program()
    {
        var program = _bed.Add(new ScanItem
        {
            Category = ScanCategory.UnusedPrograms, Kind = ItemKind.Program, Path = _bed.Tree.Dir("app"),
            Reason = "unused", Risk = RiskLevel.Review, UninstallCommand = "uninst.exe",
        });
        await _bed.ScanAsync();

        var e = Tools.ExplainItem(_bed.Id(program));

        Assert.False(e.Checks[0].Passed);
        Assert.All(e.Checks.Skip(2), c => Assert.StartsWith("Not run", c.Detail));
        Assert.False(e.CanBePlanned);
    }
}
