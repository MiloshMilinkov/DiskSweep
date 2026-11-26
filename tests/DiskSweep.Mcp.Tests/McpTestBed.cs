using DiskSweep.Core.IO;
using DiskSweep.Core.Logging;
using DiskSweep.Core.Models;
using DiskSweep.Core.Safety;
using DiskSweep.Core.Scanning;
using DiskSweep.Core.Tests;

namespace DiskSweep.Mcp.Tests;

/// <summary>A clock tests move by hand, for scan age and plan expiry.</summary>
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>Stands in for the Windows dialog: answers as told and records what it was shown. Never shows anything.</summary>
internal sealed class FakeApproval : IApprovalPrompt
{
    public bool Answer { get; set; } = true;
    public Action? OnShow { get; set; }
    public List<ApprovalRequest> Shown { get; } = [];

    public bool Confirm(ApprovalRequest request)
    {
        Shown.Add(request);
        OnShow?.Invoke();
        return Answer;
    }
}

/// <summary>
/// One server session over a throw-away folder. Scans return the items a test adds (never a real scan), the guard
/// is the fake Q: PC from <see cref="TestLayouts"/>, and quarantine lives inside the folder, so nothing outside
/// it is ever touched.
/// </summary>
internal sealed class McpTestBed : IDisposable
{
    private SweepSession? _session;

    public TempTree Tree { get; } = new();
    public ManualClock Clock { get; } = new(new DateTimeOffset(2026, 11, 26, 10, 0, 0, TimeSpan.Zero));
    public FakeApproval Approval { get; } = new();
    public List<ScanItem> NextScanItems { get; } = [];
    public string[] Exclusions { get; set; } = [];
    public PlanLimits Limits { get; set; } = new();

    /// <summary>Replaces the canned scan, for example to hold a scan open.</summary>
    public ScanRunner? Runner { get; set; }

    public string QuarantineRoot => Path.Combine(Tree.Root, "quarantine");
    public string DriveRoot => PathUtil.DriveRoot(Tree.Root);

    public SweepSession Session => _session ??= new SweepSession(Env());

    public SweepEnvironment Env() => new()
    {
        RunScan = (request, status, token) => Runner is { } r ? r(request, status, token) : Task.FromResult(BuildResult(request)),
        GetDrives = () => [new DriveSummary(DriveRoot, "Test", "NTFS", DriveType.Fixed, 1L << 40, 1L << 39, false)],
        QuarantineRootFor = _ => QuarantineRoot,
        QuarantineRoots = () => [QuarantineRoot],
        Approval = Approval,
        Clock = Clock,
        Log = NullActionLog.Instance,
        Limits = Limits,
    };

    public ScanResult BuildResult(ScanRequest request) => new()
    {
        Request = request,
        Items = NextScanItems.ToList(),
        Skipped = [],
        SkippedTotal = 0,
        Notes = [],
        WasCancelled = false,
        Duration = TimeSpan.FromSeconds(1),
        Categories = ScanCategoryInfo.ResultCategories(request.Categories),
        Guard = new SafetyGuard(TestLayouts.Synthetic(Exclusions), Win32FileAttributeProbe.Instance),
    };

    public ScanItem Add(ScanItem item)
    {
        NextScanItems.Add(item);
        return item;
    }

    /// <summary>A file the "scanner" found, aged 30 days, with its size and date recorded as a scanner records them.</summary>
    public ScanItem AddFile(string relative, RiskLevel risk = RiskLevel.Safe, string content = "x",
        ScanCategory category = ScanCategory.TempAndOldFiles)
    {
        string path = Tree.File(relative, content);
        TempTree.Age(path, 30);
        var info = new FileInfo(path);
        return Add(new ScanItem
        {
            Category = category, Kind = ItemKind.File, Path = path, Reason = "test", Risk = risk,
            SizeBytes = info.Length, LastModifiedUtc = info.LastWriteTimeUtc,
        });
    }

    /// <summary>A folder (create its files first) with its size and newest change recorded as a scanner records them.</summary>
    public ScanItem AddFolder(string relative, RiskLevel risk = RiskLevel.Review,
        ScanCategory category = ScanCategory.TempAndOldFiles)
    {
        string path = Tree.Dir(relative);
        var stats = FolderMeasure.Measure(path, CancellationToken.None);
        return Add(new ScanItem
        {
            Category = category, Kind = ItemKind.Folder, Path = path, Reason = "test", Risk = risk,
            SizeBytes = stats.TotalBytes, LastModifiedUtc = stats.NewestChangeUtc,
        });
    }

    /// <summary>Scans the whole test folder (or <paramref name="target"/>).</summary>
    public Task<ScanSnapshot> ScanAsync(string? target = null) =>
        Session.ScanAsync(target ?? Tree.Root, null, null, CancellationToken.None);

    /// <summary>The id the latest scan gave <paramref name="item"/>.</summary>
    public string Id(ScanItem item)
    {
        var scan = Session.Latest ?? throw new InvalidOperationException("Scan first.");
        for (int i = 0; i < scan.Items.Count; i++)
            if (ReferenceEquals(scan.Items[i], item)) return scan.IdOf(i);
        throw new InvalidOperationException($"{item.Path} isn't in the latest scan.");
    }

    public void Dispose() => Tree.Dispose();
}
