using System.ComponentModel;
using System.Diagnostics;
using DiskSweep.Core.Logging;
using DiskSweep.Core.Models;
using DiskSweep.Core.Safety;
using DiskSweep.Core.SystemInfo;

namespace DiskSweep.Core.Cleanup;

/// <summary>
/// Removes programs only by starting the program's own interactive uninstaller, exactly as registered.
/// The command is read from the registry again right before launch and must match what the scan saw.
/// </summary>
public sealed class UninstallLauncher
{
    /// <summary>Windows' code for "the user declined the permission prompt".</summary>
    public const int ErrorCancelled = 1223;

    private readonly SafetyGuard _guard;
    private readonly IActionLog _log;

    public UninstallLauncher(SafetyGuard guard, IActionLog log)
    {
        _guard = guard;
        _log = log;
    }

    /// <summary>
    /// Runs the program's uninstaller and waits for it to close, then checks the registry. The outcome is Moved when the
    /// program is no longer listed as installed. Cancelling stops the wait, not the uninstaller.
    /// </summary>
    public async Task<CleanupOutcome> RunAsync(ScanItem program, CancellationToken token)
    {
        var (process, error) = Launch(program);
        if (error is not null) return new CleanupOutcome(program, CleanupStatus.Skipped, error);

        if (process is null)
        {
            // Windows started it without giving DiskSweep a handle to wait on, so the result isn't known yet.
            if (!IsStillInstalled(program)) return new CleanupOutcome(program, CleanupStatus.Moved, "Uninstalled by its own uninstaller.");
            return new CleanupOutcome(program, CleanupStatus.Skipped,
                "Its uninstaller was started, but DiskSweep can't tell when it finishes. Scan again when it's done.");
        }

        using (process)
        {
            try { await process.WaitForExitAsync(token); }
            catch (OperationCanceledException) { /* stop waiting; the uninstaller keeps running on its own */ }
        }

        bool removed = !IsStillInstalled(program);
        var outcome = removed
            ? new CleanupOutcome(program, CleanupStatus.Moved, "Uninstalled by its own uninstaller.")
            : new CleanupOutcome(program, CleanupStatus.Failed,
                "Still installed: its uninstaller was cancelled, failed or is still running. Scan again later to check.");
        _log.Info($"Uninstall result for \"{program.DisplayName}\": {outcome.Detail}");
        return outcome;
    }

    /// <summary>Starts the uninstaller. Returns the process (null if Windows doesn't hand one back) or an error message.</summary>
    public (Process? Process, string? Error) Launch(ScanItem program)
    {
        if (program.Kind != ItemKind.Program) return (null, "Not a program.");

        // Same rule as ticking: only an interactive uninstaller registered by the program itself.
        var decision = SelectionPolicy.Evaluate(program, _guard);
        if (!decision.CanSelect) return (null, decision.BlockedReason);
        if (program.RegistryKeyPath is null) return (null, "The program's registry entry isn't known. Scan again.");

        string? current = ReadUninstallString(program.RegistryKeyPath);
        if (current is null) return (null, "It's no longer listed as installed.");
        if (!string.Equals(current, program.UninstallCommand, StringComparison.Ordinal))
            return (null, "Its uninstall command changed since the scan. Scan again.");

        if (!TrySplit(current, out string file, out string arguments, out string? splitError)) return (null, splitError);

        try
        {
            // Shell execute so an uninstaller that needs admin rights gets the normal Windows prompt.
            var process = Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = true });
            _log.Info($"Started the uninstaller for \"{program.DisplayName}\": {current}");
            return (process, null);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            _log.Info($"Uninstaller for \"{program.DisplayName}\" not started: the Windows prompt was declined.");
            return (null, "The Windows permission prompt was declined.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            _log.Error($"Uninstaller for \"{program.DisplayName}\" couldn't start", ex);
            return (null, $"The uninstaller couldn't start: {ex.Message}");
        }
    }

    /// <summary>True while the program's uninstall entry is still in the registry.</summary>
    public static bool IsStillInstalled(ScanItem program) =>
        program.RegistryKeyPath is not null && ReadUninstallString(program.RegistryKeyPath) is not null;

    private static string? ReadUninstallString(string keyPath)
    {
        using var key = RegistryReader.OpenDisplayPath(keyPath);
        return key is null ? null : RegistryReader.String(key, "UninstallString");
    }

    /// <summary>
    /// Splits a registered command into program and arguments. Quoted paths are taken as-is. For unquoted paths with
    /// spaces, the longest existing "*.exe" prefix wins (so a planted C:\Program.exe can't hijack it). A bare program
    /// name (msiexec.exe) is only looked up in System32, never on PATH.
    /// </summary>
    public static bool TrySplit(string command, out string file, out string arguments, out string? error)
    {
        file = "";
        arguments = "";
        error = null;
        string cmd = command.Trim();
        if (cmd.Length == 0) { error = "The uninstall command is empty."; return false; }

        if (cmd[0] == '"')
        {
            int end = cmd.IndexOf('"', 1);
            if (end < 0) { error = "The uninstall command has an unmatched quote."; return false; }
            file = Environment.ExpandEnvironmentVariables(cmd[1..end]);
            arguments = cmd[(end + 1)..].Trim();
        }
        else
        {
            string expanded = Environment.ExpandEnvironmentVariables(cmd);
            string? best = null;
            for (int i = expanded.Length; i > 0; i--)
            {
                if (i < expanded.Length && expanded[i] != ' ') continue;
                string candidate = expanded[..i];
                if (candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && Path.IsPathFullyQualified(candidate) && File.Exists(candidate))
                {
                    best = candidate;
                    break;
                }
            }

            if (best is not null)
            {
                file = best;
                arguments = expanded[best.Length..].Trim();
            }
            else
            {
                int space = expanded.IndexOf(' ');
                file = space < 0 ? expanded : expanded[..space];
                arguments = space < 0 ? "" : expanded[(space + 1)..].Trim();
            }
        }

        if (!Path.IsPathFullyQualified(file))
        {
            if (file.Contains('\\') || file.Contains('/')) { error = $"The uninstaller path isn't a full path: {file}"; return false; }
            string system = Path.Combine(Environment.SystemDirectory, file);
            if (!File.Exists(system)) { error = $"The uninstaller \"{file}\" isn't in the Windows system folder."; return false; }
            file = system;
        }
        else if (!File.Exists(file))
        {
            error = $"The uninstaller is missing: {file}";
            return false;
        }
        return true;
    }
}
