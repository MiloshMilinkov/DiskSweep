using System.Runtime.InteropServices;
using System.Text;
using DiskSweep.Core.IO;
using DiskSweep.Core.Logging;
using DiskSweep.Core.Safety;
using Microsoft.Win32;

namespace DiskSweep.Core.SystemInfo;

public sealed record ReferenceEntry(string Source, string Text);

/// <summary>
/// Everything on the PC that can point into a folder and start code from it: services, drivers,
/// scheduled tasks and startup entries. Used to make sure a "leftover" isn't still in use.
/// </summary>
public sealed class ReferenceIndex
{
    private readonly List<ReferenceEntry> _entries;

    /// <summary>Plain-language notes about sources that couldn't be read completely.</summary>
    public IReadOnlyList<string> IncompleteSources { get; }

    internal ReferenceIndex(List<ReferenceEntry> entries, IReadOnlyList<string> incomplete)
    {
        _entries = entries;
        IncompleteSources = incomplete;
    }

    /// <summary>Test helper.</summary>
    public static ReferenceIndex FromEntries(IEnumerable<(string Source, string Text)> entries, params string[] incomplete) =>
        new(entries.Select(e => new ReferenceEntry(e.Source, CommandLinePaths.Prepare(e.Text).ToLowerInvariant())).ToList(), incomplete);

    /// <summary>First entry that refers to <paramref name="folder"/> or anything inside it (also by its 8.3 short name).</summary>
    public ReferenceEntry? FindReferenceInto(string folder)
    {
        var needles = new List<string> { folder.ToLowerInvariant() };
        if (PathNormalizer.GetShortPathName(folder) is { } shortName) needles.Add(shortName.ToLowerInvariant());

        foreach (var entry in _entries)
            foreach (string needle in needles)
                if (ContainsPath(entry.Text, needle)) return entry;
        return null;
    }

    /// <summary>True when <paramref name="text"/> contains <paramref name="folder"/> followed by a path boundary.</summary>
    internal static bool ContainsPath(string text, string folder)
    {
        int start = 0;
        while (true)
        {
            int i = text.IndexOf(folder, start, StringComparison.Ordinal);
            if (i < 0) return false;
            int end = i + folder.Length;
            // Deliberately loose: "C:\App" also matches "C:\App Data\x.exe". A false match only means we don't flag something.
            if (end >= text.Length || text[end] is '\\' or '"' or '\'' or ' ' or ',' or ';' or '/' or '\0' or '\r' or '\n' or '<') return true;
            start = i + 1;
        }
    }

    public static ReferenceIndex Build(ProfileSet profiles, bool isAdmin, IActionLog log, CancellationToken token)
    {
        var entries = new List<ReferenceEntry>();
        var incomplete = new List<string>();

        void Add(string source, string? text)
        {
            if (!string.IsNullOrWhiteSpace(text))
                entries.Add(new ReferenceEntry(source, CommandLinePaths.Prepare(text).ToLowerInvariant()));
        }

        ReadServices(Add, incomplete);
        token.ThrowIfCancellationRequested();
        ReadScheduledTasks(Add, incomplete, isAdmin, log);
        token.ThrowIfCancellationRequested();
        ReadRunKeys(profiles, Add);
        ReadStartupFolders(profiles, Add, incomplete);

        log.Info($"Indexed {entries.Count} service, driver, task and startup references.");
        foreach (string gap in incomplete) log.Warn($"Reference check incomplete: {gap}");
        return new ReferenceIndex(entries, incomplete);
    }

