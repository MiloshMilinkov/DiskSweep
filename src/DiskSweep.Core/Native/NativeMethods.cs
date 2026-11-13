using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskSweep.Core.Native;

/// <summary>Read-only Win32 calls used by the engine. Nothing here writes to disk or the registry.</summary>
internal static unsafe partial class NativeMethods
{
    public const int ERROR_FILE_NOT_FOUND = 2;
    public const int ERROR_PATH_NOT_FOUND = 3;
    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_NO_MORE_FILES = 18;
    public const int ERROR_SHARING_VIOLATION = 32;
    public const int ERROR_LOCK_VIOLATION = 33;
    public const int ERROR_INVALID_NAME = 123;

    public const uint INVALID_FILE_ATTRIBUTES = 0xFFFFFFFF;

    public const int FindExInfoBasic = 1;
    public const int FindExSearchNameMatch = 0;
    public const int FIND_FIRST_EX_LARGE_FETCH = 2;

    public const uint DELETE = 0x00010000;
    public const uint FILE_SHARE_ALL = 0x00000007; // read | write | delete
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    public struct FILETIME
    {
        public uint Low;
        public uint High;
        public readonly long Value => ((long)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WIN32_FIND_DATAW
    {
        public uint dwFileAttributes;
        public FILETIME ftCreationTime;
        public FILETIME ftLastAccessTime;
        public FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0; // reparse tag when FILE_ATTRIBUTE_REPARSE_POINT is set
        public uint dwReserved1;
        public fixed char cFileName[260];
        public fixed char cAlternateFileName[14];
    }

    [LibraryImport("kernel32.dll", EntryPoint = "FindFirstFileExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint FindFirstFileEx(string lpFileName, int fInfoLevelId, out WIN32_FIND_DATAW lpFindFileData,
        int fSearchOp, nint lpSearchFilter, int dwAdditionalFlags);

    [LibraryImport("kernel32.dll", EntryPoint = "FindNextFileW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FindNextFile(nint hFindFile, out WIN32_FIND_DATAW lpFindFileData);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FindClose(nint hFindFile);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileAttributesW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint GetFileAttributes(string lpFileName);

    [LibraryImport("kernel32.dll", EntryPoint = "GetLongPathNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint GetLongPathName(string lpszShortPath, char* lpszLongPath, uint cchBuffer);

    [LibraryImport("kernel32.dll", EntryPoint = "GetShortPathNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint GetShortPathName(string lpszLongPath, char* lpszShortPath, uint cchBuffer);

    /// <summary>Used only to ask "could this be moved right now?" with every share mode granted, so other programs are never blocked.</summary>
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        nint lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, nint hTemplateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryFullProcessImageName(nint hProcess, uint dwFlags, char* lpExeName, ref uint lpdwSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint hObject);

    [LibraryImport("advapi32.dll", EntryPoint = "RegQueryInfoKeyW")]
    public static partial int RegQueryInfoKey(SafeRegistryHandle hKey, nint lpClass, nint lpcchClass, nint lpReserved,
        nint lpcSubKeys, nint lpcbMaxSubKeyLen, nint lpcbMaxClassLen, nint lpcValues, nint lpcbMaxValueNameLen,
        nint lpcbMaxValueLen, nint lpcbSecurityDescriptor, out FILETIME lpftLastWriteTime);

    [LibraryImport("shell32.dll")]
    public static partial int SHGetKnownFolderPath(in Guid rfid, uint dwFlags, nint hToken, out nint ppszPath);

    public static DateTime? FromFileTime(FILETIME ft) => FromFileTime(ft.Value);

    public static DateTime? FromFileTime(long value)
    {
        if (value <= 0) return null;
        try { return DateTime.FromFileTimeUtc(value); }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}
