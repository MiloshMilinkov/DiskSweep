/*
 * DiskSweep
 * Copyright (c) 2026 Miloš Milinkov. All rights reserved.
 * Proprietary and confidential. Use, copying, modification or distribution
 * without written permission is prohibited. See the LICENSE file.
 */

using DiskSweep.Core;
using DiskSweep.Core.Cleanup;
using DiskSweep.Core.Logging;
using DiskSweep.Core.Models;
using DiskSweep.Core.Safety;
using DiskSweep.Core.Scanning;
using DiskSweep.Core.SystemInfo;

namespace DiskSweep.Mcp;

public delegate Task<ScanResult> ScanRunner(ScanRequest request, ScanStatus status, CancellationToken token);

/// <summary>Everything the server needs from the real machine, in one place so tests can swap it out.</summary>
public sealed class SweepEnvironment
{
    public required ScanRunner RunScan { get; init; }
    public required Func<IReadOnlyList<DriveSummary>> GetDrives { get; init; }

    /// <summary>The quarantine folder for an item's path (the app uses "&lt;drive&gt;:\DiskSweep Quarantine").</summary>
    public required Func<string, string> QuarantineRootFor { get; init; }

    /// <summary>Every quarantine folder to list and restore from.</summary>
    public required Func<IEnumerable<string>> QuarantineRoots { get; init; }

    public required IApprovalPrompt Approval { get; init; }
    public required IActionLog Log { get; init; }
    public TimeProvider Clock { get; init; } = TimeProvider.System;
    public PlanLimits Limits { get; init; } = new();

    /// <summary>The real PC: DiskSweep's scan engine, local drives and each drive's own quarantine folder.</summary>
    public static SweepEnvironment ForThisPc(IActionLog log, IApprovalPrompt approval) => new()
    {
        RunScan = (request, status, token) => new ScanEngine().RunAsync(request, status, log, token),
        GetDrives = DriveCatalog.GetDrives,
        QuarantineRootFor = AppPaths.QuarantineRootFor,
        QuarantineRoots = () => DriveCatalog.GetDrives().Select(d => AppPaths.QuarantineRootFor(d.RootPath)),
        Approval = approval,
        Log = log,
    };

    /// <summary>A cleanup service that can only ever move into quarantine: its Recycle Bin refuses everything.</summary>
    internal CleanupService NewCleanupService(SafetyGuard guard) =>
        new(guard, Log, new QuarantineStore(QuarantineRootFor), RefusingRecycleBin.Instance);
}
