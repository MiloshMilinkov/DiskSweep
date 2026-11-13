using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiskSweep.Core.IO;
using DiskSweep.Core.Models;
using DiskSweep.Core.Safety;
using DiskSweep.Core.Scanning;
using DiskSweep.Core.SystemInfo;

namespace DiskSweep.App.ViewModels;

public enum ScreenState { Idle, Scanning, Results }

public sealed partial class MainViewModel : ObservableObject
{
    private readonly ScanEngine _engine = new();
    private readonly DispatcherTimer _progressTimer;
    private ScanStatus? _status;
    private CancellationTokenSource? _cts;
    private Stopwatch _clock = new();
    private List<ScanItemViewModel> _allItems = [];
    private static readonly ScanOptions Defaults = new();

    public MainViewModel()
    {
        IsAdmin = SystemSnapshot.IsRunningAsAdministrator();
        Categories = new ObservableCollection<CategoryOptionViewModel>(
            ScanCategoryInfo.Selectable.Select(c => new CategoryOptionViewModel(c)));
        foreach (var c in Categories) c.PropertyChanged += (_, _) => ScanCommand.NotifyCanExecuteChanged();

        _progressTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
        _progressTimer.Tick += (_, _) => PullProgress();

        RefreshDrives();
    }

    // ── Drives and options ────────────────────────────────────────────────

    public ObservableCollection<DriveItemViewModel> Drives { get; } = [];
    public ObservableCollection<CategoryOptionViewModel> Categories { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    [NotifyPropertyChangedFor(nameof(ScanButtonText))]
    private DriveItemViewModel? _selectedDrive;

    [ObservableProperty] private int _unusedDays = Defaults.UnusedProgramDays;
    [ObservableProperty] private int _largeFileMb = (int)(Defaults.LargeFileBytes / (1024 * 1024));
    [ObservableProperty] private int _oldFileDays = Defaults.OldFileDays;

    public bool IsAdmin { get; }
    public string AccountText => IsAdmin ? "Administrator" : "Standard user";
    public string ScanButtonText => SelectedDrive is null ? "Scan" : $"Scan {SelectedDrive.Letter}";

    [RelayCommand]
    private void RefreshDrives()
    {
        string? previous = SelectedDrive?.Drive.RootPath;
        Drives.Clear();
        foreach (var d in DriveCatalog.GetDrives()) Drives.Add(new DriveItemViewModel(d));
        SelectedDrive = Drives.FirstOrDefault(d => d.Drive.RootPath == previous)
                        ?? Drives.FirstOrDefault(d => d.Drive.IsSystemDrive)
                        ?? Drives.FirstOrDefault();
    }

    // ── Screen state ──────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(IsScanning), nameof(HasResults), nameof(CanEditOptions))]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    private ScreenState _state = ScreenState.Idle;

    public bool IsIdle => State == ScreenState.Idle;
    public bool IsScanning => State == ScreenState.Scanning;
    public bool HasResults => State == ScreenState.Results;
    public bool CanEditOptions => State != ScreenState.Scanning;

    // ── Scanning ──────────────────────────────────────────────────────────

    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string _phaseText = "";
    [ObservableProperty] private string _currentPath = "";
    [ObservableProperty] private string _foundText = "";
    [ObservableProperty] private string _elapsedText = "";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isCancelling;

    private bool CanScan() => State != ScreenState.Scanning && SelectedDrive is not null && Categories.Any(c => c.IsChecked);

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        var drive = SelectedDrive!.Drive;
        var categories = Categories.Where(c => c.IsChecked).Select(c => c.Category).ToHashSet();

        var options = new ScanOptions
        {
            UnusedProgramDays = Math.Clamp(UnusedDays, 7, 3650),
            LargeFileBytes = Math.Clamp(LargeFileMb, 10, 1_000_000) * 1024L * 1024L,
            OldFileDays = Math.Clamp(OldFileDays, 7, 3650),
        };

        _status = new ScanStatus();
        _cts = new CancellationTokenSource();
        _clock = Stopwatch.StartNew();
        IsCancelling = false;
        State = ScreenState.Scanning;
        PullProgress();
        _progressTimer.Start();

        try
        {
            var result = await _engine.RunAsync(new ScanRequest(drive, categories, options), _status, App.Log, _cts.Token);
            await ShowResultsAsync(result);
        }
        finally
        {
            // If showing the results failed, don't leave the window stuck on the progress screen.
            if (State == ScreenState.Scanning) State = ScreenState.Idle;
            _progressTimer.Stop();
            _cts.Dispose();
            _cts = null;
        }
    }

