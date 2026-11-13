using DiskSweep.Core.IO;
using DiskSweep.Core.Logging;
using Microsoft.Win32;

namespace DiskSweep.Core.SystemInfo;

public sealed record StorePackage(string FullName, string FamilyName, string Name, string? DisplayName, string? InstallLocation);

/// <summary>Store / MSIX packages, read from the registry's package repository (read-only).</summary>
public sealed class StorePackageList
{
    private readonly Dictionary<string, HashSet<string>> _familiesBySid;

    public IReadOnlyList<StorePackage> Packages { get; }
    public HashSet<string> AllFamilyNames { get; }

    public StorePackageList(IReadOnlyList<StorePackage> packages, Dictionary<string, HashSet<string>> familiesBySid)
    {
        Packages = packages;
        _familiesBySid = familiesBySid;
        AllFamilyNames = new HashSet<string>(packages.Select(p => p.FamilyName), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The package families installed for one user, or null when that user's list couldn't be read.</summary>
    public IReadOnlySet<string>? FamiliesFor(string sid) => _familiesBySid.TryGetValue(sid, out var set) ? set : null;
}

public static class StorePackageReader
{
    private const string RepositoryPath = @"Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";
    private const string AllUserStorePath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore";

    public static StorePackageList Read(ProfileSet profiles, IActionLog log)
    {
        var packages = new Dictionary<string, StorePackage>(StringComparer.OrdinalIgnoreCase);
        var bySid = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var user in profiles.Users)
        {
            RegistryKey? repo = user.IsCurrentUser
                ? RegistryReader.Open(Registry.CurrentUser, $@"Software\Classes\{RepositoryPath}")
                : RegistryReader.Open(Registry.Users, $@"{user.Sid}_Classes\{RepositoryPath}");

            HashSet<string>? families = null;
            if (repo is not null)
            {
                using (repo)
                {
                    families = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string fullName in RegistryReader.SubKeyNames(repo))
                    {
                        if (Parse(fullName) is not { } pkg) continue;
                        using var k = RegistryReader.Open(repo, fullName);
                        string? display = k is null ? null : RegistryReader.String(k, "DisplayName");
                        if (display is not null && display.StartsWith('@')) display = null; // unresolved resource string
                        string? location = k is null ? null : PathUtil.NormalizeLoose(RegistryReader.String(k, "PackageRootFolder"));
                        packages.TryAdd(fullName, pkg with { DisplayName = display, InstallLocation = location });
                        families.Add(pkg.FamilyName);
                    }
                }
            }

            // Admin-only fallback that also covers users whose registry isn't loaded.
            using var store = RegistryReader.Open(Registry.LocalMachine, $@"{AllUserStorePath}\{user.Sid}");
            if (store is not null)
            {
                families ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string fullName in RegistryReader.SubKeyNames(store))
                    if (Parse(fullName) is { } pkg)
                    {
                        packages.TryAdd(fullName, pkg);
                        families.Add(pkg.FamilyName);
                    }
            }

            if (families is not null) bySid[user.Sid] = families;
            else log.Warn($"Store apps for user {user.DisplayName} couldn't be read; their app data folders won't be judged.");
        }

        log.Info($"Read {packages.Count} Store/MSIX packages.");
        return new StorePackageList(packages.Values.ToList(), bySid);
    }

    /// <summary>"Name_Version_Arch_ResourceId_PublisherId" → family "Name_PublisherId".</summary>
    internal static StorePackage? Parse(string fullName)
    {
        string[] parts = fullName.Split('_');
        if (parts.Length < 5) return null;
        string name = parts[0];
        string publisherId = parts[^1];
        if (name.Length == 0 || publisherId.Length == 0) return null;
        return new StorePackage(fullName, $"{name}_{publisherId}", name, null, null);
    }
}
