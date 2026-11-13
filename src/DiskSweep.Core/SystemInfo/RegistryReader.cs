using System.Security;
using DiskSweep.Core.Native;
using Microsoft.Win32;

namespace DiskSweep.Core.SystemInfo;

/// <summary>Read-only registry helpers. Every key is opened without write access; DiskSweep never edits the registry.</summary>
internal static class RegistryReader
{
    private static bool IsReadError(Exception ex) => ex is SecurityException or UnauthorizedAccessException or IOException;

    public static RegistryKey? Open(RegistryKey root, string path) => Open(root, path, out _);

    /// <summary>Opens a key and reports whether it was denied (as opposed to simply missing).</summary>
    public static RegistryKey? Open(RegistryKey root, string path, out bool denied)
    {
        denied = false;
        try { return root.OpenSubKey(path, writable: false); }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException)
        {
            denied = true;
            return null;
        }
        catch (IOException) { return null; }
    }

    public static string[] SubKeyNames(RegistryKey key)
    {
        try { return key.GetSubKeyNames(); }
        catch (Exception ex) when (IsReadError(ex)) { return []; }
    }

    public static string[] ValueNames(RegistryKey key)
    {
        try { return key.GetValueNames(); }
        catch (Exception ex) when (IsReadError(ex)) { return []; }
    }

    public static string? String(RegistryKey key, string name)
    {
        try
        {
            return key.GetValue(name) switch
            {
                string s => s.Trim(),
                string[] arr => string.Join(" ", arr),
                _ => null,
            };
        }
        catch (Exception ex) when (IsReadError(ex)) { return null; }
    }

    public static int? Int(RegistryKey key, string name)
    {
        try
        {
            return key.GetValue(name) switch
            {
                int i => i,
                long l => (int)l,
                string s when int.TryParse(s, out int v) => v,
                _ => null,
            };
        }
        catch (Exception ex) when (IsReadError(ex)) { return null; }
    }

    public static byte[]? Bytes(RegistryKey key, string name)
    {
        try { return key.GetValue(name) as byte[]; }
        catch (Exception ex) when (IsReadError(ex)) { return null; }
    }

    public static DateTime? LastWriteTime(RegistryKey key)
    {
        try
        {
            int rc = NativeMethods.RegQueryInfoKey(key.Handle, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, out var ft);
            return rc == 0 ? NativeMethods.FromFileTime(ft) : null;
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SecurityException or UnauthorizedAccessException) { return null; }
    }
}
