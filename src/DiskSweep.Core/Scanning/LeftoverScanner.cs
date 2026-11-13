using DiskSweep.Core.IO;
using DiskSweep.Core.Matching;
using DiskSweep.Core.Models;
using DiskSweep.Core.Safety;
using DiskSweep.Core.SystemInfo;
using Microsoft.Win32;

namespace DiskSweep.Core.Scanning;

/// <summary>
/// Folders in Program Files, ProgramData and AppData that no installed program claims and nothing on the
/// PC still points into. Never marked Safe. Also reports (never edits) registry keys left behind.
/// </summary>
public sealed class LeftoverScanner : IScanner
{
    public ScanCategory Category => ScanCategory.Leftovers;
    public string PhaseName => "Looking for leftovers of uninstalled programs";
    public double Weight => 15;

    /// <summary>Folders changed more recently than this are in use, so they're never reported as leftovers.</summary>
    public const int RecentlyUsedDays = 30;

    private enum LocationKind { ProgramFiles, ProgramData, Roaming, Local, LocalLow, LocalPrograms, Packages }

    private sealed record Candidate(FsEntry Entry, LocationKind Kind, UserProfile? Owner);

    public void Scan(ScanContext ctx)
    {
        var snapshot = ctx.Snapshot;
        var references = snapshot.References ?? ReferenceIndex.FromEntries([], "Service, task and startup references weren't read.");
        var processes = snapshot.Processes ?? new ProcessSnapshot([], ["(not captured)"]);
        var matcher = new NameMatcher(snapshot.Programs, snapshot.StorePackages.Packages);

        var candidates = CollectCandidates(ctx);
        var leftoverNames = new List<(string Name, UserProfile? Owner)>();

        for (int i = 0; i < candidates.Count; i++)
        {
            ctx.Token.ThrowIfCancellationRequested();
            var c = candidates[i];
            ctx.Status.CurrentPath = c.Entry.FullPath;
            ctx.Status.SetPhaseProgress((double)i / Math.Max(1, candidates.Count));
            ctx.Status.AddScanned();

            if (Evaluate(ctx, c, matcher, references, processes) && c.Kind != LocationKind.Packages)
                leftoverNames.Add((c.Entry.Name, c.Owner));
        }

        ReportOrphanedRegistry(ctx, leftoverNames, matcher);
    }

    private static List<Candidate> CollectCandidates(ScanContext ctx)
    {
        var layout = ctx.Snapshot.Layout;
        var locations = new List<(string Path, LocationKind Kind, UserProfile? Owner)>();

        foreach (string pf in layout.ProgramFilesDirectories) locations.Add((pf, LocationKind.ProgramFiles, null));
        foreach (string name in SafetyGuard.ProgramFilesNames)
            locations.Add((Path.Combine(ctx.DriveRoot, name), LocationKind.ProgramFiles, null)); // e.g. D:\Program Files
        locations.Add((layout.ProgramDataDirectory, LocationKind.ProgramData, null));

        foreach (var u in ctx.Snapshot.Profiles.Users)
        {
            locations.Add((u.RoamingAppData, LocationKind.Roaming, u));
            locations.Add((u.LocalAppData, LocationKind.Local, u));
            locations.Add((u.LocalLowAppData, LocationKind.LocalLow, u));
            locations.Add((u.LocalPrograms, LocationKind.LocalPrograms, u));
            locations.Add((u.LocalPackages, LocationKind.Packages, u));
        }

        var seen = new HashSet<string>(PathUtil.Comparer);
        var result = new List<Candidate>();
        var buffer = new List<FsEntry>();
        foreach (var (path, kind, owner) in locations)
        {
            if (!ctx.IsOnDrive(path) || !seen.Add(path)) continue;
            if (FastDirectory.Exists(path) == false) continue;
            if (!FastDirectory.TryList(path, buffer, out int error))
            {
                bool otherUser = owner is { IsCurrentUser: false };
                ctx.Skips.Add(path, FastDirectory.DescribeError(error) +
                    (otherUser && error == Native.NativeMethods.ERROR_ACCESS_DENIED ? " (run as administrator to check other users' folders)." : "."));
                continue;
            }
            foreach (var e in buffer)
            {
                if (!e.IsDirectory || ctx.Guard.IsInOsOrVendorFolder(e.FullPath)) continue;
                if (ctx.ShouldSkipDirectory(e)) continue;
                result.Add(new Candidate(e, kind, owner));
            }
        }
        return result;
    }

