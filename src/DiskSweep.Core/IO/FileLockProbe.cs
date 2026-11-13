using System.Runtime.InteropServices;
using DiskSweep.Core.Native;

namespace DiskSweep.Core.IO;

public enum LockState { Free, InUse, AccessDenied, Missing, Unknown }

/// <summary>
/// Asks Windows whether a file could be moved right now by opening it for DELETE access while granting
/// every share mode. Other programs are never blocked, nothing is changed, and links are not followed.
/// </summary>
public static class FileLockProbe
{
    public static LockState Probe(string path)
    {
        using var handle = NativeMethods.CreateFile(LongPath.ToExtended(path), NativeMethods.DELETE, NativeMethods.FILE_SHARE_ALL,
            0, NativeMethods.OPEN_EXISTING, NativeMethods.FILE_FLAG_OPEN_REPARSE_POINT, 0);
        if (!handle.IsInvalid) return LockState.Free;

        return Marshal.GetLastPInvokeError() switch
        {
            NativeMethods.ERROR_SHARING_VIOLATION or NativeMethods.ERROR_LOCK_VIOLATION => LockState.InUse,
            NativeMethods.ERROR_ACCESS_DENIED => LockState.AccessDenied,
            NativeMethods.ERROR_FILE_NOT_FOUND or NativeMethods.ERROR_PATH_NOT_FOUND => LockState.Missing,
            _ => LockState.Unknown,
        };
    }

    public static string Describe(LockState state) => state switch
    {
        LockState.InUse => "In use by a running program",
        LockState.AccessDenied => "Access denied",
        LockState.Missing => "No longer exists",
        _ => "Couldn't check whether it's in use",
    };
}
