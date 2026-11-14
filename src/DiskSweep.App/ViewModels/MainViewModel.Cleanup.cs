using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using DiskSweep.App.Views;
using DiskSweep.Core.Cleanup;
using DiskSweep.Core.IO;
using DiskSweep.Core.Models;
using DiskSweep.Core.Scanning;

namespace DiskSweep.App.ViewModels;

/// <summary>Phase 2: moving ticked items to the Recycle Bin or quarantine, and launching uninstallers.</summary>
public sealed partial class MainViewModel
{
    private bool CanClean() => State == ScreenState.Results && _scan?.Guard is not null && _allItems.Any(i => i.IsSelected);

    [RelayCommand(CanExecute = nameof(CanClean))]
    private async Task CleanAsync()
    {
        var selected = _allItems.Where(i => i.IsSelected).ToList();
        var files = selected.Where(i => i.IsFileSystemItem).ToList();
        var programs = selected.Where(i => i.Item.Kind == ItemKind.Program).ToList();
        if (files.Count + programs.Count == 0) return;

        var choice = new CleanupDialogViewModel(files.Count, files.Sum(f => f.Bytes), programs.Select(p => p.Name).ToList(), IsAdmin);
        if (new CleanupDialog { DataContext = choice, Owner = Application.Current.MainWindow }.ShowDialog() != true) return;

        _status = new ScanStatus();
        _cts = new CancellationTokenSource();
        _cleanTotal = files.Count + programs.Count;
        _clock = Stopwatch.StartNew();
        IsCancelling = false;
        State = ScreenState.Cleaning;
        PullProgress();
        _progressTimer.Start();
        var notes = new List<string>();

        try
        {
            if (choice.CreateRestorePoint && !await TryRestorePointAsync(notes)) return;

            var outcomes = new List<CleanupOutcome>();
            if (files.Count > 0)
            {
                var destination = choice.UseQuarantine ? CleanupDestination.Quarantine : CleanupDestination.RecycleBin;
                var report = await MoveFilesAsync(files, destination);
                outcomes.AddRange(report.Outcomes);
                notes.Insert(0, $"Moved {report.Moved.Count():N0} item(s), {PathUtil.FormatBytes(report.MovedBytes)}, " +
                                (destination == CleanupDestination.Quarantine
                                    ? "to quarantine (open Quarantine to restore them)."
                                    : "to the Recycle Bin.") +
                                (report.WasCancelled ? " Stopped before the end." : ""));
            }

            var uninstalls = await RunUninstallersAsync(programs);
            if (uninstalls.Count > 0)
                notes.Add($"Uninstalled {uninstalls.Count(u => u.Status == CleanupStatus.Moved)} of {uninstalls.Count} program(s).");
            outcomes.AddRange(uninstalls);

            ApplyCleanupResults(selected, outcomes, notes);
        }
        finally
        {
            _progressTimer.Stop();
            State = ScreenState.Results;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>Returns false when no restore point was made and the user chose not to continue.</summary>
    private async Task<bool> TryRestorePointAsync(List<string> notes)
    {
        _status!.Show("Creating a System Restore point…", "", 0);
        var point = await Task.Run(() => RestorePoint.TryCreate("Before DiskSweep cleanup", App.Log));
        if (point.Created)
        {
            notes.Add(point.Message);
            return true;
        }

        var answer = MessageBox.Show(Application.Current.MainWindow,
            $"No restore point was created.\n\n{point.Message}\n\nContinue cleaning without one?",
            "DiskSweep", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            App.Log.Info("Cleanup cancelled: no restore point, and the user chose not to continue.");
            return false;
        }
        notes.Add($"No restore point: {point.Message}");
        return true;
    }

    private Task<CleanupReport> MoveFilesAsync(List<ScanItemViewModel> files, CleanupDestination destination)
    {
        var service = new CleanupService(_scan!.Guard!, App.Log);
        var items = files.Select(f => f.Item).ToList();
        var status = _status;
        var token = _cts!.Token;
        // One single-threaded-apartment thread for the whole run (the shell's Recycle Bin call needs one).
        return Task.Run(() => StaThread.Run(() => service.Run(items, destination, status, token)));
    }

    private async Task<List<CleanupOutcome>> RunUninstallersAsync(List<ScanItemViewModel> programs)
    {
        var outcomes = new List<CleanupOutcome>();
        var launcher = new UninstallLauncher(_scan!.Guard!, App.Log);
        var token = _cts!.Token;
        for (int i = 0; i < programs.Count; i++)
        {
            var program = programs[i].Item;
            if (token.IsCancellationRequested)
            {
                outcomes.Add(new CleanupOutcome(program, CleanupStatus.Skipped, "Not started: cleanup was stopped."));
                continue;
            }
            _status!.Show($"Uninstalling {programs[i].Name} ({i + 1} of {programs.Count})",
                "Its own uninstaller is open. Follow its steps, then close it to continue.", (double)i / programs.Count);
            outcomes.Add(await launcher.RunAsync(program, token));
            _status.AddScanned();
        }
        return outcomes;
    }

    /// <summary>Drops what was cleaned from the results; unticks and lists everything that wasn't.</summary>
    private void ApplyCleanupResults(List<ScanItemViewModel> selected, List<CleanupOutcome> outcomes, List<string> notes)
    {
        var byItem = selected.ToDictionary(vm => vm.Item);
        var gone = new HashSet<ScanItemViewModel>();
        var notDone = new List<SkippedEntry>();

        _bulkUpdating = true;
        try
        {
            foreach (var outcome in outcomes)
            {
                var vm = byItem[outcome.Item];
                if (outcome.Status == CleanupStatus.Moved)
                {
                    gone.Add(vm);
                    continue;
                }
                vm.IsSelected = false; // look at it again before retrying
                string what = outcome.Item.Kind == ItemKind.Program ? "uninstalled" : "cleaned";
                notDone.Add(new SkippedEntry(outcome.Item.Kind == ItemKind.Program ? vm.Name : outcome.Item.Path, $"Not {what}: {outcome.Detail}"));
            }
        }
        finally
        {
            _bulkUpdating = false;
        }

        if (notDone.Count > 0) notes.Add($"{notDone.Count:N0} item(s) weren't cleaned. The Skipped tab says why.");

        foreach (var vm in gone) vm.SelectionChanged -= OnItemSelectionChanged;
        _allItems = _allItems.Where(i => !gone.Contains(i)).ToList();
        foreach (var tab in Tabs.OfType<ResultTabViewModel>()) tab.Remove(gone);
        Tabs.OfType<SkippedTabViewModel>().FirstOrDefault()?.Prepend(notDone);
        if (SelectedItem is not null && gone.Contains(SelectedItem)) SelectedItem = null;

        Notes.Clear();
        foreach (string note in notes) Notes.Add(note);
        RefreshTotals();
    }

    private bool CanOpenQuarantine() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanOpenQuarantine))]
    private void OpenQuarantine() =>
        new QuarantineWindow { DataContext = new QuarantineViewModel(), Owner = Application.Current.MainWindow }.ShowDialog();
}
