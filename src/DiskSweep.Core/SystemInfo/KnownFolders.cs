using System.Runtime.InteropServices;
using DiskSweep.Core.IO;
using DiskSweep.Core.Native;

namespace DiskSweep.Core.SystemInfo;

public static class KnownFolders
{
    public static readonly Guid Downloads = new("374DE290-123F-4565-9164-39C4925E467B");
    public static readonly Guid LocalAppDataLow = new("A520A1A4-1780-4FF6-BD18-167343C5AF16");

    /// <summary>Current user's path for a known folder (handles redirection), or null.</summary>
    public static string? TryGetPath(Guid id)
    {
        try
        {
            int hr = NativeMethods.SHGetKnownFolderPath(id, 0, 0, out nint ptr);
            if (hr != 0 || ptr == 0) return null;
            try { return PathUtil.NormalizeLoose(Marshal.PtrToStringUni(ptr)); }
            finally { Marshal.FreeCoTaskMem(ptr); }
        }
        catch (Exception ex) when (ex is COMException or ArgumentException) { return null; }
    }
}
