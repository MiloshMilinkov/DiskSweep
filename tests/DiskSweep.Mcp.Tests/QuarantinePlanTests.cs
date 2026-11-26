using DiskSweep.Core.Cleanup;
using DiskSweep.Core.Models;
using ModelContextProtocol;

namespace DiskSweep.Mcp.Tests;

/// <summary>quarantine_plan: a person approves, each plan works once and briefly, and items only ever go to quarantine.</summary>
public sealed class QuarantinePlanTests : IDisposable
{
    private readonly McpTestBed _bed = new();

    public void Dispose() => _bed.Dispose();

    private CleanupPlanner Planner => _bed.Planner;
    private PlanExecutor Executor => _bed.Executor;

    private async Task<(string PlanId, ScanItem A, ScanItem B)> PlanTwoFilesAsync()
    {
        var a = _bed.AddFile("a.tmp", content: "aa");
        var b = _bed.AddFile(@"sub\b.tmp", content: "bbb");
        await _bed.ScanAsync();
        string planId = Planner.Propose([_bed.Id(a), _bed.Id(b)]).PlanId!;
        return (planId, a, b);
    }

    [Fact]
    public async Task Approved_plan_moves_items_into_quarantine_only()
    {
        var (planId, a, b) = await PlanTwoFilesAsync();

        var result = Executor.Execute(planId, "test-client", CancellationToken.None);

        Assert.True(result.Approved);
        Assert.Equal(2, result.Moved.Count);
        Assert.Equal(5, result.MovedBytes);
        Assert.Empty(result.NotMoved);
        Assert.False(File.Exists(a.Path));
        Assert.False(File.Exists(b.Path));
        var batch = Assert.Single(QuarantineStore.LoadAll([_bed.QuarantineRoot]));
        Assert.Equal(2, batch.Entries.Count(e => e.State == QuarantineState.Quarantined));
        Assert.DoesNotContain(Directory.EnumerateFiles(_bed.Tree.Root, "*", SearchOption.AllDirectories),
            f => !f.StartsWith(_bed.QuarantineRoot, StringComparison.OrdinalIgnoreCase));

        var shown = Assert.Single(_bed.Approval.Shown);
        Assert.Equal(("test-client", 2, 5L), (shown.ClientName, shown.ItemCount, shown.TotalBytes));
    }

    [Fact]
    public async Task Declined_plan_moves_nothing_and_is_used_up()
    {
        var (planId, a, b) = await PlanTwoFilesAsync();
        _bed.Approval.Answer = false;

        var result = Executor.Execute(planId, "test-client", CancellationToken.None);

        Assert.False(result.Approved);
        Assert.Contains("declined", result.Message);
        Assert.Empty(result.Moved);
        Assert.True(File.Exists(a.Path) && File.Exists(b.Path));
        var again = Assert.Throws<McpException>(() => Executor.Execute(planId, "test-client", CancellationToken.None));
        Assert.Contains("already used", again.Message);
    }

    [Fact]
    public async Task Expired_plan_is_refused_before_the_dialog()
    {
        var (planId, a, _) = await PlanTwoFilesAsync();
        _bed.Clock.Advance(TimeSpan.FromMinutes(11));

        var ex = Assert.Throws<McpException>(() => Executor.Execute(planId, "test-client", CancellationToken.None));

        Assert.Contains("expired", ex.Message);
        Assert.Empty(_bed.Approval.Shown);
        Assert.True(File.Exists(a.Path));
    }

    [Fact]
    public async Task Plan_that_expires_while_the_dialog_is_open_moves_nothing()
    {
        var (planId, a, b) = await PlanTwoFilesAsync();
        _bed.Approval.OnShow = () => _bed.Clock.Advance(TimeSpan.FromMinutes(11));

        var result = Executor.Execute(planId, "test-client", CancellationToken.None);

        Assert.False(result.Approved);
        Assert.Contains("expired", result.Message);
        Assert.True(File.Exists(a.Path) && File.Exists(b.Path));
    }