    /// <summary>Returns true when the folder was reported as a leftover.</summary>
    private static bool Evaluate(ScanContext ctx, Candidate c, NameMatcher matcher, ReferenceIndex references, ProcessSnapshot processes)
    {
        string path = c.Entry.FullPath;

        // Installed-program folders (and folders containing them) are protected; they're not leftovers.
        if (!ctx.Guard.CheckRules(path).IsAllowed) return false;

        var evidence = new List<string>();
        var gaps = new List<string>();
        NameMatch best;

        if (c.Kind == LocationKind.Packages)
        {
            var families = c.Owner is null ? null : ctx.Snapshot.StorePackages.FamiliesFor(c.Owner.Sid);
            if (families is null)
            {
                ctx.Skips.Add(path, "Store app list for this user couldn't be read, so this app data folder isn't judged.");
                return false;
            }
            if (!c.Entry.Name.Contains('_') || families.Contains(c.Entry.Name) || ctx.Snapshot.StorePackages.AllFamilyNames.Contains(c.Entry.Name))
                return false;
            best = new NameMatch(0, null);
            evidence.Add($"No installed Store app has the package family name \"{c.Entry.Name}\".");
        }
        else
        {
            best = matcher.BestMatch(c.Entry.Name);
            if (best.Score >= NameMatcher.MatchThreshold) return false;
            evidence.Add(best.Score > 0.4
                ? $"No installed program matches by name, publisher or install folder (closest: {best.Owner}, {best.Score:P0} similar)."
                : "No installed program matches by name, publisher or install folder.");
        }

        if (references.FindReferenceInto(path) is { } reference)
        {
            ctx.Log.Info($"Not a leftover, still referenced: {path} ← {reference.Source}");
            return false;
        }
        evidence.Add("No service, driver, scheduled task or startup entry points inside it.");

        if (processes.FindProcessIn(path) is { } running)
        {
            ctx.Log.Info($"Not a leftover, a program is running from it: {running}");
            return false;
        }
        evidence.Add("No running program was started from it.");

        gaps.AddRange(references.IncompleteSources);
        if (!processes.IsComplete)
            gaps.Add($"{processes.UninspectableNames.Count} running process(es) couldn't be inspected.");
        if (c.Owner is { HiveReadable: false })
            gaps.Add($"The registry of {c.Owner.DisplayName} isn't loaded, so that user's programs and startup entries couldn't be checked.");
        if (c.Owner is null)
        {
            var unread = ctx.Snapshot.Profiles.Users.Where(u => !u.HiveReadable).Select(u => u.DisplayName).ToList();
            if (unread.Count > 0)
                gaps.Add($"The registry of {string.Join(", ", unread)} isn't loaded, so their startup entries couldn't be checked.");
        }

        var stats = FolderMeasure.Measure(path, ctx.Token);
        DateTime newest = stats.NewestChangeUtc > c.Entry.NewestChangeUtc ? stats.NewestChangeUtc : c.Entry.NewestChangeUtc;
        int ageDays = ctx.AgeDays(newest);

        // A program that's gone doesn't write to its folder. Recent changes mean something still uses it.
        if (ageDays < RecentlyUsedDays)
        {
            ctx.Log.Info($"Not a leftover, changed {ageDays} days ago so something still uses it: {path}");
            return false;
        }

        int confidence = 95;
        if (best.Score > 0.4) confidence -= (int)((best.Score - 0.4) * 100);
        if (ageDays < 180) confidence -= 15;
        if (stats.ExecutableCount > 0) confidence -= 10;
        if (gaps.Count > 0) confidence -= 10;
        if (!stats.IsFullyReadable) confidence -= 10;
        confidence = Math.Clamp(confidence, 5, 95);

        evidence.Add($"Last change inside: {newest.ToLocalTime():d} ({ageDays} days ago).");
        evidence.Add($"{stats.FileCount:N0} files in {stats.FolderCount:N0} folders, {stats.ExecutableCount:N0} executable(s).");
        if (stats.FirstUnreadable is not null) evidence.Add($"Not everything inside could be read: {stats.FirstUnreadable}");
        if (stats.Truncated) evidence.Add("Very large folder: measuring stopped early, so size is a minimum.");
        if (stats.FirstCloudItem is not null) evidence.Add($"Contains cloud files: {stats.FirstCloudItem}");
        foreach (string gap in gaps) evidence.Add($"Not fully checked: {gap}");

        // Leftovers are never Safe. Review needs high confidence and a complete check; everything else is Risky.
        RiskLevel risk = confidence >= 70 && gaps.Count == 0 && stats.IsFullyReadable && stats.FirstCloudItem is null
            ? RiskLevel.Review
            : RiskLevel.Risky;

        ctx.Add(new ScanItem
        {
            Category = ScanCategory.Leftovers,
            Kind = ItemKind.Folder,
            Path = path,
            DisplayName = c.Entry.Name,
            SizeBytes = stats.TotalBytes,
            LastModifiedUtc = newest,
            Reason = c.Kind == LocationKind.Packages ? "Data of an uninstalled Store app" : $"Likely leftover ({Describe(c.Kind)})",
            Risk = risk,
            Confidence = confidence,
            Evidence = evidence,
            ContainsReparsePoint = stats.FirstReparsePoint,
        });
        return true;
    }

