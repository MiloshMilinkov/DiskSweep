/*
 * DiskSweep
 * Copyright (c) 2026 Miloš Milinkov. All rights reserved.
 * Proprietary and confidential. Use, copying, modification or distribution
 * without written permission is prohibited. See the LICENSE file.
 */

using System.Diagnostics.CodeAnalysis;
using DiskSweep.Core.IO;
using DiskSweep.Core.Models;
using DiskSweep.Core.Safety;
using DiskSweep.Core.Scanning;
using ModelContextProtocol;

namespace DiskSweep.Mcp;

/// <summary>One finished scan. <see cref="Items"/> holds only what is in scope; an item's id is its index there.</summary>
/// <param name="Folder">The folder the scan was narrowed to, or null for the whole drive.</param>
/// <param name="Paths">Each item's path in the safety layer's form (see <see cref="SweepSession.Normalized"/>), worked out once.</param>
public sealed record ScanSnapshot(int Number, ScanResult Result, string? Folder, DateTimeOffset FinishedAt,
    IReadOnlyList<ScanItem> Items, IReadOnlyList<string> Paths)
{
    public string Target => Folder ?? Result.Request.Drive.RootPath;
    public string IdOf(int index) => $"{Number}-{index}";
    public bool IsUnder(int index, string folder) => PathUtil.IsSameOrUnder(Paths[index], folder);
}

/// <summary>An item of the latest scan, found by its id.</summary>
public sealed record ItemRef(ScanSnapshot Scan, int Index)
{
    public ScanItem Item => Scan.Items[Index];

    /// <summary>The canonical id, however the caller spelled it ("3-017" and " 3-17" both become "3-17").</summary>
    public string Id => Scan.IdOf(Index);

    public string Path => Scan.Paths[Index];
}

