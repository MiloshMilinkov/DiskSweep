using DiskSweep.Core.IO;
using DiskSweep.Core.Matching;
using DiskSweep.Core.Models;
using DiskSweep.Core.SystemInfo;

namespace DiskSweep.Core.Scanning;

/// <summary>
/// Installed programs with no sign of use for a long time. Removal (phase 2) will only ever launch the
/// program's own registered uninstaller; this scanner never touches program files.
/// </summary>
public sealed class UnusedProgramScanner : IScanner
{
    public ScanCategory Category => ScanCategory.UnusedPrograms;
    public string PhaseName => "Checking when programs were last used";
    public double Weight => 10;

    /// <summary>Shared components other programs depend on. They're never "used" directly, so they'd always look unused.</summary>
    private static readonly string[] SharedComponentMarkers =
    [
        "Redistributable", "Runtime", "Visual C++", ".NET", "Framework", "SDK", "Driver", "Chipset", "Update for",
        "Hotfix", "Security Update", "Service Pack", "Language Pack", "WebView2", "DirectX", "PhysX", "Vulkan", "OpenAL",
        "Windows Software Development Kit", "Targeting Pack", "Host FX", "Hosting Bundle", "MUI",
    ];

    private static readonly string[] IgnoredExeNames =
    [
        "unins000", "uninst", "uninstall", "uninstaller", "setup", "install", "installer", "update", "updater",
        "crashpad_handler", "crashreporter", "elevate", "helper",
    ];

    public void Scan(ScanContext ctx)
    {
        var snapshot = ctx.Snapshot;
        var usage = snapshot.Usage ?? new UsageData();
        var processes = snapshot.Processes;
        var references = snapshot.References;
        var cutoff = ctx.NowUtc.AddDays(-ctx.Options.UnusedProgramDays);

        var programs = snapshot.Programs
            .Where(p => !p.IsSystemComponent && !p.IsUpdate && p.HasUninstaller && !IsSharedComponent(p))
            .ToList();
        var installFolders = ListInstallFolders(ctx);

        for (int i = 0; i < programs.Count; i++)
        {
            ctx.Token.ThrowIfCancellationRequested();
            var p = programs[i];
            ctx.Status.SetPhaseProgress((double)i / Math.Max(1, programs.Count));
            ctx.Status.CurrentPath = p.DisplayName;
            ctx.Status.AddScanned();

            var dirs = snapshot.ProgramFolders.TryGetValue(p, out var own) ? own.ToList() : [];
            bool hasOwnInstallFolder = p.InstallLocation is not null && dirs.Contains(p.InstallLocation, PathUtil.Comparer);
            string? guessedFolder = null;
            if (!hasOwnInstallFolder && GuessInstallFolder(p, installFolders) is { } guess)
            {
                // Many MSI installers don't record their folder; find it by name so usage can still be checked.
                guessedFolder = guess;
                dirs.Add(guess);
            }

            // Only programs that live on the chosen drive (or, for the system drive, programs with no known folder).
            bool onDrive = dirs.Count > 0 ? dirs.Any(ctx.IsOnDrive) : ctx.IsSystemDrive;
            if (!onDrive) continue;

            if (processes is not null && dirs.Select(processes.FindProcessIn).FirstOrDefault(r => r is not null) is { } running)
            {
                ctx.Log.Info($"Skipped \"{p.DisplayName}\": running now ({running}).");
                continue;
            }

            var exes = FindExecutables(dirs, ctx.Token);
            var sources = new List<(string Source, DateTime When)>();

            if (usage.Prefetch is { } prefetch)
            {
                var hits = exes.Select(e => prefetch.TryGetValue(e.Name, out var t) ? t : (DateTime?)null).OfType<DateTime>().ToList();
                if (hits.Count > 0) sources.Add(("Prefetch (Windows' record of program starts)", hits.Max()));
            }

            var ua = usage.UserAssist
                .Where(r => (!r.IsAppId && dirs.Any(d => PathUtil.IsStrictlyUnder(r.Path, d))) ||
                            ((r.IsAppId || r.Path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) &&
                             NameMatcher.Score(r.MatchName, p.DisplayName) >= 0.9))
                .Select(r => r.LastRunUtc)
                .ToList();
            if (ua.Count > 0) sources.Add(("UserAssist (started from Start menu, taskbar or desktop)", ua.Max()));

            if (usage.LastAccessTimesEnabled)
            {
                var access = exes.Where(e => e.LastAccessUtc > DateTime.MinValue).Select(e => e.LastAccessUtc).ToList();
                if (access.Count > 0) sources.Add(("Program file last-access time (less reliable)", access.Max()));
            }

            DateTime? lastUsed = sources.Count > 0 ? sources.Max(s => s.When) : null;
            string? lastUsedSource = sources.Count > 0 ? sources.MaxBy(s => s.When).Source : null;

            var evidence = new List<string>();
            if (guessedFolder is not null) evidence.Add($"Install folder not registered; matched by name: {guessedFolder}");
            foreach (var s in sources) evidence.Add($"{s.Source}: {s.When.ToLocalTime():d}");
            if (usage.Prefetch is null) evidence.Add($"Prefetch not checked: {usage.PrefetchNote ?? "unavailable"}");
            if (!usage.LastAccessTimesEnabled) evidence.Add("Program file access times not used: Windows isn't recording them on this PC.");
            if (p.InstallDateUtc is { } inst) evidence.Add($"Installed {inst.ToLocalTime():d} ({p.InstallDateSource}).");

            string reason;
            if (lastUsed is { } used)
            {
                if (used >= cutoff) continue;
                reason = $"Not used for {ctx.AgeDays(used)} days";
            }
            else
            {
                if (p.InstallDateUtc is not { } installed || installed >= cutoff) continue;
                reason = "No recorded use since it was installed";
                evidence.Add("No usage record found in any source. Programs that only run in the background leave no record.");
            }

            RiskLevel risk = RiskLevel.Review;
            if (references is not null && dirs.Select(references.FindReferenceInto).FirstOrDefault(r => r is not null) is { } reference)
            {
                risk = RiskLevel.Risky;
                evidence.Add($"Runs in the background or at startup: {reference.Source}. It may be in use without you opening it.");
            }

            ctx.Add(new ScanItem
            {
                Category = ScanCategory.UnusedPrograms,
                Kind = ItemKind.Program,
                Path = dirs.FirstOrDefault() ?? p.KeyPath,
                DisplayName = p.DisplayVersion is null ? p.DisplayName : $"{p.DisplayName} {p.DisplayVersion}",
                Publisher = p.Publisher,
                LastUsedUtc = lastUsed,
                LastUsedSource = lastUsedSource ?? "No record",
                InstalledUtc = p.InstallDateUtc,
                Reason = reason,
                Risk = risk,
                Evidence = evidence,
                UninstallCommand = p.UninstallString,
                QuietUninstallCommand = p.QuietUninstallString,
            });
        }
    }

