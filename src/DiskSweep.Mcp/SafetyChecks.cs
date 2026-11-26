/*
 * DiskSweep
 * Copyright (c) 2026 Miloš Milinkov. All rights reserved.
 * Proprietary and confidential. Use, copying, modification or distribution
 * without written permission is prohibited. See the LICENSE file.
 */

using DiskSweep.Core.IO;
using DiskSweep.Core.Models;
using DiskSweep.Core.Safety;

namespace DiskSweep.Mcp;

/// <summary>
/// Spells out, check by check, what the safety layer says about one item right now. It only reports: every check is
/// Core's own (<see cref="SafetyGuard"/>, <see cref="Core.Cleanup.CleanupService.Preflight"/>) or the MCP plan rule,
/// and "can be planned" means every check passed, which covers everything <see cref="CleanupPlanner"/> checks.
/// </summary>
internal static class SafetyChecks
{
    private static readonly string[] GuardChecks = ["Path rules", "File system", "Pre-move check"];

    public static IReadOnlyList<SafetyCheck> Run(SweepSession session, ItemRef found)
    {
        var item = found.Item;
        var checks = new List<SafetyCheck>(8);
        bool fileOrFolder = item.Kind is ItemKind.File or ItemKind.Folder;
        bool reportOnly = item.IsReportOnly || item.Kind == ItemKind.RegistryKey;
        checks.Add(new("Files and folders only", fileOrFolder,
            fileOrFolder ? $"It's a {item.Kind.ToString().ToLowerInvariant()}." : PlanItemRule.WhyNot(item)!));
        checks.Add(new("Not report-only", !reportOnly, reportOnly ? PlanItemRule.WhyNot(item)! : "DiskSweep may remove it."));

        if (!fileOrFolder || reportOnly)
        {
            foreach (string name in (string[])["Risk is Safe or Review", "No links found by the scan", .. GuardChecks, "Scan is fresh"])
                checks.Add(new(name, false, "Not run: only files and folders can go in a plan."));
            return checks;
        }

        // For a file or folder that isn't report-only, the plan rule's only remaining refusal is a Risky item.
        string? rule = PlanItemRule.WhyNot(item);
        checks.Add(new("Risk is Safe or Review", rule is null, rule ?? $"Risk level: {item.Risk}."));
        checks.Add(new("No links found by the scan", item.ContainsReparsePoint is null,
            item.ContainsReparsePoint is { } link ? $"Contains a junction, symbolic link or other reparse point: {link}" : "None found."));

        if (found.Scan.Result.Guard is not { } guard)
        {
            foreach (string name in GuardChecks) checks.Add(new(name, false, $"Not run: {Texts.NoGuard}"));
        }
        else
        {
            if (PathNormalizer.TryNormalize(item.Path, out string path, out string error))
            {
                checks.Add(Verdict(GuardChecks[0], guard.CheckRules(path), "No protection rule covers it."));
                checks.Add(Verdict(GuardChecks[1], guard.CheckFileSystem(path), "It exists, isn't a link or cloud file, and no parent folder is a link."));
            }
            else
            {
                checks.Add(new(GuardChecks[0], false, error));
                checks.Add(new(GuardChecks[1], false, "Not run: the path isn't valid."));
            }

            var preflight = session.Env.NewCleanupService(guard).Preflight(item);
            checks.Add(new(GuardChecks[2], preflight.CanMove, preflight.Refusal ??
                $"Unchanged since the scan, not in use, nothing inside is a link or unreadable ({PathUtil.FormatBytes(preflight.SizeBytes)} now)."));
        }

        string? stale = session.StaleReason(found.Scan);
        checks.Add(new("Scan is fresh", stale is null,
            stale ?? $"Scanned less than {session.Env.Limits.MaxScanAge.TotalMinutes:0} minutes ago."));
        return checks;
    }

    private static SafetyCheck Verdict(string name, SafetyVerdict verdict, string passed) =>
        new(name, verdict.IsAllowed, verdict.IsAllowed ? passed : verdict.Reason);
}