    [Fact]
    public async Task Cancelled_call_moves_nothing()
    {
        var (planId, a, b) = await PlanTwoFilesAsync();
        using var cts = new CancellationTokenSource();
        _bed.Approval.OnShow = cts.Cancel;

        var result = Executor.Execute(planId, "test-client", cts.Token);

        Assert.False(result.Approved);
        Assert.True(File.Exists(a.Path) && File.Exists(b.Path));
    }

    [Fact]
    public async Task Forged_blank_and_foreign_plan_ids_are_refused()
    {
        var (_, a, _) = await PlanTwoFilesAsync();
        string foreign = new PlanStore(_bed.Session.Env).Issue(_bed.NewGuard(), []).Id; // another server session's plan

        foreach (string id in new[] { Guid.NewGuid().ToString("N"), "", "   ", foreign })
        {
            var ex = Assert.Throws<McpException>(() => Executor.Execute(id, "test-client", CancellationToken.None));
            Assert.Contains("not issued by this DiskSweep session", ex.Message);
        }
        Assert.Empty(_bed.Approval.Shown);
        Assert.True(File.Exists(a.Path));
    }

    [Fact]
    public async Task Item_changed_after_propose_is_left_in_place()
    {
        var (planId, a, b) = await PlanTwoFilesAsync();
        File.WriteAllText(a.Path, "changed, and longer");

        var result = Executor.Execute(planId, "test-client", CancellationToken.None);

        Assert.True(result.Approved);
        Assert.Equal(b.Path, Assert.Single(result.Moved).Path);
        Assert.Contains("changed since the scan", Assert.Single(result.NotMoved).Reason);
        Assert.Equal("changed, and longer", File.ReadAllText(a.Path));
    }

    [Fact]
    public async Task Folder_that_gains_a_junction_after_propose_is_left_in_place()
    {
        _bed.Tree.File(@"leftover\a.txt", "x");
        var folder = _bed.AddFolder("leftover");
        await _bed.ScanAsync();
        string planId = Planner.Propose([_bed.Id(folder)]).PlanId!;
        string target = _bed.Tree.Dir("target");
        _bed.Tree.File(@"target\keep.txt", "keep");
        _bed.Tree.Junction(@"leftover\link", target);

        var result = Executor.Execute(planId, "test-client", CancellationToken.None);

        Assert.Empty(result.Moved);
        Assert.Contains("junction", Assert.Single(result.NotMoved).Reason, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(folder.Path));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(target, "keep.txt")));
    }

    [Fact]
    public void The_recycle_bin_stand_in_refuses_everything()
    {
        string file = _bed.Tree.File("x.tmp");

        var result = RefusingRecycleBin.Instance.Recycle(file, 1);

        Assert.Equal(RecycleStatus.Refused, result.Status);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void Approval_text_lists_ten_paths_then_a_count()
    {
        var paths = Enumerable.Range(1, 12).Select(i => $@"C:\junk\file{i:00}.tmp").ToList();

        string text = WindowsApprovalPrompt.Text(new ApprovalRequest("claude-code", 12, 2048, paths));

        Assert.Contains("\"claude-code\"", text);
        Assert.Contains("12 item(s), 2 KB", text);
        Assert.Contains(@"C:\junk\file10.tmp", text);
        Assert.DoesNotContain(@"C:\junk\file11.tmp", text);
        Assert.Contains("…and 2 more", text);
    }

    [Fact]
    public void Approval_text_keeps_a_client_name_short_and_on_one_line()
    {
        string text = WindowsApprovalPrompt.Text(new ApprovalRequest("evil\r\nWindows Update" + new string('x', 200), 1, 1, ["C:\\a"]));

        string firstLine = text.Split('\n')[0];
        Assert.Contains("evil Windows Update", firstLine);
        Assert.True(firstLine.Length < 220);
    }
}
