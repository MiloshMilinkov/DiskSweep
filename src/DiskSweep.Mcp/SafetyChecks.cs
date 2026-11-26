/*
 * DiskSweep
 * Copyright (c) 2026 Miloš Milinkov. All rights reserved.
 * Proprietary and confidential. Use, copying, modification or distribution
 * without written permission is prohibited. See the LICENSE file.
 */

using DiskSweep.Core.Models;
using DiskSweep.Core.Safety;

namespace DiskSweep.Mcp;

/// <summary>
/// Spells out, check by check, what the safety layer says about one item right now. It only reports: every check is
/// Core's own (<see cref="SafetyGuard"/>, <see cref="Core.Cleanup.CleanupService.Preflight"/>) or the MCP plan rule.
/// </summary>
internal static class SafetyChecks
{
    public static IReadOnlyList<SafetyCheck> Run(SweepSession session, ScanSnapshot scan, ScanItem item)
    {
        var checks = new List<SafetyCheck>(8);
        bool fileOrFolder = item.Kind is ItemKind.File or ItemKind.Folder;
        bool reportOnly = item.IsReportOnly || item.Kind == ItemKind.RegistryKey;
        checks.Add(new("Files and folders only", fileOrFolder,
            fileOrFolder ? $"It's a {item.Kind.ToString().ToLowerInvariant()}." : PlanItemRule.WhyNot(item) ?? "Not a file or folder."));
        checks.Add(new("Not report-only", !reportOnly,
            reportOnly ? PlanItemRule.WhyNot(item) ?? "Report only." : "DiskSweep may remove it."));

        if (!fileOrFolder || reportOnly)
        {
            foreach (string name in (string[])["Risk is Safe or Review", "No links found by the scan", "Path rules", "File system", "Pre-move check", "Scan is fresh"])
                checks.Add(new(name, false, "Not run: only files and folders can go in a plan."));
            return checks;
        }

        checks.Add(new("Risk is Safe or Review", item.Risk != RiskLevel.Risky,
            item.Risk == RiskLevel.Risky ? PlanItemRule.WhyNot(item)! : $"Risk level: {item.Risk}."));
        checks.Add(new("No links found by the scan", item.ContainsReparsePoint is null,
            item.ContainsReparsePoint is { } link ? $"Contains a junction, symbolic link or other reparse point: {link}" : "None found."));

        if (scan.Result.Guard is not { } guard)
        {
            foreach (string name in (string[])["Path rules", "File system", "Pre-move check"])
                checks.Add(new(name, false, "Not run: this scan has no safety guard. Scan again."));
        }
        else
        {
            if (PathNormalizer.TryNormalize(item.Path, out string path, out string error))
            {
                checks.Add(Verdict("Path rules", guard.CheckRules(path), "No protection rule covers it."));
                checks.Add(Verdict("File system", guard.CheckFileSystem(path), "It exists, isn't a link or cloud file, and no parent folder is a link."));
            }
            else
            {
                checks.Add(new("Path rules", false, error));
                checks.Add(new("File system", false, "Not run: the path isn't valid."));
            }

            var preflight = session.Env.NewCleanupService(guard).Preflight(item);
            checks.Add(new("Pre-move check", preflight.CanMove, preflight.Refusal ??
                $"Unchanged since the scan, not in use, nothing inside is a link or unreadable ({CandidateItem.SizeText(preflight.SizeBytes)} now)."));
        }

        bool fresh = !session.IsStale(scan);
        checks.Add(new("Scan is fresh", fresh, fresh
            ? $"Scanned less than {session.Env.Limits.MaxScanAge.TotalMinutes:0} minutes ago."
            : $"The scan is older than {session.Env.Limits.MaxScanAge.TotalMinutes:0} minutes. Scan again."));
        return checks;
    }

    private static SafetyCheck Verdict(string name, SafetyVerdict verdict, string passed) =>
        new(name, verdict.IsAllowed, verdict.IsAllowed ? passed : verdict.Reason);
}
