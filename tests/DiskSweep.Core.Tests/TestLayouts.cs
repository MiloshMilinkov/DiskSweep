using DiskSweep.Core.Safety;

namespace DiskSweep.Core.Tests;

/// <summary>A fake PC on drive Q: so rule tests never depend on (or touch) the real machine.</summary>
internal static class TestLayouts
{
    public static ProtectionLayout Synthetic(params string[] exclusions) => new()
    {
        SystemDrive = @"Q:\",
        WindowsDirectory = @"Q:\Windows",
        ProgramFilesDirectories = [@"Q:\Program Files", @"Q:\Program Files (x86)"],
        ProgramDataDirectory = @"Q:\ProgramData",
        UserProfileDirectories = [@"Q:\Users\alice", @"Q:\Users\bob", @"Q:\Users\Public", @"Q:\Users\Default"],
        AppDataDirectories = [@"Q:\Users\alice\AppData", @"Q:\Users\bob\AppData"],
        ScanAnchorDirectories =
        [
            @"Q:\Users\alice\AppData\Roaming", @"Q:\Users\alice\AppData\Local", @"Q:\Users\alice\AppData\LocalLow",
            @"Q:\Users\alice\AppData\Local\Programs", @"Q:\Users\alice\AppData\Local\Packages",
        ],
        TempDirectories = [@"Q:\Windows\Temp", @"Q:\Users\alice\AppData\Local\Temp"],
        InstalledProgramDirectories =
        [
            new(@"Q:\Program Files\Contoso\Editor", "Belongs to the installed program \"Contoso Editor\"."),
            new(@"Q:\Users\alice\AppData\Local\Programs\Fabrikam", "Belongs to the installed program \"Fabrikam\"."),
            new(@"Q:\Games\Adventure", "Belongs to the installed program \"Adventure\"."),
        ],
        OtherProtectedDirectories = [new(@"Q:\Users\Default", "Template for new user accounts.")],
        ProtectedFiles = [new(@"Q:\Users\alice\Downloads\setup-uninstall.exe", "Uninstaller of an installed program.")],
        CloudSyncRoots = [@"Q:\Users\alice\OneDrive", @"G:\"],
        AppOwnDirectories = [@"Q:\Users\alice\AppData\Local\DiskSweep"],
        UserExclusions = exclusions,
    };

    /// <summary>Probe that reports every path as an ordinary folder, except the ones configured.</summary>
    public sealed class FakeProbe : IFileAttributeProbe
    {
        private readonly Dictionary<string, FileAttributes?> _overrides = new(StringComparer.OrdinalIgnoreCase);

        public FakeProbe Set(string path, FileAttributes? attributes)
        {
            _overrides[path] = attributes;
            return this;
        }

        public FileAttributes? GetAttributes(string path) =>
            _overrides.TryGetValue(path, out var a) ? a : FileAttributes.Directory;
    }

    public static SafetyGuard Guard(FakeProbe? probe = null, params string[] exclusions) =>
        new(Synthetic(exclusions), probe ?? new FakeProbe());
}