    private static string Describe(LocationKind kind) => kind switch
    {
        LocationKind.ProgramFiles => "Program Files",
        LocationKind.ProgramData => "ProgramData",
        LocationKind.Roaming => "AppData\\Roaming",
        LocationKind.Local => "AppData\\Local",
        LocationKind.LocalLow => "AppData\\LocalLow",
        LocationKind.LocalPrograms => "per-user programs",
        _ => kind.ToString(),
    };

    /// <summary>Report only. Keys are opened read-only and never changed.</summary>
    private static void ReportOrphanedRegistry(ScanContext ctx, List<(string Name, UserProfile? Owner)> leftovers, NameMatcher matcher)
    {
        const string reportOnly = "Report only. DiskSweep never edits the registry.";

        // 1. Uninstall entries whose files are all gone from this drive.
        foreach (var p in ctx.Snapshot.Programs)
        {
            var concrete = new List<string>();
            if (p.InstallLocation is not null) concrete.Add(p.InstallLocation);
            concrete.AddRange(p.RegisteredFiles.Where(f => !PathUtil.IsSameOrUnder(f, ctx.Snapshot.Layout.WindowsDirectory)));
            if (concrete.Count == 0 || !concrete.All(ctx.IsOnDrive)) continue;
            if (!concrete.All(f => FastDirectory.Exists(f) == false)) continue;

            ctx.Add(new ScanItem
            {
                Category = ScanCategory.OrphanedRegistry,
                Kind = ItemKind.RegistryKey,
                Path = p.KeyPath,
                DisplayName = p.DisplayName,
                Reason = "Uninstall entry whose program files are gone",
                Risk = RiskLevel.Review,
                IsReportOnly = true,
                ReportOnlyReason = reportOnly,
                Publisher = p.Publisher,
                Evidence = concrete.Select(f => $"Missing: {f}").Prepend($"Listed in Apps & features as \"{p.DisplayName}\".").ToList(),
            });
        }

        // 2. Settings keys named exactly like a leftover folder.
        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        foreach (var (name, owner) in leftovers)
        {
            var roots = new List<(RegistryKey Root, string Path, string Display)>
            {
                (hklm, $@"SOFTWARE\{name}", $@"HKLM\SOFTWARE\{name}"),
                (hklm, $@"SOFTWARE\WOW6432Node\{name}", $@"HKLM\SOFTWARE\WOW6432Node\{name}"),
            };
            if (owner is null || owner.IsCurrentUser)
                roots.Add((Registry.CurrentUser, $@"Software\{name}", $@"HKCU\Software\{name}"));
            else if (owner.HiveReadable)
                roots.Add((Registry.Users, $@"{owner.Sid}\Software\{name}", $@"HKU\{owner.Sid}\Software\{name}"));

            foreach (var (root, path, display) in roots)
            {
                using var key = RegistryReader.Open(root, path);
                if (key is null || !reported.Add(display)) continue;
                ctx.Add(new ScanItem
                {
                    Category = ScanCategory.OrphanedRegistry,
                    Kind = ItemKind.RegistryKey,
                    Path = display,
                    DisplayName = name,
                    LastModifiedUtc = RegistryReader.LastWriteTime(key),
                    Reason = $"Settings key named like the leftover folder \"{name}\"",
                    Risk = RiskLevel.Risky,
                    IsReportOnly = true,
                    ReportOnlyReason = reportOnly,
                    Evidence = [$"No installed program matches \"{name}\".", "Shown for information; remove it yourself only if you're sure."],
                });
            }
        }
    }
}
