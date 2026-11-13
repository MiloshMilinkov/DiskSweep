/*
 * DiskSweep
 * Copyright (c) 2026 MiloÅ¡ Milinkov. All rights reserved.
 * Proprietary and confidential. Use, copying, modification or distribution
 * without written permission is prohibited. See the LICENSE file.
 */

using System.Diagnostics;
using DiskSweep.Core.Logging;
using DiskSweep.Core.Models;
using DiskSweep.Core.Safety;
using DiskSweep.Core.SystemInfo;

namespace DiskSweep.Core.Scanning;

/// <summary>Runs the selected scanners on a background thread. Read-only: nothing is moved, deleted or edited.</summary>
public sealed class ScanEngine
{
    private readonly IReadOnlyList<IScanner> _scanners =
    [
        new EmptyFolderScanner(),
        new LeftoverScanner(),
        new UnusedProgramScanner(),
        new TempAndOldFileScanner(),
    ];

    public Task<ScanResult> RunAsync(ScanRequest request, ScanStatus status, IActionLog log, CancellationToken token) =>
        Task.Run(() => Run(request, status, log, token), CancellationToken.None);

    private ScanResult Run(ScanRequest request, ScanStatus status, IActionLog log, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        var skips = new SkipLog(log);
        var notes = new List<string>();
        var selected = _scanners.Where(s => request.Categories.Contains(s.Category)).ToList();
        bool cancelled = false;
        ScanContext? ctx = null;
        bool isAdmin = SystemSnapshot.IsRunningAsAdministrator();

        log.Info($"Scan started on {request.Drive.RootPath} ({string.Join(", ", selected.Select(s => s.Category))}), " +
                 $"{(isAdmin ? "as administrator" : "as standard user")}. Read-only.");

        const double snapshotWeight = 8;
        status.Plan(snapshotWeight + selected.Sum(s => s.Weight));

        try
        {
            status.BeginPhase("Reading installed programs and system settings", snapshotWeight);
            var snapshot = SystemSnapshot.Capture(request.Categories, request.Options, isAdmin, log, token);
            var guard = new SafetyGuard(snapshot.Layout);

            ctx = new ScanContext
            {
                Request = request,
                Snapshot = snapshot,
                Guard = guard,
                Status = status,
                Skips = skips,
                Log = log,
                Token = token,
            };

            if (!isAdmin)
                notes.Add("Running without administrator rights: Windows\\Temp, Prefetch, other users' folders and some scheduled tasks couldn't be checked, so leftovers are marked Risky.");
            if (snapshot.References is { IncompleteSources.Count: > 0 } refs && isAdmin)
                notes.AddRange(refs.IncompleteSources);
            foreach (var u in snapshot.Profiles.Users.Where(u => !u.HiveReadable))
                notes.Add($"The registry of user {u.DisplayName} isn't loaded (they're signed out), so their programs and startup entries couldn't be checked.");
            if (snapshot.CloudRoots.Count > 0)
                notes.Add($"Skipped {snapshot.CloudRoots.Count} cloud-synced folder(s): {string.Join(", ", snapshot.CloudRoots)}.");

            foreach (var scanner in selected)
            {
                token.ThrowIfCancellationRequested();
                status.BeginPhase(scanner.PhaseName, scanner.Weight);
                var before = ctx.ItemCount;
                var phaseClock = Stopwatch.StartNew();
                try
                {
                    scanner.Scan(ctx);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    log.Error($"{scanner.PhaseName} failed", ex);
                    notes.Add($"{ScanCategoryInfo.Title(scanner.Category)}: the scan stopped early because of an error ({ex.Message}). Results shown are partial.");
                }
                log.Info($"{scanner.PhaseName}: {ctx.ItemCount - before} items in {phaseClock.Elapsed.TotalSeconds:0.0}s.");
            }
            status.Complete();
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            notes.Insert(0, "The scan was cancelled. Results are partial.");
            log.Info("Scan cancelled by the user.");
        }
        catch (Exception ex)
        {
            log.Error("Scan failed", ex);
            notes.Insert(0, $"The scan failed: {ex.Message}");
        }

        var items = ctx?.Items ?? [];
        log.Info($"Scan finished in {clock.Elapsed.TotalSeconds:0.0}s: {items.Count} items, {skips.Total} skipped.");
        log.Flush();

        return new ScanResult
        {
            Request = request,
            Items = items,
            Skipped = skips.Snapshot(),
            SkippedTotal = skips.Total,
            Notes = notes,
            WasCancelled = cancelled,
            Duration = clock.Elapsed,
            Categories = ScanCategoryInfo.ResultCategories(request.Categories),
            LogFilePath = log.FilePath,
            Guard = ctx?.Guard,
        };
    }
}
