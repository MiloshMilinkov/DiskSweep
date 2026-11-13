using DiskSweep.Core.IO;
using DiskSweep.Core.Logging;
using Microsoft.Win32;

namespace DiskSweep.Core.SystemInfo;

/// <summary>One UserAssist entry. <see cref="Path"/> is a file path, or an app ID such as "Chrome" when <see cref="IsAppId"/>.</summary>
public sealed record UsageRecord(string Path, DateTime LastRunUtc, int RunCount, bool IsAppId = false)
{
    /// <summary>"Microsoft.WindowsTerminal_8wekyb3d8bbwe!App" → "Microsoft.WindowsTerminal"; "notepad.lnk" → "notepad".</summary>
    public string MatchName
    {
        get
        {
            if (!IsAppId) return System.IO.Path.GetFileNameWithoutExtension(Path);
            int cut = Path.IndexOfAny(['_', '!']);
            return cut > 0 ? Path[..cut] : Path;
        }
    }
}

/// <summary>Where "last used" dates come from. Each is incomplete on its own, so the scanner combines them.</summary>
public sealed class UsageData
{
    /// <summary>Upper-case exe file name → newest Prefetch timestamp. Null when Prefetch couldn't be read (needs admin).</summary>
    public IReadOnlyDictionary<string, DateTime>? Prefetch { get; init; }
    public string? PrefetchNote { get; init; }
    public IReadOnlyList<UsageRecord> UserAssist { get; init; } = [];

    /// <summary>False when Windows is set not to update last-access times, which makes exe access times meaningless.</summary>
    public bool LastAccessTimesEnabled { get; init; }

    public static UsageData Capture(ProfileSet profiles, string windowsDirectory, IActionLog log)
    {
        var prefetch = PrefetchReader.Read(windowsDirectory, out string? note);
        var userAssist = new List<UsageRecord>();
        userAssist.AddRange(UserAssistReader.Read(Registry.CurrentUser));
        foreach (var u in profiles.Users.Where(u => !u.IsCurrentUser && u.HiveReadable))
        {
            using var hive = RegistryReader.Open(Registry.Users, u.Sid);
            if (hive is not null) userAssist.AddRange(UserAssistReader.Read(hive));
        }

        bool lastAccess = LastAccessEnabled();
        log.Info($"Usage sources: Prefetch {(prefetch is null ? "unavailable" : $"{prefetch.Count} programs")}, " +
                 $"UserAssist {userAssist.Count} entries, last-access times {(lastAccess ? "on" : "off")}.");
        return new UsageData { Prefetch = prefetch, PrefetchNote = note, UserAssist = userAssist, LastAccessTimesEnabled = lastAccess };
    }

    private static bool LastAccessEnabled()
    {
        using var k = RegistryReader.Open(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\FileSystem");
        int? v = k is null ? null : RegistryReader.Int(k, "NtfsDisableLastAccessUpdate");
        return v is null || (v.Value & 1) == 0;
    }
}

public static class PrefetchReader
{
    /// <summary>
    /// Windows writes C:\Windows\Prefetch\NAME.EXE-HASH.pf each time a program starts, so the file's own
    /// timestamp is a good "last run" date. The folder is readable by administrators only.
    /// </summary>
    public static IReadOnlyDictionary<string, DateTime>? Read(string windowsDirectory, out string? note)
    {
        string dir = Path.Combine(windowsDirectory, "Prefetch");
        var entries = new List<FsEntry>();
        if (!FastDirectory.TryList(dir, entries, out int error))
        {
            note = error == Native.NativeMethods.ERROR_ACCESS_DENIED
                ? "Prefetch needs administrator rights."
                : "Prefetch isn't available on this PC.";
            return null;
        }

        var result = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
        {
            if (e.IsDirectory || !e.Name.EndsWith(".pf", StringComparison.OrdinalIgnoreCase)) continue;
            int dash = e.Name.LastIndexOf('-');
            if (dash <= 0) continue;
            string exe = e.Name[..dash];
            if (!result.TryGetValue(exe, out var existing) || e.LastWriteUtc > existing) result[exe] = e.LastWriteUtc;
        }
        note = null;
        return result;
    }
}

public static class UserAssistReader
{
    private const string UserAssistPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist";

    /// <summary>Explorer's record of programs a user started from the Start menu, taskbar or desktop (names are ROT13-encoded).</summary>
    public static List<UsageRecord> Read(RegistryKey userRoot)
    {
        var records = new List<UsageRecord>();
        using var root = RegistryReader.Open(userRoot, UserAssistPath);
        if (root is null) return records;

        foreach (string guid in RegistryReader.SubKeyNames(root))
        {
            using var count = RegistryReader.Open(root, $@"{guid}\Count");
            if (count is null) continue;
            foreach (string encoded in RegistryReader.ValueNames(count))
            {
                if (RegistryReader.Bytes(count, encoded) is not { } data) continue;
                if (Decode(Rot13(encoded), data) is { } record) records.Add(record);
            }
        }
        return records;
    }

    internal static UsageRecord? Decode(string name, byte[] data)
    {
        if (data.Length < 68) return null; // Windows 7+ format is 72 bytes
        int runCount = BitConverter.ToInt32(data, 4);
        long fileTime = BitConverter.ToInt64(data, 60);
        if (Native.NativeMethods.FromFileTime(fileTime) is not { } lastRun) return null;
        string? path = ResolveKnownFolderPrefix(name);
        if (path is not null) return new UsageRecord(path, lastRun, runCount);
        // Windows 11 records many launches by app ID instead of path.
        bool looksLikeAppId = name.Length >= 3 && !name.Contains('\\') && !name.StartsWith("UEME_", StringComparison.Ordinal);
        return looksLikeAppId ? new UsageRecord(name, lastRun, runCount, IsAppId: true) : null;
    }

    public static string Rot13(string s)
    {
        var chars = s.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            char c = chars[i];
            if (c is >= 'a' and <= 'z') chars[i] = (char)('a' + (c - 'a' + 13) % 26);
            else if (c is >= 'A' and <= 'Z') chars[i] = (char)('A' + (c - 'A' + 13) % 26);
        }
        return new string(chars);
    }

    /// <summary>"{6D809377-...}\Vendor\app.exe" → "C:\Program Files\Vendor\app.exe".</summary>
    internal static string? ResolveKnownFolderPrefix(string name)
    {
        if (name.Length > 38 && name[0] == '{' && name[37] == '}' && Guid.TryParse(name.AsSpan(0, 38), out var id))
        {
            string? folder = KnownFolders.TryGetPath(id);
            return folder is null ? null : PathUtil.NormalizeLoose(folder + name[38..]);
        }
        return PathUtil.NormalizeLoose(name);
    }
}
