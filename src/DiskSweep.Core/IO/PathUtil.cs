namespace DiskSweep.Core.IO;

/// <summary>Case-insensitive path helpers. Paths passed in are expected to be full, normalized and without a trailing separator (except a drive root like "C:\").</summary>
public static class PathUtil
{
    public static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;
    public const StringComparison Comparison = StringComparison.OrdinalIgnoreCase;

    public static string TrimEndSeparator(string path)
    {
        if (path.Length <= 3) return path; // "C:\"
        return path.TrimEnd('\\', '/');
    }

    public static bool AreSame(string a, string b) => string.Equals(a, b, Comparison);

    /// <summary>True when <paramref name="path"/> equals <paramref name="root"/> or lies anywhere below it.</summary>
    public static bool IsSameOrUnder(string path, string root)
    {
        if (path.Length < root.Length) return false;
        if (!path.StartsWith(root, Comparison)) return false;
        if (path.Length == root.Length) return true;
        return root.EndsWith('\\') || path[root.Length] == '\\';
    }

    /// <summary>True when <paramref name="path"/> lies below <paramref name="root"/> (not equal to it).</summary>
    public static bool IsStrictlyUnder(string path, string root) =>
        path.Length > root.Length && IsSameOrUnder(path, root);

    /// <summary>"C:\" for "C:\foo\bar".</summary>
    public static string DriveRoot(string path) => path.Length >= 3 ? path[..3] : path;

    public static bool IsOnDrive(string path, string driveRoot) =>
        path.Length >= 3 && driveRoot.Length >= 3 && string.Compare(path, 0, driveRoot, 0, 3, Comparison) == 0;

    /// <summary>"X:\…" — a full path on a lettered drive (the only kind DiskSweep ever works with).</summary>
    public static bool IsDrivePath(string p) => p.Length >= 3 && char.IsAsciiLetter(p[0]) && p[1] == ':' && p[2] == '\\';

    public static string? Parent(string path)
    {
        if (path.Length <= 3) return null;
        int i = path.LastIndexOf('\\');
        if (i < 0) return null;
        return i <= 2 ? path[..3] : path[..i];
    }

    /// <summary>Segments without the drive, e.g. "C:\a\b" → ["a","b"].</summary>
    public static string[] Segments(string path) =>
        path.Length <= 3 ? [] : path[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Best-effort normalisation for paths read from the system (registry, environment). No validation.</summary>
    public static string? NormalizeLoose(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string p = LongPath.FromExtended(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"').Trim()).Replace('/', '\\'));
        if (!IsDrivePath(p)) return null;
        try { p = Path.GetFullPath(p); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        p = TrimEndSeparator(p);
        return char.ToUpperInvariant(p[0]) + p[1..];
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes} B" : $"{v:0.#} {units[u]}";
    }
}
