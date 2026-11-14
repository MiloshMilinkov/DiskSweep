using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using DiskSweep.Core.IO;
using DiskSweep.Core.Native;
using DiskSweep.Core.SystemInfo;
using Microsoft.Win32;

namespace DiskSweep.Core.Cleanup;

public enum RecycleStatus { Recycled, Refused, Failed }

public readonly record struct RecycleResult(RecycleStatus Status, string Detail);

public interface IRecycleBin
{
    /// <summary>
    /// Sends one item to the Recycle Bin, but only when Windows would really recycle it. Items it would delete
    /// permanently instead are refused and left where they are.
    /// </summary>
    RecycleResult Recycle(string path, long sizeBytes);
}

/// <summary>The rules for when Windows would permanently delete instead of recycling. Pure, so it can be tested.</summary>
public static class RecycleBinPolicy
{
    public const int MaxPathLength = 259;

    /// <param name="capacityBytes">The Bin's size limit on this drive, or null when Windows doesn't say.</param>
    /// <param name="binUsedBytes">What's already in the Bin on this drive, including items recycled earlier in this run.</param>
    public static string? WhyNot(int pathLength, DriveType driveType, bool nukeOnDelete, long? capacityBytes,
        long driveTotalBytes, long binUsedBytes, long itemBytes)
    {
        if (pathLength > MaxPathLength)
            return "The path is longer than 260 characters, which the Recycle Bin can't take.";
        if (driveType != DriveType.Fixed)
            return "This drive has no Recycle Bin (removable or network drive).";
        if (nukeOnDelete)
            return "The Recycle Bin on this drive is set to delete files immediately.";

        // Windows doesn't always record a limit; assume the smallest default (5% of the drive) to stay on the safe side.
        long limit = capacityBytes ?? driveTotalBytes / 20;
        if (itemBytes > limit)
            return $"Bigger than the Recycle Bin's size limit on this drive ({PathUtil.FormatBytes(limit)}).";
        // When the Bin is full, Windows makes room by permanently deleting its oldest items (possibly ones this run just moved).
        if (binUsedBytes + itemBytes > limit)
            return $"The Recycle Bin on this drive is too full ({PathUtil.FormatBytes(binUsedBytes)} of {PathUtil.FormatBytes(limit)}): " +
                   "Windows would permanently delete older items to make room. Empty the Bin or use quarantine.";
        return null;
    }
}

/// <summary>Uses the shell's own "delete to Recycle Bin". Never deletes permanently.</summary>
public sealed class WindowsRecycleBin : IRecycleBin
{
    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;
    // Backstop: if Windows still decides to delete permanently, it must ask the user first.
    private const ushort FOF_WANTNUKEWARNING = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCTW
    {
        public nint hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public nint hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCTW lpFileOp);

    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO
    {
        public uint cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBinW(string pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    /// <summary>Drive facts that don't change during a run (the per-volume Bin settings are re-read each time).</summary>
    private sealed record DriveFacts(DriveType Type, long TotalBytes, string? VolumeGuid);

    private readonly ConcurrentDictionary<string, DriveFacts?> _drives = new(PathUtil.Comparer);

    /// <summary>Bytes in each drive's Bin: read once, then increased by what this instance recycles.</summary>
    private readonly ConcurrentDictionary<string, long> _binUsed = new(PathUtil.Comparer);

    /// <summary>Use one instance per cleanup run, so the Bin's fill level is tracked across the run's items.</summary>
    public WindowsRecycleBin() { }

    public RecycleResult Recycle(string path, long sizeBytes)
    {
        if (WhyNotRecyclable(path, sizeBytes) is { } why) return new(RecycleStatus.Refused, why);
        string? error;
        try { error = StaThread.Run(() => Send(path)); }
        catch (Exception ex) { error = $"Windows couldn't move it to the Recycle Bin: {ex.GetBaseException().Message}"; }
        if (error is not null) return new(RecycleStatus.Failed, error);

        _binUsed.AddOrUpdate(PathUtil.DriveRoot(path), sizeBytes, (_, used) => used + sizeBytes);
        return new(RecycleStatus.Recycled, "Moved to the Recycle Bin.");
    }

    private string? WhyNotRecyclable(string path, long sizeBytes)
    {
        string root = PathUtil.DriveRoot(path);
        if (_drives.GetOrAdd(root, ReadDriveFacts) is not { } drive) return "The drive can't be read.";
        long used = _binUsed.GetOrAdd(root, BinUsedBytes);

        bool nuke = false;
        long? capacity = null;
        if (drive.VolumeGuid is { } guid)
        {
            using var key = RegistryReader.Open(Registry.CurrentUser, $@"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume\{guid}");
            if (key is not null)
            {
                nuke = RegistryReader.Int(key, "NukeOnDelete") == 1;
                if (RegistryReader.Int(key, "MaxCapacity") is { } mb and > 0) capacity = mb * 1024L * 1024L;
            }
        }
        return RecycleBinPolicy.WhyNot(path.Length, drive.Type, nuke, capacity, drive.TotalBytes, used, sizeBytes);
    }

    /// <summary>What's in this drive's Recycle Bin now. If Windows can't say, assume it's full, which refuses everything.</summary>
    private static long BinUsedBytes(string root)
    {
        var info = new SHQUERYRBINFO { cbSize = (uint)Marshal.SizeOf<SHQUERYRBINFO>() };
        return SHQueryRecycleBinW(root, ref info) == 0 ? info.i64Size : long.MaxValue / 2;
    }

    private static DriveFacts? ReadDriveFacts(string root)
    {
        try
        {
            var drive = new DriveInfo(root);
            return new DriveFacts(drive.DriveType, drive.TotalSize, VolumeGuid(root));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static string? Send(string path)
    {
        var op = new SHFILEOPSTRUCTW
        {
            wFunc = FO_DELETE,
            pFrom = path + "\0", // the marshaller adds the second terminator the API needs
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT | FOF_WANTNUKEWARNING,
        };
        int rc = SHFileOperationW(ref op);
        if (op.fAnyOperationsAborted) return "Cancelled.";
        if (rc != 0) return $"Windows couldn't move it to the Recycle Bin (error 0x{rc:X}). It may be in use.";
        return FastDirectory.Exists(path) == false ? null : "It's still there after the move; it may be in use.";
    }

    private static unsafe string? VolumeGuid(string root)
    {
        char* buffer = stackalloc char[64];
        if (!NativeMethods.GetVolumeNameForVolumeMountPoint(root, buffer, 64)) return null;
        string name = new(buffer); // \\?\Volume{GUID}\
        int start = name.IndexOf('{');
        int end = name.IndexOf('}');
        return start >= 0 && end > start ? name[start..(end + 1)] : null;
    }
}
