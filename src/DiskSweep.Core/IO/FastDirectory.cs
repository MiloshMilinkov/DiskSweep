using System.Runtime.InteropServices;
using DiskSweep.Core.Native;

namespace DiskSweep.Core.IO;

/// <summary>
/// Lists one directory level with FindFirstFileEx. Never recurses and never follows links:
/// callers decide what to descend into after checking <see cref="FsEntry.IsReparsePoint"/>.
/// </summary>
public static class FastDirectory
{
    /// <summary>Fills <paramref name="into"/> with the children of <paramref name="directory"/>. Returns false with a Win32 error code when it can't be read completely.</summary>
    public static unsafe bool TryList(string directory, List<FsEntry> into, out int error)
    {
        into.Clear();
        string search = LongPath.ToExtended(Path.Join(directory, "*"));
        nint handle = NativeMethods.FindFirstFileEx(search, NativeMethods.FindExInfoBasic, out var data,
            NativeMethods.FindExSearchNameMatch, 0, NativeMethods.FIND_FIRST_EX_LARGE_FETCH);

        if (handle == -1)
        {
            error = Marshal.GetLastPInvokeError();
            if (error == NativeMethods.ERROR_FILE_NOT_FOUND) { error = 0; return true; } // empty volume root
            return false;
        }

        try
        {
            do
            {
                string name = new(data.cFileName);
                if (name is "." or "..") continue;

                var attributes = (FileAttributes)data.dwFileAttributes;
                long size = ((long)data.nFileSizeHigh << 32) | data.nFileSizeLow;
                uint tag = FileAttributeFlags.IsReparsePoint(attributes) ? data.dwReserved0 : 0;

                into.Add(new FsEntry(
                    name,
                    Path.Join(directory, name),
                    attributes,
                    size,
                    NativeMethods.FromFileTime(data.ftCreationTime) ?? DateTime.MinValue,
                    NativeMethods.FromFileTime(data.ftLastWriteTime) ?? DateTime.MinValue,
                    NativeMethods.FromFileTime(data.ftLastAccessTime) ?? DateTime.MinValue,
                    tag));
            }
            while (NativeMethods.FindNextFile(handle, out data));

            int last = Marshal.GetLastPInvokeError();
            error = last == NativeMethods.ERROR_NO_MORE_FILES ? 0 : last;
            return error == 0;
        }
        finally
        {
            NativeMethods.FindClose(handle);
        }
    }

    public static string DescribeError(int error) => error switch
    {
        NativeMethods.ERROR_ACCESS_DENIED => "Access denied",
        NativeMethods.ERROR_PATH_NOT_FOUND or NativeMethods.ERROR_FILE_NOT_FOUND => "No longer exists",
        NativeMethods.ERROR_INVALID_NAME => "Invalid name",
        _ => $"Can't be read (Windows error {error})",
    };

    /// <summary>Attributes of a single path without following links, or null when it can't be read.</summary>
    public static FileAttributes? GetAttributes(string path, out int error)
    {
        uint a = NativeMethods.GetFileAttributes(LongPath.ToExtended(path));
        if (a == NativeMethods.INVALID_FILE_ATTRIBUTES)
        {
            error = Marshal.GetLastPInvokeError();
            return null;
        }
        error = 0;
        return (FileAttributes)a;
    }

    /// <summary>True = exists, false = definitely missing, null = can't tell (for example access denied).</summary>
    public static bool? Exists(string path)
    {
        if (GetAttributes(path, out int error) is not null) return true;
        return error is NativeMethods.ERROR_FILE_NOT_FOUND or NativeMethods.ERROR_PATH_NOT_FOUND ? false : null;
    }
}
