using DiskSweep.Core.Cleanup;
using DiskSweep.Core.Models;
using DiskSweep.Core.Safety;

namespace DiskSweep.Core.Tests;

public class UninstallLauncherTests : IDisposable
{
    private readonly TempTree _tree = new();

    public void Dispose() => _tree.Dispose();

    [Fact]
    public void Quoted_commands_keep_their_arguments()
    {
        string exe = _tree.File(@"App Dir\uninst.exe");
        Assert.True(UninstallLauncher.TrySplit($"\"{exe}\" /remove", out string file, out string args, out _));
        Assert.Equal(exe, file);
        Assert.Equal("/remove", args);
    }

    [Fact]
    public void Unquoted_paths_with_spaces_use_the_longest_existing_exe_not_a_planted_shorter_one()
    {
        string real = _tree.File(@"Program Files\App\uninst.exe");
        _tree.File(@"Program.exe"); // a planted file that a naive split would pick
        string cmd = $"{Path.Combine(_tree.Root, @"Program Files\App\uninst.exe")} /S";

        Assert.True(UninstallLauncher.TrySplit(cmd, out string file, out string args, out _));
        Assert.Equal(real, file);
        Assert.Equal("/S", args);
    }

    [Fact]
    public void Bare_program_names_resolve_only_to_system32()
    {
        Assert.True(UninstallLauncher.TrySplit("MsiExec.exe /X{12345678-1234-1234-1234-123456789012}", out string file, out string args, out _));
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "MsiExec.exe"), file, ignoreCase: true);
        Assert.StartsWith("/X{", args);

        Assert.False(UninstallLauncher.TrySplit("not-a-real-tool.exe /x", out _, out _, out string? error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Missing_uninstallers_are_refused()
    {
        Assert.False(UninstallLauncher.TrySplit($"\"{Path.Combine(_tree.Root, "gone.exe")}\"", out _, out _, out _));
        Assert.False(UninstallLauncher.TrySplit("\"unterminated", out _, out _, out _));
        Assert.False(UninstallLauncher.TrySplit("   ", out _, out _, out _));
    }

    [Fact]
    public void Programs_with_only_a_silent_uninstaller_cannot_be_ticked()
    {
        var quietOnly = new ScanItem
        {
            Category = ScanCategory.UnusedPrograms, Kind = ItemKind.Program, Path = @"Q:\Program Files\X",
            Reason = "test", Risk = RiskLevel.Review, QuietUninstallCommand = "\"Q:\\Program Files\\X\\u.exe\" /S",
        };
        var s = new SelectableItem(quietOnly, TestLayouts.Guard());
        Assert.False(s.TrySetSelected(true));
        Assert.Contains("silent", s.Decision.BlockedReason, StringComparison.OrdinalIgnoreCase);

        // The launcher applies the same rule, so a quiet-only program can't be uninstalled even if a caller skips the tick box.
        var (process, error) = new UninstallLauncher(TestLayouts.Guard(), Logging.NullActionLog.Instance).Launch(quietOnly);
        Assert.Null(process);
        Assert.Contains("silent", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Launch_refuses_without_a_registry_entry_to_re_check()
    {
        var program = new ScanItem
        {
            Category = ScanCategory.UnusedPrograms, Kind = ItemKind.Program, Path = @"Q:\Program Files\X",
            Reason = "test", Risk = RiskLevel.Review, UninstallCommand = "\"Q:\\x.exe\"",
            RegistryKeyPath = @"HKCU\Software\DiskSweepTests\DoesNotExist",
        };
        var (process, error) = new UninstallLauncher(TestLayouts.Guard(), Logging.NullActionLog.Instance).Launch(program);
        Assert.Null(process);
        Assert.NotNull(error);
    }
}
