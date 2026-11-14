using DiskSweep.Core.IO;
using DiskSweep.Core.Logging;
using DiskSweep.Core.Models;
using DiskSweep.Core.Safety;
using DiskSweep.Core.SystemInfo;

namespace DiskSweep.Core.Scanning;

/// <summary>Live progress, written by the engine thread and polled by the UI. Lock-free.</summary>
public sealed class ScanStatus
{
    private string _phase = "Preparing";
    private string _currentPath = "";
    private int _itemsFound;
    private long _entriesScanned;
    private double _fraction;
    private double _doneWeight, _totalWeight = 1, _currentWeight;

    public string Phase { get => Volatile.Read(ref _phase); set => Volatile.Write(ref _phase, value); }
    public string CurrentPath { get => Volatile.Read(ref _currentPath); set => Volatile.Write(ref _currentPath, value); }
    public int ItemsFound => Volatile.Read(ref _itemsFound);
    public long EntriesScanned => Interlocked.Read(ref _entriesScanned);

    /// <summary>Overall progress, 0–1.</summary>
    public double Fraction => Volatile.Read(ref _fraction);

    internal void AddItem() => Interlocked.Increment(ref _itemsFound);
    /// <summary>Counts one more entry checked (or, during cleanup, one more item handled).</summary>
    public void AddScanned() => Interlocked.Increment(ref _entriesScanned);

    internal void Plan(double totalWeight) => _totalWeight = Math.Max(totalWeight, 0.0001);

    internal void BeginPhase(string name, double weight)
    {
        _doneWeight += _currentWeight;
        _currentWeight = weight;
        Phase = name;
        SetPhaseProgress(0);
    }

    internal void SetPhaseProgress(double f) =>
        Volatile.Write(ref _fraction, Math.Clamp((_doneWeight + Math.Clamp(f, 0, 1) * _currentWeight) / _totalWeight, 0, 1));

    internal void Complete() => Volatile.Write(ref _fraction, 1);

    /// <summary>For steps outside the engine (like waiting for an uninstaller): sets what the progress screen shows.</summary>
    public void Show(string phase, string detail, double fraction)
    {
        Phase = phase;
        CurrentPath = detail;
        Volatile.Write(ref _fraction, Math.Clamp(fraction, 0, 1));
    }
}

public sealed class ScanContext
{
    public required ScanRequest Request { get; init; }
    public required SystemSnapshot Snapshot { get; init; }
    public required SafetyGuard Guard { get; init; }
    public required ScanStatus Status { get; init; }
    public required SkipLog Skips { get; init; }
    public required IActionLog Log { get; init; }
    public required CancellationToken Token { get; init; }
    public DateTime NowUtc { get; init; } = DateTime.UtcNow;

    private readonly List<ScanItem> _items = [];
    private readonly object _gate = new();
    private HashSet<string>? _tempRoots;

    public string DriveRoot => Request.Drive.RootPath;
    public ScanOptions Options => Request.Options;
    public bool IsOnDrive(string path) => PathUtil.IsOnDrive(path, DriveRoot);
    public bool IsSystemDrive => PathUtil.IsOnDrive(Snapshot.Layout.SystemDrive, DriveRoot);

    /// <summary>Whole days between <paramref name="utc"/> and the start of the scan.</summary>
    public int AgeDays(DateTime utc) => (int)(NowUtc - utc).TotalDays;

    /// <summary>The temp folders themselves (their contents belong to the temp-files category).</summary>
    public bool IsTempRoot(string path) =>
        (_tempRoots ??= new HashSet<string>(
            Snapshot.Layout.TempDirectories.Select(t => PathUtil.NormalizeLoose(t) ?? t), PathUtil.Comparer)).Contains(path);

    public void Add(ScanItem item)
    {
        lock (_gate) _items.Add(item);
        Status.AddItem();
    }

    /// <summary>A copy of the items found so far.</summary>
    public IReadOnlyList<ScanItem> Items
    {
        get { lock (_gate) return _items.ToList(); }
    }

    public int ItemCount
    {
        get { lock (_gate) return _items.Count; }
    }

    /// <summary>Skips (and logs) a link or a cloud file/folder. Links are never followed.</summary>
    public bool SkipIfLinkOrCloud(in FsEntry entry)
    {
        if (entry.IsReparsePoint)
        {
            Skips.Add(entry.FullPath, $"Link not followed ({FileAttributeFlags.DescribeReparseTag(entry.ReparseTag)}).");
            return true;
        }
        if (entry.IsCloudPlaceholder)
        {
            Skips.Add(entry.FullPath, entry.IsDirectory ? "Cloud-synced folder." : "Cloud placeholder file.");
            return true;
        }
        return false;
    }

    /// <summary>Skips a link, a cloud folder or a protected subtree, logging why.</summary>
    public bool ShouldSkipDirectory(in FsEntry entry)
    {
        if (SkipIfLinkOrCloud(entry)) return true;
        if (Guard.ProtectedSubtreeReason(entry.FullPath) is { } reason)
        {
            Skips.Add(entry.FullPath, reason);
            return true;
        }
        return false;
    }
}

public interface IScanner
{
    ScanCategory Category { get; }
    string PhaseName { get; }

    /// <summary>Rough share of the total scan time, used for the progress bar.</summary>
    double Weight { get; }

    void Scan(ScanContext context);
}
