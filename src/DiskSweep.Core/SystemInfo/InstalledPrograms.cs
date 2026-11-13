using System.Globalization;
using DiskSweep.Core.IO;
using DiskSweep.Core.Logging;
using Microsoft.Win32;

namespace DiskSweep.Core.SystemInfo;

public sealed class InstalledProgram
{
    public required string KeyPath { get; init; }
    public required string DisplayName { get; init; }
    public string? Publisher { get; init; }
    public string? DisplayVersion { get; init; }
    public string? InstallLocation { get; init; }
    public DateTime? InstallDateUtc { get; init; }
    public string? InstallDateSource { get; init; }
    public string? UninstallString { get; init; }
    public string? QuietUninstallString { get; init; }
    public bool IsSystemComponent { get; init; }
    public bool IsUpdate { get; init; }

    /// <summary>Files named by the registry entry (uninstaller, DisplayIcon, ModifyPath).</summary>
    public IReadOnlyList<string> RegisteredFiles { get; init; } = [];

    /// <summary>InstallLocation plus the folders of <see cref="RegisteredFiles"/>.</summary>
    public IReadOnlyList<string> RelatedDirectories { get; init; } = [];

    public bool HasUninstaller => !string.IsNullOrWhiteSpace(UninstallString) || !string.IsNullOrWhiteSpace(QuietUninstallString);
}

public static class InstalledProgramReader
{
    /// <summary>The 64-bit and 32-bit (WOW6432Node) uninstall lists.</summary>
    private static readonly string[] UninstallPaths =
    [
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
    ];

    public static IReadOnlyList<InstalledProgram> Read(ProfileSet profiles, IActionLog log)
    {
        var programs = new List<InstalledProgram>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var hklm64 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        var roots = new List<(RegistryKey Root, string Prefix, string Label)>
        {
            (hklm64, "", "HKLM"),
            (Registry.CurrentUser, "", "HKCU"),
        };
        roots.AddRange(profiles.Users.Where(u => !u.IsCurrentUser && u.HiveReadable)
            .Select(u => (Registry.Users, u.Sid + "\\", $"HKU\\{u.Sid}")));

        foreach (var (root, prefix, label) in roots)
            foreach (string path in UninstallPaths)
                ReadRoot(root, prefix + path, $@"{label}\{path}", programs, seen, log);

        log.Info($"Read {programs.Count} installed program entries from the registry.");
        return programs;
    }

    private static void ReadRoot(RegistryKey root, string path, string displayPath,
        List<InstalledProgram> into, HashSet<string> seen, IActionLog log)
    {
        using var key = RegistryReader.Open(root, path);
        if (key is null) return;

        foreach (string name in RegistryReader.SubKeyNames(key))
        {
            string keyPath = $@"{displayPath}\{name}";
            if (!seen.Add(keyPath)) continue;

            using var k = RegistryReader.Open(key, name);
            if (k is null)
            {
                log.Warn($"Uninstall entry can't be read: {keyPath}");
                continue;
            }

            string? displayName = RegistryReader.String(k, "DisplayName");
            if (string.IsNullOrWhiteSpace(displayName)) continue;

            string? installLocation = PathUtil.NormalizeLoose(RegistryReader.String(k, "InstallLocation"));
            string? uninstall = RegistryReader.String(k, "UninstallString");
            string? quiet = RegistryReader.String(k, "QuietUninstallString");
            string? icon = RegistryReader.String(k, "DisplayIcon");
            string? modify = RegistryReader.String(k, "ModifyPath");

            var files = new[] { uninstall, quiet, icon, modify }
                .SelectMany(CommandLinePaths.Extract)
                .Distinct(PathUtil.Comparer)
                .ToList();

            var dirs = new List<string>();
            if (installLocation is not null) dirs.Add(installLocation);
            dirs.AddRange(files.Select(Path.GetDirectoryName).OfType<string>().Select(PathUtil.TrimEndSeparator));

            (DateTime? installed, string? source) = ParseInstallDate(RegistryReader.String(k, "InstallDate"));
            if (installed is null && RegistryReader.LastWriteTime(k) is { } written)
                (installed, source) = (written, "Registry entry last changed");

            string? releaseType = RegistryReader.String(k, "ReleaseType");
            into.Add(new InstalledProgram
            {
                KeyPath = keyPath,
                DisplayName = displayName,
                Publisher = RegistryReader.String(k, "Publisher"),
                DisplayVersion = RegistryReader.String(k, "DisplayVersion"),
                InstallLocation = installLocation,
                InstallDateUtc = installed,
                InstallDateSource = source,
                UninstallString = uninstall,
                QuietUninstallString = quiet,
                IsSystemComponent = RegistryReader.Int(k, "SystemComponent") == 1,
                IsUpdate = !string.IsNullOrEmpty(RegistryReader.String(k, "ParentKeyName")) ||
                           releaseType is not null && (releaseType.Contains("Update", StringComparison.OrdinalIgnoreCase) ||
                                                       releaseType.Contains("Hotfix", StringComparison.OrdinalIgnoreCase)),
                RegisteredFiles = files,
                RelatedDirectories = dirs.Distinct(PathUtil.Comparer).ToList(),
            });
        }
    }

    private static (DateTime?, string?) ParseInstallDate(string? value)
    {
        if (value is not null && value.Length >= 8 &&
            DateTime.TryParseExact(value[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d))
            return (d.ToUniversalTime(), "Install date recorded by the installer");
        return (null, null);
    }
}
