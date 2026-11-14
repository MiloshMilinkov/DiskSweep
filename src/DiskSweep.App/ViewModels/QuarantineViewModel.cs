using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiskSweep.Core;
using DiskSweep.Core.Cleanup;
using DiskSweep.Core.IO;
using DiskSweep.Core.SystemInfo;

namespace DiskSweep.App.ViewModels;

public sealed partial class QuarantineEntryViewModel : ObservableObject
{
    public QuarantineEntryViewModel(QuarantineBatch batch, QuarantineEntry entry)
    {
        Batch = batch;
        Entry = entry;
        CanAct = QuarantineRestorer.IsInQuarantine(batch, entry);
    }

    public QuarantineBatch Batch { get; }
    public QuarantineEntry Entry { get; }

    [ObservableProperty] private bool _isSelected;

    public bool CanAct { get; }
    public string Name => Path.GetFileName(Entry.OriginalPath);
    public string Folder => Path.GetDirectoryName(Entry.OriginalPath) ?? Entry.OriginalPath;
    public long SizeSort => Entry.SizeBytes;
    public string SizeText => PathUtil.FormatBytes(Entry.SizeBytes);
    public DateTime WhenSort => Entry.QuarantinedUtc;
    public string WhenText => Entry.QuarantinedUtc.ToLocalTime().ToString("g");
    public string StateText => Entry.State switch
    {
        QuarantineState.Quarantined => "In quarantine",
        QuarantineState.Pending => CanAct ? "In quarantine (move was interrupted)" : "Interrupted before moving: still in its original place",
        _ => Entry.State.ToString(),
    };
}

/// <summary>The Restore view: everything DiskSweep has put in quarantine, on every drive.</summary>
public sealed partial class QuarantineViewModel : ObservableObject
{
    private readonly QuarantineRestorer _restorer = new(App.Log);

    public BulkObservableCollection<QuarantineEntryViewModel> Entries { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenFolderCommand))]
    private QuarantineEntryViewModel? _selectedEntry;

    [ObservableProperty] private string _statusText = "";

    public bool IsEmpty => Entries.Count == 0;

    public QuarantineViewModel() => Load();

    private void Load()
    {
        var roots = DriveCatalog.GetDrives().Select(d => AppPaths.QuarantineRootFor(d.RootPath));
        Entries.ReplaceAll(QuarantineStore.LoadAll(roots).SelectMany(batch => batch.Entries
            .Where(e => e.State is QuarantineState.Quarantined or QuarantineState.Pending)
            .Select(e => new QuarantineEntryViewModel(batch, e))));
        OnPropertyChanged(nameof(IsEmpty));
        StatusText = Entries.Count == 0
            ? "Nothing is in quarantine."
            : $"{Entries.Count:N0} item(s), {PathUtil.FormatBytes(Entries.Sum(e => e.SizeSort))}.";
    }

    [RelayCommand]
    private Task RestoreSelectedAsync() =>
        RunOnSelectedAsync("Restored", vm => _restorer.Restore(vm.Batch, vm.Entry), confirm: null);

    [RelayCommand]
    private Task RecycleSelectedAsync() =>
        RunOnSelectedAsync("Moved to the Recycle Bin", vm => _restorer.SendToRecycleBin(vm.Batch, vm.Entry),
            confirm: n => $"Move {n} item(s) from quarantine to the Recycle Bin?\n\nItems the Recycle Bin can't take stay in quarantine.");

    private async Task RunOnSelectedAsync(string doneVerb, Func<QuarantineEntryViewModel, string?> action, Func<int, string>? confirm)
    {
        var chosen = Entries.Where(e => e.IsSelected && e.CanAct).ToList();
        if (chosen.Count == 0)
        {
            StatusText = "Tick the items first.";
            return;
        }
        if (confirm is not null &&
            MessageBox.Show(confirm(chosen.Count), "DiskSweep", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        var errors = await Task.Run(() => chosen
            .Select(vm => action(vm) is { } error ? $"{vm.Name}: {error}" : null)
            .OfType<string>()
            .ToList());

        int done = chosen.Count - errors.Count;
        Load();
        StatusText = errors.Count == 0
            ? $"{doneVerb} {done} item(s)."
            : $"{doneVerb} {done} item(s). {errors.Count} couldn't be done:\n" + string.Join("\n", errors.Take(8)) +
              (errors.Count > 8 ? $"\n…and {errors.Count - 8} more (see the log)." : "");
    }

    private bool CanOpenFolder() => SelectedEntry is not null;

    [RelayCommand(CanExecute = nameof(CanOpenFolder))]
    private void OpenFolder()
    {
        if (SelectedEntry is null) return;
        string stored = SelectedEntry.Batch.PathOf(SelectedEntry.Entry);
        if (FastDirectory.Exists(stored) == true) Explorer.Select(stored);
        else Explorer.Open(SelectedEntry.Batch.Folder);
    }
}