    private bool CanCancel() => !IsCancelling;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        if (_cts is null) return;
        IsCancelling = true;
        PhaseText = "Stopping…";
        _cts.Cancel();
    }

    private void PullProgress()
    {
        if (_status is null) return;
        ProgressPercent = _status.Fraction * 100;
        if (!IsCancelling) PhaseText = _status.Phase;
        CurrentPath = _status.CurrentPath;
        FoundText = $"{_status.ItemsFound:N0} found, {_status.EntriesScanned:N0} checked";
        ElapsedText = _clock.Elapsed.ToString(@"m\:ss");
    }

    // ── Results ───────────────────────────────────────────────────────────

    public ObservableCollection<TabViewModel> Tabs { get; } = [];
    public ObservableCollection<string> Notes { get; } = [];

    [ObservableProperty] private TabViewModel? _selectedTab;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedItem))]
    [NotifyCanExecuteChangedFor(nameof(ShowInExplorerCommand))]
    private ScanItemViewModel? _selectedItem;

    public bool HasSelectedItem => SelectedItem is not null;

    [ObservableProperty] private string _summaryText = "";
    [ObservableProperty] private string _selectionText = "";
    [ObservableProperty] private IReadOnlyList<RiskBucketViewModel> _riskBuckets = RiskBucketViewModel.Build([]);
    [ObservableProperty] private string? _logFilePath;

    private async Task ShowResultsAsync(ScanResult result)
    {
        foreach (var vm in _allItems) vm.SelectionChanged -= OnItemSelectionChanged;
        Tabs.Clear();
        Notes.Clear();
        SelectedItem = null;

        // Each row's safety check touches the disk, so build the rows off the UI thread.
        var guard = result.Guard;
        _allItems = guard is null
            ? []
            : await Task.Run(() => result.Items.Select(i => new ScanItemViewModel(new SelectableItem(i, guard))).ToList());
        foreach (var vm in _allItems) vm.SelectionChanged += OnItemSelectionChanged;

        foreach (var category in result.Categories)
        {
            var items = _allItems.Where(i => i.Item.Category == category)
                .OrderBy(i => i.Risk).ThenByDescending(i => i.Bytes);
            Tabs.Add(new ResultTabViewModel(category, items));
        }
        Tabs.Add(new SkippedTabViewModel(result.Skipped, result.SkippedTotal));
        SelectedTab = Tabs.FirstOrDefault();

        foreach (string note in result.Notes) Notes.Add(note);
        LogFilePath = result.LogFilePath;

        long total = _allItems.Sum(i => i.Bytes);
        SummaryText = $"{_allItems.Count:N0} items on {result.Request.Drive.Letter}, {PathUtil.FormatBytes(total)} in files and folders. " +
                      $"Scanned in {result.Duration:m\\:ss}{(result.WasCancelled ? " (cancelled)" : "")}.";
        RiskBuckets = RiskBucketViewModel.Build(_allItems.Where(i => !i.Item.IsReportOnly).ToList());

        UpdateSelectionText();
        State = ScreenState.Results;
    }

    private void OnItemSelectionChanged(object? sender, EventArgs e) => UpdateSelectionText();

    private void UpdateSelectionText()
    {
        var selected = _allItems.Where(i => i.IsSelected).ToList();
        long bytes = selected.Sum(i => i.Bytes);
        SelectionText = selected.Count == 0
            ? "Nothing selected"
            : $"{selected.Count:N0} selected, {PathUtil.FormatBytes(bytes)}";
    }

    [RelayCommand]
    private void NewScan()
    {
        State = ScreenState.Idle;
        RefreshDrives();
    }

    // ── Other actions ─────────────────────────────────────────────────────

    [RelayCommand]
    private void RestartAsAdministrator()
    {
        string? exe = Environment.ProcessPath;
        if (exe is null) return;
        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" });
            App.Log.Info("Restarting as administrator.");
            Application.Current.Shutdown();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // The user said no to the Windows prompt; keep running as is.
        }
    }

    [RelayCommand]
    private void OpenLog()
    {
        string? path = LogFilePath ?? App.Log.FilePath;
        if (path is null) return;
        App.Log.Flush();
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private bool CanShowInExplorer() => SelectedItem is { IsFileSystemItem: true };

    [RelayCommand(CanExecute = nameof(CanShowInExplorer))]
    private void ShowInExplorer()
    {
        if (SelectedItem is null) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{SelectedItem.Path}\"") { UseShellExecute = true });
    }
}
