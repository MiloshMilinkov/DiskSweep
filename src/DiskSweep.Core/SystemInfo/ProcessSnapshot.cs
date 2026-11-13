using System.Diagnostics;
using DiskSweep.Core.IO;
using DiskSweep.Core.Logging;
using DiskSweep.Core.Native;

namespace DiskSweep.Core.SystemInfo;

/// <summary>Executable paths of every running process DiskSweep is allowed to inspect.</summary>
public sealed class ProcessSnapshot
{
    private static readonly HashSet<string> PseudoProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Idle", "System", "Registry", "Memory Compression", "Secure System", "vmmem", "vmmemWSL",
    };

    public IReadOnlyList<string> ImagePaths { get; }
    public IReadOnlyList<string> UninspectableNames { get; }
    public bool IsComplete => UninspectableNames.Count == 0;

    public ProcessSnapshot(IReadOnlyList<string> paths, IReadOnlyList<string> uninspectable)
    {
        ImagePaths = paths;
        UninspectableNames = uninspectable;
    }

    public string? FindProcessIn(string folder) =>
        ImagePaths.FirstOrDefault(p => PathUtil.IsStrictlyUnder(p, folder));

    public static unsafe ProcessSnapshot Capture(IActionLog log)
    {
        var paths = new List<string>();
        var failed = new List<string>();
        char* buffer = stackalloc char[32_768];

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                string name;
                try { name = process.ProcessName; }
                catch (InvalidOperationException) { continue; } // exited while we were looking
                if (process.Id is 0 or 4 || PseudoProcesses.Contains(name)) continue;
                nint handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)process.Id);
                if (handle == 0)
                {
                    failed.Add(name);
                    continue;
                }
                try
                {
                    uint size = 32_768;
                    if (NativeMethods.QueryFullProcessImageName(handle, 0, buffer, ref size) &&
                        PathUtil.NormalizeLoose(new string(buffer, 0, (int)size)) is { } path)
                        paths.Add(path);
                    else
                        failed.Add(name);
                }
                finally
                {
                    NativeMethods.CloseHandle(handle);
                }
            }
        }

        var distinctFailed = failed.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        log.Info($"Inspected {paths.Count} running processes; {distinctFailed.Count} couldn't be inspected.");
        return new ProcessSnapshot(paths.Distinct(PathUtil.Comparer).ToList(), distinctFailed);
    }
}
