using System.Security.Principal;
using DiskSweep.Core.IO;
using DiskSweep.Core.Logging;
using Microsoft.Win32;

namespace DiskSweep.Core.SystemInfo;

public sealed record UserProfile(string Sid, string ProfilePath, bool IsCurrentUser, bool HiveReadable)
{
    public string RoamingAppData { get; init; } = Path.Combine(ProfilePath, "AppData", "Roaming");
    public string LocalAppData { get; init; } = Path.Combine(ProfilePath, "AppData", "Local");
    public string LocalLowAppData { get; init; } = Path.Combine(ProfilePath, "AppData", "LocalLow");
    public string TempDirectory { get; init; } = Path.Combine(ProfilePath, "AppData", "Local", "Temp");
    public string AppDataDirectory => Path.Combine(ProfilePath, "AppData");
    public string LocalPrograms => Path.Combine(LocalAppData, "Programs");
    public string LocalPackages => Path.Combine(LocalAppData, "Packages");

    /// <summary>Downloads, Desktop, Documents, Videos, Music, Pictures (redirected locations for the current user).</summary>
    public IReadOnlyList<string> PersonalFolders { get; init; } =
        new[] { "Downloads", "Desktop", "Documents", "Videos", "Music", "Pictures" }.Select(n => Path.Combine(ProfilePath, n)).ToArray();

    public string DownloadsFolder { get; init; } = Path.Combine(ProfilePath, "Downloads");

    public string DisplayName => Path.GetFileName(ProfilePath);
}

public sealed record ProfileSet(IReadOnlyList<UserProfile> Users, IReadOnlyList<string> SpecialProfiles)
{
    /// <summary>The template Windows copies when a new account signs in for the first time.</summary>
    public string? DefaultProfile { get; init; }

    public UserProfile? Current => Users.FirstOrDefault(u => u.IsCurrentUser);
    public IEnumerable<string> AllProfileRoots => Users.Select(u => u.ProfilePath).Concat(SpecialProfiles);
}

public static class UserProfileReader
{
    private const string ProfileListKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";

    public static ProfileSet Read(IActionLog log)
    {
        string currentSid = WindowsIdentity.GetCurrent().User?.Value ?? "";
        var users = new List<UserProfile>();
        var special = new List<string>();
        string? defaultProfile = null;

        using var list = RegistryReader.Open(Registry.LocalMachine, ProfileListKey);
        if (list is not null)
        {
            foreach (string sid in RegistryReader.SubKeyNames(list))
            {
                if (!(sid.StartsWith("S-1-5-21-", StringComparison.Ordinal) || sid.StartsWith("S-1-12-1-", StringComparison.Ordinal))) continue;
                if (sid.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)) continue;
                using var k = RegistryReader.Open(list, sid);
                string? path = k is null ? null : PathUtil.NormalizeLoose(RegistryReader.String(k, "ProfileImagePath"));
                if (path is null) continue;

                bool isCurrent = sid == currentSid;
                bool hive = isCurrent || CanReadHive(sid);
                users.Add(isCurrent ? ForCurrentUser(sid, path) : new UserProfile(sid, path, false, hive));
            }

            foreach (string name in new[] { "Default", "Public" })
                if (PathUtil.NormalizeLoose(RegistryReader.String(list, name)) is { } p) special.Add(p);
            defaultProfile = PathUtil.NormalizeLoose(RegistryReader.String(list, "Default"));
        }
        else
        {
            log.Warn("The list of user profiles couldn't be read.");
        }

        if (users.All(u => !u.IsCurrentUser) &&
            PathUtil.NormalizeLoose(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) is { } me)
            users.Add(ForCurrentUser(currentSid, me));

        return new ProfileSet(users, special) { DefaultProfile = defaultProfile };
    }

    private static bool CanReadHive(string sid)
    {
        using var k = RegistryReader.Open(Registry.Users, sid);
        return k is not null;
    }

    private static UserProfile ForCurrentUser(string sid, string path)
    {
        var p = new UserProfile(sid, path, true, true);
        string? Get(Environment.SpecialFolder f) => PathUtil.NormalizeLoose(Environment.GetFolderPath(f));

        string roaming = Get(Environment.SpecialFolder.ApplicationData) ?? p.RoamingAppData;
        string local = Get(Environment.SpecialFolder.LocalApplicationData) ?? p.LocalAppData;
        string localLow = KnownFolders.TryGetPath(KnownFolders.LocalAppDataLow) ?? p.LocalLowAppData;
        string temp = PathUtil.NormalizeLoose(Path.GetTempPath()) ?? p.TempDirectory;
        string downloads = KnownFolders.TryGetPath(KnownFolders.Downloads) ?? p.DownloadsFolder;

        var personal = new[]
        {
            downloads,
            Get(Environment.SpecialFolder.DesktopDirectory),
            Get(Environment.SpecialFolder.MyDocuments),
            Get(Environment.SpecialFolder.MyVideos),
            Get(Environment.SpecialFolder.MyMusic),
            Get(Environment.SpecialFolder.MyPictures),
        }.OfType<string>().Distinct(PathUtil.Comparer).ToArray();

        // Expand 8.3 short names (TEMP is often C:\Users\LONGNA~1\...).
        temp = Safety.PathNormalizer.TryNormalize(temp, out var t, out _) ? t : temp;

        return p with
        {
            RoamingAppData = roaming,
            LocalAppData = local,
            LocalLowAppData = localLow,
            TempDirectory = temp,
            DownloadsFolder = downloads,
            PersonalFolders = personal,
        };
    }
}
