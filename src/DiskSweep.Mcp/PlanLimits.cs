/*
 * DiskSweep
 * Copyright (c) 2026 Miloš Milinkov. All rights reserved.
 * Proprietary and confidential. Use, copying, modification or distribution
 * without written permission is prohibited. See the LICENSE file.
 */

using DiskSweep.Core.Models;

namespace DiskSweep.Mcp;

/// <summary>How much one plan may hold and how long scans and plans stay usable. Agreed with the owner (2026-11-26).</summary>
public sealed record PlanLimits
{
    public int MaxItems { get; init; } = 100;
    public long MaxBytes { get; init; } = 10L * 1024 * 1024 * 1024;
    public TimeSpan PlanLifetime { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan MaxScanAge { get; init; } = TimeSpan.FromMinutes(60);
    public int MaxRestoreEntries { get; init; } = 100;
}

/// <summary>
/// What the MCP server adds on top of the app's selection policy: an agent may only plan Safe and Review files and
/// folders. Programs, registry keys and Risky items stay with a person in the DiskSweep app.
/// </summary>
public static class PlanItemRule
{
    /// <summary>Why <paramref name="item"/> can never go into a plan, or null when it may (the safety checks still apply).</summary>
    public static string? WhyNot(ScanItem item) => item switch
    {
        { Kind: ItemKind.Program } =>
            "Programs are removed only by their own uninstaller, from the DiskSweep app. The MCP server never runs uninstallers.",
        { Kind: ItemKind.RegistryKey } =>
            "Registry entries are report only. DiskSweep never edits the registry.",
        { IsReportOnly: true } =>
            item.ReportOnlyReason ?? "Report only: DiskSweep shows it but never removes it.",
        { Kind: not (ItemKind.File or ItemKind.Folder) } =>
            "Only files and folders can go in a plan.",
        { Risk: RiskLevel.Risky } =>
            "Risky items need a person to look at them one at a time in the DiskSweep app. The MCP server never plans them.",
        _ => null,
    };
}