    private static void ReadServices(Action<string, string?> add, List<string> incomplete)
    {
        using var services = RegistryReader.Open(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Services");
        if (services is null)
        {
            incomplete.Add("The list of services and drivers couldn't be read.");
            return;
        }

        int failed = 0;
        foreach (string name in RegistryReader.SubKeyNames(services))
        {
            using var k = RegistryReader.Open(services, name, out bool denied);
            if (k is null)
            {
                if (denied) failed++;
                continue;
            }
            add($"Service or driver \"{name}\"", RegistryReader.String(k, "ImagePath"));
            add($"Service \"{name}\"", RegistryReader.String(k, "ServiceDll"));
            using var p = RegistryReader.Open(k, "Parameters");
            if (p is not null) add($"Service \"{name}\"", RegistryReader.String(p, "ServiceDll"));
        }
        if (failed > 0) incomplete.Add($"{failed} service or driver entries couldn't be read.");
    }

    private static void ReadScheduledTasks(Action<string, string?> add, List<string> incomplete, bool isAdmin, IActionLog log)
    {
        if (!isAdmin) incomplete.Add("Some scheduled tasks are only visible to administrators.");

        object? service = null;
        try
        {
            Type? type = Type.GetTypeFromProgID("Schedule.Service");
            if (type is null)
            {
                incomplete.Add("The Task Scheduler couldn't be opened.");
                return;
            }
            service = Activator.CreateInstance(type);
            dynamic svc = service!;
            svc.Connect();
            int failedFolders = 0, failedTasks = 0;
            object rootFolder = svc.GetFolder("\\");
            WalkTaskFolder(rootFolder, add, ref failedFolders, ref failedTasks, 0);
            if (failedFolders + failedTasks > 0)
                incomplete.Add($"{failedFolders} scheduled-task folders and {failedTasks} tasks couldn't be read.");
        }
        catch (Exception ex)
        {
            log.Error("Reading scheduled tasks failed", ex);
            incomplete.Add("Scheduled tasks couldn't be read.");
        }
        finally
        {
            if (service is not null && Marshal.IsComObject(service)) Marshal.FinalReleaseComObject(service);
        }
    }

    private static void WalkTaskFolder(object folderObject, Action<string, string?> add, ref int failedFolders, ref int failedTasks, int depth)
    {
        if (depth > 32) return;
        dynamic folder = folderObject;

        try
        {
            dynamic tasks = folder.GetTasks(1); // TASK_ENUM_HIDDEN
            int count = tasks.Count;
            for (int i = 1; i <= count; i++)
            {
                try
                {
                    dynamic task = tasks.Item(i);
                    string name = task.Path;
                    add($"Scheduled task \"{name}\"", (string)task.Xml);
                }
                catch { failedTasks++; }
            }
        }
        catch { failedFolders++; }

        try
        {
            dynamic subfolders = folder.GetFolders(0);
            int count = subfolders.Count;
            for (int i = 1; i <= count; i++)
            {
                try
                {
                    object sub = subfolders.Item(i);
                    WalkTaskFolder(sub, add, ref failedFolders, ref failedTasks, depth + 1);
                }
                catch { failedFolders++; }
            }
        }
        catch { failedFolders++; }
    }

    private static readonly string[] RunKeyPaths =
    [
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunServices",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer\Run",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce",
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon",
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows",
    ];

    private static void ReadRunKeys(ProfileSet profiles, Action<string, string?> add)
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        ReadRunKeysUnder(hklm, "", "HKLM", add);
        ReadRunKeysUnder(Registry.CurrentUser, "", "HKCU", add);
        foreach (var u in profiles.Users.Where(u => !u.IsCurrentUser && u.HiveReadable))
            ReadRunKeysUnder(Registry.Users, u.Sid + "\\", $"HKU\\{u.Sid}", add);
    }

    private static void ReadRunKeysUnder(RegistryKey root, string prefix, string label, Action<string, string?> add)
    {
        foreach (string path in RunKeyPaths)
        {
            using var k = RegistryReader.Open(root, prefix + path);
            if (k is null) continue;
            foreach (string value in RegistryReader.ValueNames(k))
                add($"Startup entry {label}\\{path}\\{value}", RegistryReader.String(k, value));
        }
    }

    private static void ReadStartupFolders(ProfileSet profiles, Action<string, string?> add, List<string> incomplete)
    {
        var folders = new List<string>();
        if (PathUtil.NormalizeLoose(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup)) is { } common) folders.Add(common);
        foreach (var u in profiles.Users)
            folders.Add(Path.Combine(u.RoamingAppData, @"Microsoft\Windows\Start Menu\Programs\Startup"));

        var buffer = new List<FsEntry>();
        foreach (string folder in folders.Distinct(PathUtil.Comparer))
        {
            if (!FastDirectory.TryList(folder, buffer, out int error))
            {
                if (error == Native.NativeMethods.ERROR_ACCESS_DENIED)
                    incomplete.Add($"Startup folder couldn't be read: {folder}");
                continue;
            }
            foreach (var e in buffer.Where(e => !e.IsDirectory && !e.IsReparsePoint && e.Size < 1_000_000))
            {
                string source = $"Startup folder item \"{e.FullPath}\"";
                add(source, e.FullPath);
                // Shortcuts and scripts store their target as plain text; reading the bytes covers both without running anything.
                try
                {
                    byte[] bytes = File.ReadAllBytes(e.FullPath);
                    add(source, Encoding.Unicode.GetString(bytes));
                    add(source, Encoding.Latin1.GetString(bytes));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* name already added */ }
            }
        }
    }
}