/// <summary>
/// What this server session knows: the latest scan and the ids it handed out. Write tools only ever accept these
/// ids, never a path the agent typed. Everything lives in memory, so a restarted server starts empty.
/// </summary>
public sealed class SweepSession(SweepEnvironment env)
{
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly object _gate = new();
    private readonly Dictionary<string, (string BatchFolder, int Slot)> _quarantineIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string, int), string> _quarantineIdsByEntry = [];
    private ScanSnapshot? _latest;

    public SweepEnvironment Env { get; } = env;
    public ScanSnapshot? Latest => Volatile.Read(ref _latest);

    /// <summary>Scans a drive or a folder (a folder scan scans its drive and keeps what is under the folder). One at a time.</summary>
    public async Task<ScanSnapshot> ScanAsync(string target, IReadOnlyList<string>? categories,
        IProgress<ProgressNotificationValue>? progress, CancellationToken token)
    {
        var (drive, folder) = ResolveTarget(target);
        var selected = (categories ?? []).Select(c => ParseEnum(c, "category", ScanCategoryInfo.Selectable)).OfType<ScanCategory>().ToHashSet();
        if (selected.Count == 0) selected = [.. ScanCategoryInfo.Selectable];
        if (!_scanGate.Wait(0)) throw new McpException("A scan is already running. Wait for it to finish.");
        try
        {
            var status = new ScanStatus();
            var scan = Env.RunScan(new ScanRequest(drive, selected, new ScanOptions()), status, token);
            if (progress is not null)
            {
                while (!scan.IsCompleted)
                {
                    progress.Report(new ProgressNotificationValue { Progress = (float)(status.Fraction * 100), Total = 100, Message = status.Phase });
                    await Task.WhenAny(scan, Task.Delay(500, CancellationToken.None));
                }
            }
            var result = await scan;

            var items = new List<ScanItem>(result.Items.Count);
            var paths = new List<string>(result.Items.Count);
            foreach (var item in result.Items)
            {
                string path = Normalized(item.Path);
                if (folder is not null && !PathUtil.IsSameOrUnder(path, folder)) continue;
                items.Add(item);
                paths.Add(path);
            }

            // Scans run one at a time (the gate above), so the next number can't be taken twice.
            var snapshot = new ScanSnapshot((Latest?.Number ?? 0) + 1, result, folder, Env.Clock.GetUtcNow(), items, paths);
            Volatile.Write(ref _latest, snapshot);
            Env.Log.Info($"MCP: scan {snapshot.Number} of {snapshot.Target}: {items.Count} item(s).");
            return snapshot;
        }
        finally
        {
            _scanGate.Release();
        }
    }

    /// <summary>Finds an item of the latest scan by its id.</summary>
    public bool TryGetItem(string id, [NotNullWhen(true)] out ItemRef? found, out string error)
    {
        found = null;
        error = "";
        if (Latest is not { } latest)
        {
            error = Texts.NoScan;
            return false;
        }

        string[] parts = (id ?? "").Trim().Split('-');
        if (parts.Length == 2 && int.TryParse(parts[0], out int number) && int.TryParse(parts[1], out int index))
        {
            if (number != latest.Number)
            {
                error = $"Item id \"{id}\" is from an older scan; use ids from the latest scan.";
                return false;
            }
            if (index >= 0 && index < latest.Items.Count)
            {
                found = new ItemRef(latest, index);
                return true;
            }
        }
        error = $"Unknown item id \"{id}\". Use ids from list_candidates.";
        return false;
    }

    /// <summary>Why items from <paramref name="scan"/> may no longer be planned, or null while it is fresh.</summary>
    public string? StaleReason(ScanSnapshot scan) =>
        Env.Clock.GetUtcNow() - scan.FinishedAt > Env.Limits.MaxScanAge
            ? $"The scan is older than {Env.Limits.MaxScanAge.TotalMinutes:0} minutes. Scan again."
            : null;

    /// <summary>A stable id for a quarantine entry within this session ("q1", "q2"…).</summary>
    internal string QuarantineIdFor(string batchFolder, int slot)
    {
        lock (_gate)
        {
            var key = (batchFolder.ToUpperInvariant(), slot);
            if (_quarantineIdsByEntry.TryGetValue(key, out string? id)) return id;
            id = $"q{_quarantineIds.Count + 1}";
            _quarantineIds[id] = (batchFolder, slot);
            _quarantineIdsByEntry[key] = id;
            return id;
        }
    }

    internal bool TryResolveQuarantineId(string id, out string batchFolder, out int slot)
    {
        lock (_gate)
        {
            bool found = _quarantineIds.TryGetValue((id ?? "").Trim(), out var entry);
            (batchFolder, slot) = entry;
            return found;
        }
    }

    /// <summary>The safety layer's form of a path (full, long names, no trailing separator), for comparing paths.</summary>
    internal static string Normalized(string path) =>
        PathNormalizer.TryNormalize(path, out string p, out _) ? p : PathUtil.NormalizeLoose(path) ?? path;

    /// <summary>Parses an enum name (any case), optionally limited to <paramref name="allowed"/>; null for an empty value.</summary>
    internal static T? ParseEnum<T>(string? value, string what, IReadOnlyCollection<T>? allowed = null) where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var valid = allowed ?? Enum.GetValues<T>();
        if (Enum.TryParse(value.Trim(), ignoreCase: true, out T parsed) && valid.Contains(parsed)) return parsed;
        throw new McpException($"Unknown {what} \"{value}\". Use any of: {string.Join(", ", valid)}.");
    }

    private (DriveSummary Drive, string? Folder) ResolveTarget(string target)
    {
        string t = (target ?? "").Trim().Replace('/', '\\');
        if (t.Length == 1 && char.IsAsciiLetter(t[0])) t += ":";
        if (t.Length == 2 && t[1] == ':') t += "\\";

        string root;
        string? folder = null;
        if (t.Length == 3 && PathUtil.IsDrivePath(t))
        {
            root = char.ToUpperInvariant(t[0]) + @":\";
        }
        else
        {
            if (!PathNormalizer.TryNormalize(t, out string p, out string error))
                throw new McpException($"Can't scan \"{target}\": {error} Give a drive (\"C:\") or a full folder path.");
            if (FastDirectory.GetAttributes(p, out _) is not { } attributes || (attributes & FileAttributes.Directory) == 0)
                throw new McpException($"\"{p}\" isn't an existing folder.");
            root = PathUtil.DriveRoot(p);
            if (p.Length > 3) folder = p;
        }

        var drives = Env.GetDrives();
        var drive = drives.FirstOrDefault(d => PathUtil.AreSame(d.RootPath, root))
            ?? throw new McpException($"{root} isn't a local drive DiskSweep can scan. Available: {string.Join(", ", drives.Select(d => d.Letter))}.");
        return (drive, folder);
    }
}
