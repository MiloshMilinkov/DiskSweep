using System.Security.Principal;
using DiskSweep.Core.IO;
using DiskSweep.Core.Logging;
using DiskSweep.Core.Models;
using DiskSweep.Core.Safety;

namespace DiskSweep.Core.SystemInfo;

/// <summary>Everything read about the PC before scanning starts. Read-only.</summary>
public sealed class SystemSnapshot
{
    public required ProfileSet Profiles { get; init; }
    public required IReadOnlyList<InstalledProgram> Programs { get; init; }
    public required StorePackageList StorePackages { get; init; }
    public required IReadOnlyList<string> CloudRoots { get; init; }
    public required ProtectionLayout Layout { get; init; }
    public ReferenceIndex? References { get; init; }
    public ProcessSnapshot? Processes { get; init; }
    public UsageData? Usage { get; init; }

    /// <summary>
    /// Each program's own folders: its registered folders minus anything too broad (a drive root, Program Files,
    /// a profile or personal folder…) or inside Windows. These are exactly the folders protected as "belongs to".
    /// </summary>
    public IReadOnlyDictionary<InstalledProgram, IReadOnlyList<string>> ProgramFolders { get; init; } =
        new Dictionary<InstalledProgram, IReadOnlyList<string>>();

    public static bool IsRunningAsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static SystemSnapshot Capture(IReadOnlySet<ScanCategory> categories, ScanOptions options, bool isAdmin, IActionLog log, CancellationToken token)
    {
        string windows = PathUtil.NormalizeLoose(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\Windows";

        var profiles = UserProfileReader.Read(log);
        token.ThrowIfCancellationRequested();
        var programs = InstalledProgramReader.Read(profiles, log);
        token.ThrowIfCancellationRequested();
        var packages = StorePackageReader.Read(profiles, log);
        token.ThrowIfCancellationRequested();
        var cloud = CloudSyncRoots.Detect(profiles, log);
        token.ThrowIfCancellationRequested();

        bool needReferences = categories.Contains(ScanCategory.Leftovers) || categories.Contains(ScanCategory.UnusedPrograms);
        var references = needReferences ? ReferenceIndex.Build(profiles, isAdmin, log, token) : null;
        var processes = needReferences ? ProcessSnapshot.Capture(log) : null;
        var usage = categories.Contains(ScanCategory.UnusedPrograms) ? UsageData.Capture(profiles, windows, log) : null;

        var (layout, programFolders) = BuildLayout(windows, profiles, programs, packages, cloud, options, log);

        return new SystemSnapshot
        {
            Profiles = profiles,
            Programs = programs,
            StorePackages = packages,
            CloudRoots = cloud,
            Layout = layout,
            ProgramFolders = programFolders,
            References = references,
            Processes = processes,
            Usage = usage,
        };
    }

    internal static (ProtectionLayout, Dictionary<InstalledProgram, IReadOnlyList<string>>) BuildLayout(
        string windows, ProfileSet profiles, IReadOnlyList<InstalledProgram> programs,
        StorePackageList packages, IReadOnlyList<string> cloud, ScanOptions options, IActionLog log)
    {
        string systemDrive = PathUtil.DriveRoot(windows);
        var programFiles = new[]
            {
                Environment.GetEnvironmentVariable("ProgramW6432"),
                Environment.GetEnvironmentVariable("ProgramFiles"),
                Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                Environment.GetEnvironmentVariable("ProgramFiles(Arm)"),
            }
            .Select(PathUtil.NormalizeLoose).OfType<string>().Distinct(PathUtil.Comparer).ToList();
        string programData = PathUtil.NormalizeLoose(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData))
                             ?? Path.Combine(systemDrive, "ProgramData");

        var profileRoots = profiles.AllProfileRoots.Distinct(PathUtil.Comparer).ToList();
        var anchors = new List<string>();
        foreach (var u in profiles.Users)
            anchors.AddRange([u.AppDataDirectory, u.RoamingAppData, u.LocalAppData, u.LocalLowAppData, u.LocalPrograms, u.LocalPackages, u.TempDirectory]);
        var temps = profiles.Users.Select(u => u.TempDirectory).Append(Path.Combine(windows, "Temp")).Distinct(PathUtil.Comparer).ToList();

        // Folders too broad to protect as "belongs to program X" (badly written installers sometimes register them).
        var tooBroad = new HashSet<string>(PathUtil.Comparer) { programData, Path.Combine(systemDrive, "Users") };
        tooBroad.UnionWith(programFiles);
        tooBroad.UnionWith(profileRoots);
        tooBroad.UnionWith(anchors);
        foreach (var u in profiles.Users) tooBroad.UnionWith(u.PersonalFolders);

        var programFolders = new Dictionary<InstalledProgram, IReadOnlyList<string>>();
        var installedDirs = new List<ProtectedPath>();
        var protectedFiles = new List<ProtectedPath>();
        foreach (var program in programs)
        {
            var own = program.RelatedDirectories
                .Where(dir => dir.Length > 3 && !tooBroad.Contains(dir) && !PathUtil.IsSameOrUnder(dir, windows))
                .ToList();
            programFolders[program] = own;

            string reason = $"Belongs to the installed program \"{program.DisplayName}\".";
            installedDirs.AddRange(own.Select(dir => new ProtectedPath(dir, reason)));
            foreach (string file in program.RegisteredFiles)
                protectedFiles.Add(new ProtectedPath(file, $"Uninstaller or icon of the installed program \"{program.DisplayName}\"."));
        }
        foreach (var pkg in packages.Packages.Where(p => p.InstallLocation is not null))
            installedDirs.Add(new ProtectedPath(pkg.InstallLocation!, $"Installed Store app \"{pkg.DisplayName ?? pkg.Name}\"."));

        var appOwn = new List<string> { AppPaths.DataDirectory };
        // Protect the folder DiskSweep runs from, unless it's a shared folder like Downloads; then protect just its own exe.
        if (PathUtil.NormalizeLoose(AppContext.BaseDirectory) is { } baseDir)
        {
            if (!tooBroad.Contains(baseDir) && baseDir.Length > 3) appOwn.Add(baseDir);
            else if (PathUtil.NormalizeLoose(Environment.ProcessPath) is { } exe) protectedFiles.Add(new ProtectedPath(exe, "DiskSweep itself."));
        }

        log.Info($"Protected: {installedDirs.Count} installed-program folders, {protectedFiles.Count} uninstaller files, {cloud.Count} cloud folders.");

        var layout = new ProtectionLayout
        {
            SystemDrive = systemDrive,
            WindowsDirectory = windows,
            ProgramFilesDirectories = programFiles,
            ProgramDataDirectory = programData,
            UserProfileDirectories = profileRoots,
            AppDataDirectories = profiles.Users.Select(u => u.AppDataDirectory).ToList(),
            ScanAnchorDirectories = anchors.Distinct(PathUtil.Comparer).ToList(),
            TempDirectories = temps,
            InstalledProgramDirectories = installedDirs,
            OtherProtectedDirectories = profiles.DefaultProfile is { } defaultProfile
                ? [new ProtectedPath(defaultProfile, "The Default profile is the template Windows copies for every new user account.")]
                : [],
            ProtectedFiles = protectedFiles,
            CloudSyncRoots = cloud,
            AppOwnDirectories = appOwn,
            UserExclusions = options.ExcludedPaths,
        };
        return (layout, programFolders);
    }
}
