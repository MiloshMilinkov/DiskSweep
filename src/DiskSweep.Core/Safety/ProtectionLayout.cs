namespace DiskSweep.Core.Safety;

public sealed record ProtectedPath(string Path, string Reason);

/// <summary>
/// Everything the <see cref="SafetyGuard"/> needs to know about this PC. Built once per scan from the live
/// system (see <c>SystemSnapshot</c>), or by hand in tests.
/// </summary>
public sealed class ProtectionLayout
{
    /// <summary>"C:\" — the drive Windows runs from.</summary>
    public required string SystemDrive { get; init; }

    /// <summary>"C:\Windows". Everything below it is protected except the contents of Windows\Temp.</summary>
    public required string WindowsDirectory { get; init; }

    /// <summary>Program Files, Program Files (x86), Program Files (Arm). The folders themselves are protected.</summary>
    public required IReadOnlyList<string> ProgramFilesDirectories { get; init; }

    /// <summary>"C:\ProgramData". The folder itself and ProgramData\Microsoft are protected.</summary>
    public required string ProgramDataDirectory { get; init; }

    /// <summary>Every user profile root, including Default and Public. The folders themselves are protected.</summary>
    public required IReadOnlyList<string> UserProfileDirectories { get; init; }

    /// <summary>Each profile's AppData folder. Used to decide whether an item sits in an "app area".</summary>
    public IReadOnlyList<string> AppDataDirectories { get; init; } = [];

    /// <summary>Folders DiskSweep looks inside (AppData roots, temp folders…). They are never items themselves.</summary>
    public IReadOnlyList<string> ScanAnchorDirectories { get; init; } = [];

    /// <summary>Temp folders whose contents belong to the temp-files category.</summary>
    public IReadOnlyList<string> TempDirectories { get; init; } = [];

    /// <summary>Folders that belong to an installed program. Whole subtree protected.</summary>
    public IReadOnlyList<ProtectedPath> InstalledProgramDirectories { get; init; } = [];

    /// <summary>Other folders protected with everything inside them, such as the Default user profile Windows copies for new accounts.</summary>
    public IReadOnlyList<ProtectedPath> OtherProtectedDirectories { get; init; } = [];

    /// <summary>Single files that must stay, such as a program's own uninstaller.</summary>
    public IReadOnlyList<ProtectedPath> ProtectedFiles { get; init; } = [];

    /// <summary>OneDrive, Dropbox, Google Drive, iCloud… Whole subtree excluded from every scan.</summary>
    public IReadOnlyList<string> CloudSyncRoots { get; init; } = [];

    /// <summary>DiskSweep's own program, settings, log and quarantine folders.</summary>
    public IReadOnlyList<string> AppOwnDirectories { get; init; } = [];

    /// <summary>The user's exclusion list.</summary>
    public IReadOnlyList<string> UserExclusions { get; init; } = [];
}
