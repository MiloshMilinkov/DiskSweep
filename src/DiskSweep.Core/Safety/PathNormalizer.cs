using DiskSweep.Core.IO;
using DiskSweep.Core.Native;

namespace DiskSweep.Core.Safety;

/// <summary>
/// Turns any user- or scanner-supplied path into one canonical local form before safety rules run,
/// so tricks like "..", forward slashes, 8.3 short names, \\?\ prefixes or stream names can't slip past them.
/// </summary>
public static class PathNormalizer
{
    private static readonly char[] InvalidChars = ['<', '>', '"', '|', '?', '*'];

    public static bool TryNormalize(string? path, out string normalized, out string error)
    {
        normalized = "";
        error = "";
        if (string.IsNullOrWhiteSpace(path)) { error = "Empty path."; return false; }

        string p = LongPath.FromExtended(path.Trim().Replace('/', '\\'));
        if (p.StartsWith(@"\\", PathUtil.Comparison)) { error = "Network and device paths are not allowed."; return false; }

        if (!PathUtil.IsDrivePath(p))
        {
            error = "Only full paths on a local drive are allowed.";
            return false;
        }
        if (p.IndexOf(':', 2) >= 0) { error = "Alternate data streams are not allowed."; return false; }
        if (p.IndexOfAny(InvalidChars) >= 0 || p.Any(char.IsControl)) { error = "The path contains invalid characters."; return false; }

        try
        {
            p = Path.GetFullPath(p); // resolves "." and "..", collapses separators, trims trailing dots/spaces
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = "The path can't be read.";
            return false;
        }

        // GetFullPath may turn reserved names into device paths; refuse anything that is no longer a drive path.
        if (!PathUtil.IsDrivePath(p))
        {
            error = "Only full paths on a local drive are allowed.";
            return false;
        }

        if (p.Contains('~')) p = ExpandShortNames(p);

        p = PathUtil.TrimEndSeparator(p);
        normalized = char.ToUpperInvariant(p[0]) + p[1..];
        return true;
    }

    /// <summary>Resolves 8.3 short names (C:\PROGRA~1) on the longest existing prefix of the path.</summary>
    internal static string ExpandShortNames(string path)
    {
        string? existing = path;
        string suffix = "";
        while (existing is not null)
        {
            string? resolved = QueryPathName(existing, longForm: true);
            if (resolved is not null)
                return suffix.Length == 0 ? PathUtil.TrimEndSeparator(resolved) : resolved.TrimEnd('\\') + suffix;
            string? parent = PathUtil.Parent(existing);
            if (parent is null) break;
            suffix = existing[parent.TrimEnd('\\').Length..] + suffix;
            existing = parent;
        }
        return path;
    }

    /// <summary>8.3 form of an existing path, or null when the volume has none.</summary>
    public static string? GetShortPathName(string path)
    {
        string? s = QueryPathName(path, longForm: false);
        if (s is null) return null;
        s = PathUtil.TrimEndSeparator(s);
        return PathUtil.AreSame(s, path) ? null : s;
    }

    /// <summary>GetLongPathName / GetShortPathName with a small stack buffer, growing only when Windows asks for more.</summary>
    private static unsafe string? QueryPathName(string path, bool longForm)
    {
        string extended = LongPath.ToExtended(path);
        const int StackSize = 1024;
        char* stack = stackalloc char[StackSize];
        uint len = Call(extended, stack, StackSize, longForm);
        if (len == 0) return null;
        if (len < StackSize) return LongPath.FromExtended(new string(stack, 0, (int)len));

        char[] heap = new char[len]; // len is the size needed, terminator included
        fixed (char* b = heap)
        {
            uint written = Call(extended, b, len, longForm);
            return written == 0 || written >= len ? null : LongPath.FromExtended(new string(b, 0, (int)written));
        }

        static uint Call(string p, char* buffer, uint size, bool longForm) => longForm
            ? NativeMethods.GetLongPathName(p, buffer, size)
            : NativeMethods.GetShortPathName(p, buffer, size);
    }
}
