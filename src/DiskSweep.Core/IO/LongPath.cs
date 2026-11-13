namespace DiskSweep.Core.IO;

/// <summary>Adds the \\?\ prefix so Win32 calls work beyond 260 characters. Only for full, already-normalized local paths.</summary>
public static class LongPath
{
    public const string Prefix = @"\\?\";

    public static string ToExtended(string path)
    {
        if (path.StartsWith(Prefix, StringComparison.Ordinal)) return path;
        return Prefix + path;
    }

    /// <summary>Removes a \\?\ or NT \??\ prefix. (\\?\UNC\ is left alone so callers can still reject network paths.)</summary>
    public static string FromExtended(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return path;
        if (path.StartsWith(Prefix, StringComparison.Ordinal) || path.StartsWith(@"\??\", StringComparison.Ordinal)) return path[4..];
        return path;
    }
}