    /// <summary>Program folders one or two levels down (Program Files\App, Program Files\Vendor\App, AppData\Local\Programs\App).</summary>
    private static List<string> ListInstallFolders(ScanContext ctx)
    {
        var roots = ctx.Snapshot.Layout.ProgramFilesDirectories
            .Concat(ctx.Snapshot.Profiles.Users.Select(u => u.LocalPrograms))
            .Distinct(PathUtil.Comparer);
        var result = new List<string>();
        var level1 = new List<FsEntry>();
        var level2 = new List<FsEntry>();
        foreach (string root in roots)
        {
            if (!FastDirectory.TryList(root, level1, out _)) continue;
            foreach (var e in level1.Where(e => e.IsDirectory && !e.IsReparsePoint))
            {
                result.Add(e.FullPath);
                if (FastDirectory.TryList(e.FullPath, level2, out _))
                    result.AddRange(level2.Where(c => c.IsDirectory && !c.IsReparsePoint).Select(c => c.FullPath));
            }
        }
        return result;
    }

    internal static string? GuessInstallFolder(InstalledProgram p, List<string> folders)
    {
        string? best = null;
        double bestScore = 0.85;
        foreach (string folder in folders)
        {
            double s = NameMatcher.Score(Path.GetFileName(folder), p.DisplayName);
            if (s > bestScore || (s >= bestScore && best is not null && folder.Length < best.Length))
            {
                bestScore = s;
                best = folder;
            }
        }
        return best;
    }

    internal static bool IsSharedComponent(InstalledProgram p) =>
        SharedComponentMarkers.Any(m => p.DisplayName.Contains(m, StringComparison.OrdinalIgnoreCase));

    private static List<FsEntry> FindExecutables(List<string> dirs, CancellationToken token)
    {
        var result = new List<FsEntry>();
        var buffer = new List<FsEntry>();
        foreach (string root in dirs.Distinct(PathUtil.Comparer))
        {
            var queue = new Queue<(string Dir, int Depth)>();
            queue.Enqueue((root, 0));
            while (queue.Count > 0 && result.Count < 300)
            {
                token.ThrowIfCancellationRequested();
                var (dir, depth) = queue.Dequeue();
                if (!FastDirectory.TryList(dir, buffer, out _)) continue;
                foreach (var e in buffer)
                {
                    if (e.IsReparsePoint) continue;
                    if (e.IsDirectory)
                    {
                        if (depth < 2) queue.Enqueue((e.FullPath, depth + 1));
                    }
                    else if (e.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                             !IgnoredExeNames.Any(n => Path.GetFileNameWithoutExtension(e.Name).StartsWith(n, StringComparison.OrdinalIgnoreCase)))
                    {
                        result.Add(e);
                    }
                }
            }
        }
        return result;
    }
}
