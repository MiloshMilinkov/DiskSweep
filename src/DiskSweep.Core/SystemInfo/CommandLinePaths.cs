using System.Text.RegularExpressions;
using DiskSweep.Core.IO;

namespace DiskSweep.Core.SystemInfo;

/// <summary>Pulls absolute file paths out of command lines like registry ImagePath, UninstallString or Run values.</summary>
public static partial class CommandLinePaths
{
    [GeneratedRegex(@"(?<![A-Za-z])[A-Za-z]:\\[^""<>|*?\r\n]*?\.(?:exe|dll|sys|com|bat|cmd|msi|ico|ps1|vbs|js|lnk|scr|cpl|ocx|jar|py)(?![A-Za-z0-9])",
        RegexOptions.IgnoreCase)]
    private static partial Regex PathRegex();

    private static readonly string WindowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    /// <summary>Expands environment variables and the NT prefixes Windows uses (\SystemRoot\, \??\).</summary>
    public static string Prepare(string commandLine)
    {
        string s = Environment.ExpandEnvironmentVariables(commandLine);
        if (s.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)) s = WindowsDirectory + s[@"\SystemRoot".Length..];
        s = s.Replace(@"\??\", "", StringComparison.Ordinal);
        if (s.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase)) s = Path.Combine(WindowsDirectory, s);
        return s;
    }

    public static IReadOnlyList<string> Extract(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return [];
        string s = Prepare(commandLine);
        var results = new List<string>();
        foreach (Match m in PathRegex().Matches(s))
            if (PathUtil.NormalizeLoose(m.Value) is { } p) results.Add(p);
        return results;
    }
}
