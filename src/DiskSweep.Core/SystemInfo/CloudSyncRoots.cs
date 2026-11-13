using System.Text.Json;
using DiskSweep.Core.IO;
using DiskSweep.Core.Logging;
using Microsoft.Win32;

namespace DiskSweep.Core.SystemInfo;

/// <summary>Finds OneDrive, Dropbox, Google Drive, iCloud and other sync folders so every scan can skip them.</summary>
public static class CloudSyncRoots
{
    private static readonly string[] DefaultFolderNames =
    [
        "OneDrive", "Dropbox", "Google Drive", "My Drive", "iCloudDrive", "iCloud Drive", "iCloud Photos", "Box", "Box Sync",
    ];

    private static readonly string[] DefaultFolderPrefixes = ["OneDrive - ", "Dropbox (", "Dropbox - "];

    public static IReadOnlyList<string> Detect(ProfileSet profiles, IActionLog log)
    {
        var roots = new List<string>();
        void Add(string? p)
        {
            if (PathUtil.NormalizeLoose(p) is { } n) roots.Add(n);
        }

        // Every sync engine that uses the Windows Cloud Files API registers its folders here.
        using (var manager = RegistryReader.Open(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager"))
        {
            if (manager is not null)
            {
                foreach (string id in RegistryReader.SubKeyNames(manager))
                {
                    using var userRoots = RegistryReader.Open(manager, $@"{id}\UserSyncRoots");
                    if (userRoots is null) continue;
                    foreach (string sid in RegistryReader.ValueNames(userRoots)) Add(RegistryReader.String(userRoots, sid));
                }
            }
        }

        Add(Environment.GetEnvironmentVariable("OneDrive"));
        Add(Environment.GetEnvironmentVariable("OneDriveConsumer"));
        Add(Environment.GetEnvironmentVariable("OneDriveCommercial"));

        ReadOneDriveAccounts(Registry.CurrentUser, @"Software\Microsoft\OneDrive\Accounts", Add);
        foreach (var u in profiles.Users.Where(u => !u.IsCurrentUser && u.HiveReadable))
            ReadOneDriveAccounts(Registry.Users, $@"{u.Sid}\Software\Microsoft\OneDrive\Accounts", Add);

        var buffer = new List<FsEntry>();
        foreach (var u in profiles.Users)
        {
            foreach (string info in new[] { Path.Combine(u.RoamingAppData, "Dropbox", "info.json"), Path.Combine(u.LocalAppData, "Dropbox", "info.json") })
                foreach (string p in ReadDropboxInfo(info)) Add(p);

            if (FastDirectory.TryList(u.ProfilePath, buffer, out _))
            {
                foreach (var e in buffer.Where(e => e.IsDirectory))
                {
                    if (DefaultFolderNames.Contains(e.Name, StringComparer.OrdinalIgnoreCase) ||
                        DefaultFolderPrefixes.Any(prefix => e.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                        Add(e.FullPath);
                }
            }
        }

        var result = roots.Distinct(PathUtil.Comparer).ToList();
        foreach (string r in result) log.Info($"Cloud-synced folder excluded from all scans: {r}");
        return result;
    }

    private static void ReadOneDriveAccounts(RegistryKey root, string path, Action<string?> add)
    {
        using var accounts = RegistryReader.Open(root, path);
        if (accounts is null) return;
        foreach (string name in RegistryReader.SubKeyNames(accounts))
        {
            using var a = RegistryReader.Open(accounts, name);
            if (a is not null) add(RegistryReader.String(a, "UserFolder"));
        }
    }

    private static IEnumerable<string> ReadDropboxInfo(string file)
    {
        var paths = new List<string>();
        try
        {
            if (!File.Exists(file)) return paths;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var account in doc.RootElement.EnumerateObject())
                if (account.Value.ValueKind == JsonValueKind.Object && account.Value.TryGetProperty("path", out var p) && p.GetString() is { } s)
                    paths.Add(s);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return paths;
    }
}
