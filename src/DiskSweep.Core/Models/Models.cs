namespace DiskSweep.Core.Models;

public enum RiskLevel { Safe, Review, Risky }

public enum ScanCategory
{
    EmptyFolders,
    Leftovers,
    /// <summary>Produced by the leftovers scan. Report only: the registry is never edited.</summary>
    OrphanedRegistry,
    UnusedPrograms,
    TempAndOldFiles,
}

public enum ItemKind { File, Folder, Program, RegistryKey }

public sealed class ScanItem
{
    public required ScanCategory Category { get; init; }
    public required ItemKind Kind { get; init; }

    /// <summary>File-system path, or the registry key path for <see cref="ItemKind.RegistryKey"/>, or the install folder / uninstall key for programs.</summary>
    public required string Path { get; init; }

    public string? DisplayName { get; init; }
    public long? SizeBytes { get; init; }
    public DateTime? LastModifiedUtc { get; init; }
    public DateTime? LastUsedUtc { get; init; }
    public string? LastUsedSource { get; init; }
    public DateTime? InstalledUtc { get; init; }

    public required string Reason { get; init; }
    public required RiskLevel Risk { get; init; }

    /// <summary>0–100, leftovers only.</summary>
    public int? Confidence { get; init; }

    public IReadOnlyList<string> Evidence { get; init; } = [];

    /// <summary>Shown but never selectable (for example registry keys).</summary>
    public bool IsReportOnly { get; init; }
    public string? ReportOnlyReason { get; init; }

    /// <summary>Set by the scanner when a junction, symbolic link or other reparse point exists anywhere inside a folder item.</summary>
    public string? ContainsReparsePoint { get; init; }

    /// <summary>Programs only: the uninstall commands exactly as registered by the program itself.</summary>
    public string? UninstallCommand { get; init; }
    public string? QuietUninstallCommand { get; init; }
    public string? Publisher { get; init; }
}

public sealed record DriveSummary(
    string RootPath,
    string Label,
    string FileSystem,
    DriveType DriveType,
    long TotalBytes,
    long FreeBytes,
    bool IsSystemDrive)
{
    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);
    public string Letter => RootPath.TrimEnd('\\');
}

public sealed record SkippedEntry(string Path, string Reason);

public sealed record ScanOptions
{
    public int EmptyFolderMinAgeDays { get; init; } = 7;
    public int TempMinAgeDays { get; init; } = 2;
    public int UnusedProgramDays { get; init; } = 180;
    public long LargeFileBytes { get; init; } = 500L * 1024 * 1024;
    public int OldFileDays { get; init; } = 365;

    /// <summary>User exclusions (folders or files). Everything inside an excluded folder is left alone.</summary>
    public IReadOnlyList<string> ExcludedPaths { get; init; } = [];
}

public sealed record ScanRequest(DriveSummary Drive, IReadOnlySet<ScanCategory> Categories, ScanOptions Options);

public sealed class ScanResult
{
    public required ScanRequest Request { get; init; }
    public required IReadOnlyList<ScanItem> Items { get; init; }
    public required IReadOnlyList<SkippedEntry> Skipped { get; init; }
    public required int SkippedTotal { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
    public required bool WasCancelled { get; init; }
    public required TimeSpan Duration { get; init; }

    /// <summary>The result categories this scan produced, in display order (Leftovers also brings OrphanedRegistry).</summary>
    public required IReadOnlyList<ScanCategory> Categories { get; init; }
    public string? LogFilePath { get; init; }

    /// <summary>The guard built from this scan's view of the PC; the UI uses it for every tick.</summary>
    public Safety.SafetyGuard? Guard { get; init; }
}

public static class ScanCategoryInfo
{
    /// <summary>The categories a user can choose to scan, in display order.</summary>
    public static readonly IReadOnlyList<ScanCategory> Selectable =
        Enum.GetValues<ScanCategory>().Where(c => c != ScanCategory.OrphanedRegistry).ToArray();

    /// <summary>Every result category a scan of <paramref name="requested"/> produces, in display order.</summary>
    public static IReadOnlyList<ScanCategory> ResultCategories(IReadOnlySet<ScanCategory> requested) =>
        Enum.GetValues<ScanCategory>()
            .Where(c => requested.Contains(c) || (c == ScanCategory.OrphanedRegistry && requested.Contains(ScanCategory.Leftovers)))
            .ToArray();

    public static string Title(ScanCategory c) => c switch
    {
        ScanCategory.EmptyFolders => "Empty folders",
        ScanCategory.Leftovers => "Program leftovers",
        ScanCategory.OrphanedRegistry => "Registry (report only)",
        ScanCategory.UnusedPrograms => "Unused programs",
        ScanCategory.TempAndOldFiles => "Temp, large & old files",
        _ => c.ToString(),
    };

    public static string Description(ScanCategory c) => c switch
    {
        ScanCategory.EmptyFolders => $"Folders with nothing in them, older than {new ScanOptions().EmptyFolderMinAgeDays} days.",
        ScanCategory.Leftovers => "Folders left behind by programs that are no longer installed.",
        ScanCategory.UnusedPrograms => "Installed programs you haven't opened in a long time.",
        ScanCategory.TempAndOldFiles => "Old temp files, plus large and old files in your user folders.",
        _ => "",
    };
}
