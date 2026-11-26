using System.IO.Pipelines;
using System.Text.Json;
using DiskSweep.Core.Cleanup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DiskSweep.Mcp.Tests;

/// <summary>
/// The real server wiring (<see cref="ServerSetup.AddDiskSweep"/>) driven by the SDK's own client over in-memory
/// streams: what a client actually sees, from tool names and annotations to errors.
/// </summary>
public sealed class ProtocolTests : IAsyncLifetime
{
    private static readonly string[] ReadOnlyTools = ["scan", "list_candidates", "explain_item", "list_quarantine", "propose_cleanup"];

    private readonly McpTestBed _bed = new();
    private IHost? _host;
    private McpClient? _client;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_client is not null) await _client.DisposeAsync();
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
        _bed.Dispose();
    }

    private async Task<McpClient> ConnectAsync()
    {
        var toServer = new Pipe();
        var toClient = new Pipe();
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddDiskSweep(_bed.Env()).WithStreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream());
        _host = builder.Build();
        await _host.StartAsync();

        _client = await McpClient.CreateAsync(
            new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()),
            new McpClientOptions { ClientInfo = new() { Name = "protocol-test", Version = "1.0" } });
        return _client;
    }

    private static async Task<JsonElement> CallAsync(McpClient client, string tool, Dictionary<string, object?>? args = null)
    {
        var result = await client.CallToolAsync(tool, args ?? []);
        string text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.True(result.IsError != true, $"{tool} failed: {text}");
        return JsonDocument.Parse(text).RootElement;
    }

    [Fact]
    public async Task Server_exposes_seven_tools_with_honest_annotations()
    {
        var client = await ConnectAsync();

        var tools = (await client.ListToolsAsync()).Select(t => t.ProtocolTool).ToDictionary(t => t.Name);

        Assert.Equal(
            ["explain_item", "list_candidates", "list_quarantine", "propose_cleanup", "quarantine_plan", "restore_from_quarantine", "scan"],
            tools.Keys.Order());
        foreach (var (name, tool) in tools)
        {
            Assert.Equal(ReadOnlyTools.Contains(name), tool.Annotations?.ReadOnlyHint);
            Assert.False(tool.Annotations?.OpenWorldHint);
            Assert.Contains("Can't:", tool.Description);
        }
        Assert.True(tools["quarantine_plan"].Annotations?.DestructiveHint);
    }

    [Fact]
    public async Task Scan_propose_and_an_approved_plan_work_end_to_end()
    {
        var file = _bed.AddFile("a.tmp", content: "hello");
        var client = await ConnectAsync();

        var scan = await CallAsync(client, "scan", new() { ["target"] = _bed.Tree.Root });
        Assert.Equal(1, scan.GetProperty("itemCount").GetInt32());
        string id = (await CallAsync(client, "list_candidates")).GetProperty("items")[0].GetProperty("id").GetString()!;

        var proposal = await CallAsync(client, "propose_cleanup", new() { ["itemIds"] = new[] { id } });
        string planId = proposal.GetProperty("planId").GetString()!;
        Assert.True(File.Exists(file.Path));
        Assert.Empty(_bed.Approval.Shown);

        var run = await CallAsync(client, "quarantine_plan", new() { ["planId"] = planId });

        Assert.True(run.GetProperty("approved").GetBoolean());
        Assert.False(File.Exists(file.Path));
        Assert.Equal("protocol-test", Assert.Single(_bed.Approval.Shown).ClientName);
        Assert.Single(QuarantineStore.LoadAll([_bed.QuarantineRoot]));
        Assert.Equal(1, (await CallAsync(client, "list_quarantine")).GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task A_forged_plan_id_comes_back_as_a_tool_error_without_a_dialog()
    {
        var client = await ConnectAsync();

        var result = await client.CallToolAsync("quarantine_plan", new Dictionary<string, object?> { ["planId"] = Guid.NewGuid().ToString("N") });

        Assert.True(result.IsError);
        Assert.Contains("not issued by this DiskSweep session", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.Empty(_bed.Approval.Shown);
    }
}
