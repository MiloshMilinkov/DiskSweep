using DiskSweep.Core.Models;
using ModelContextProtocol;

namespace DiskSweep.Mcp.Tests;

/// <summary>list_quarantine and restore_from_quarantine, on a quarantine folder inside the test folder.</summary>
public sealed class QuarantineToolsTests : IDisposable
{
    private readonly McpTestBed _bed = new();
    private PlanStore? _plans;

    public void Dispose() => _bed.Dispose();

    private PlanStore Plans => _plans ??= new PlanStore(_bed.Session.Env);
    private QuarantineBrowser Browser => new(_bed.Session);

    private async Task<(ScanItem A, ScanItem B)> QuarantineTwoFilesAsync()
    {
        var a = _bed.AddFile("a.tmp", content: "aa");
        var b = _bed.AddFile(@"sub\b.tmp", content: "bbb");
        await _bed.ScanAsync();
        string planId = new CleanupPlanner(_bed.Session, Plans).Propose([_bed.Id(a), _bed.Id(b)]).PlanId!;
        Assert.True(new PlanExecutor(_bed.Session.Env, Plans).Execute(planId, "test-client", CancellationToken.None).Approved);
        return (a, b);
    }

    private static bool CameFrom(QuarantineListing entry, ScanItem item) =>
        string.Equals(entry.OriginalPath, SweepSession.Normalized(item.Path), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void List_is_empty_when_nothing_is_in_quarantine() => Assert.Empty(Browser.List());

    [Fact]
    public async Task List_shows_quarantined_items_with_stable_ids()
    {
        var (a, b) = await QuarantineTwoFilesAsync();

        var listed = Browser.List();

        Assert.Equal(2, listed.Count);
        Assert.All(listed, e => Assert.True(e.CanRestore));
        Assert.Contains(listed, e => CameFrom(e, a) && e.SizeBytes == 2);
        Assert.Contains(listed, e => CameFrom(e, b) && e.SizeBytes == 3);
        Assert.Equal(2, listed.Select(e => e.Id).Distinct().Count());
        Assert.Equal(listed.Select(e => e.Id), Browser.List().Select(e => e.Id));
    }

    [Fact]
    public async Task Restore_puts_items_back_and_never_overwrites()
    {
        var (a, b) = await QuarantineTwoFilesAsync();
        var listed = Browser.List();
        File.WriteAllText(b.Path, "new"); // something new appeared at b's old place

        var outcomes = Browser.Restore([listed.Single(e => CameFrom(e, a)).Id, listed.Single(e => CameFrom(e, b)).Id]);

        Assert.All(outcomes, o => Assert.True(o.Restored, o.Detail));
        Assert.Equal("aa", File.ReadAllText(a.Path));
        Assert.Equal("new", File.ReadAllText(b.Path));
        Assert.Equal("bbb", File.ReadAllText(Path.Combine(Path.GetDirectoryName(b.Path)!, "b (restored).tmp")));
        Assert.Empty(Browser.List());
    }

    [Fact]
    public async Task Restore_refuses_unknown_and_already_restored_ids()
    {
        await QuarantineTwoFilesAsync();
        string id = Browser.List()[0].Id;
        Assert.True(Assert.Single(Browser.Restore([id])).Restored);

        var again = Browser.Restore([id, "q999"]);

        Assert.False(again[0].Restored);
        Assert.Contains("no longer in the quarantine folder", again[0].Detail);
        Assert.False(again[1].Restored);
        Assert.Contains("Unknown quarantine id", again[1].Detail);
    }

    [Fact]
    public void Restore_rejects_too_many_ids()
    {
        var ex = Assert.Throws<McpException>(() => Browser.Restore(Enumerable.Range(0, 101).Select(i => $"q{i}").ToList()));
        Assert.Contains("100", ex.Message);
    }
}
