using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiskSweep.Core.IO;
using DiskSweep.Core.Models;
using DiskSweep.Core.Safety;

namespace DiskSweep.App.ViewModels;

public sealed partial class DriveItemViewModel : ObservableObject
{
    public DriveSummary Drive { get; }

    public DriveItemViewModel(DriveSummary drive) => Drive = drive;

    public string Letter => Drive.Letter;
    public string Label => Drive.IsSystemDrive ? $"{Drive.Label} (Windows)" : Drive.Label;
    public string Details => $"{Drive.FileSystem}, {(Drive.DriveType == System.IO.DriveType.Removable ? "removable" : "fixed")}";
    public string SpaceText => $"{PathUtil.FormatBytes(Drive.FreeBytes)} free of {PathUtil.FormatBytes(Drive.TotalBytes)}";
    public double UsedPercent => Drive.TotalBytes == 0 ? 0 : 100.0 * Drive.UsedBytes / Drive.TotalBytes;
}

public sealed partial class CategoryOptionViewModel : ObservableObject
{
    public ScanCategory Category { get; }
    public string Title => ScanCategoryInfo.Title(Category);
    public string Description => ScanCategoryInfo.Description(Category);

    [ObservableProperty] private bool _isChecked = true;

    public CategoryOptionViewModel(ScanCategory category) => Category = category;
}

public sealed class ScanItemViewModel : ObservableObject
{
    private readonly SelectableItem _selectable;

    public event EventHandler? SelectionChanged;

    public ScanItemViewModel(SelectableItem selectable) => _selectable = selectable;

    public ScanItem Item => _selectable.Item;

    public bool IsSelected
    {
        get => _selectable.IsSelected;
        set
        {
            bool before = _selectable.IsSelected, couldSelect = CanSelect;
            _selectable.TrySetSelected(value); // re-runs the safety checks; may refuse
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanSelect));
            OnPropertyChanged(nameof(SelectionNote));
            // A refused tick can make the row unselectable, which changes the "Tick all" counts too.
            if (before != _selectable.IsSelected || couldSelect != CanSelect) SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool CanSelect => _selectable.Decision.CanSelect;

    public string SelectionNote => _selectable.Decision.CanSelect
        ? (_selectable.Decision.Preselect ? "Safe: ticked automatically." : "Tick it yourself after checking.")
        : $"Can't be selected: {_selectable.Decision.BlockedReason}";

    public string Name => Item.DisplayName ?? System.IO.Path.GetFileName(Item.Path);
    public string Path => Item.Path;
    public string Location => Item.Kind is ItemKind.RegistryKey ? Item.Path : System.IO.Path.GetDirectoryName(Item.Path) ?? Item.Path;
    public RiskLevel Risk => Item.Risk;
    public string RiskText => Item.IsReportOnly ? "Report only" : Item.Risk.ToString();
    public string Reason => Item.Reason;
    public long SizeSort => Item.SizeBytes ?? -1;
    /// <summary>Size for totals (0 when the item has no size).</summary>
    public long Bytes => Item.SizeBytes ?? 0;
    public string SizeText => Item.SizeBytes is { } s ? PathUtil.FormatBytes(s) : "";
    public DateTime ModifiedSort => Item.LastModifiedUtc ?? DateTime.MinValue;
    public string ModifiedText => Item.LastModifiedUtc?.ToLocalTime().ToString("d") ?? "";
    public DateTime LastUsedSort => Item.LastUsedUtc ?? DateTime.MinValue;
    public string LastUsedText => Item.LastUsedUtc?.ToLocalTime().ToString("d") ?? "Never recorded";
    public string LastUsedSource => Item.LastUsedSource ?? "";
    public string InstalledText => Item.InstalledUtc?.ToLocalTime().ToString("d") ?? "";
    public int ConfidenceSort => Item.Confidence ?? -1;
    public string ConfidenceText => Item.Confidence is { } c ? $"{c}%" : "";
    public IReadOnlyList<string> Evidence => Item.Evidence;
    public string? PublisherText => Item.Publisher is { } p ? $"Publisher: {p}" : null;
    public string? UninstallCommand => Item.QuietUninstallCommand ?? Item.UninstallCommand;
    public bool IsFileSystemItem => Item.Kind is ItemKind.File or ItemKind.Folder;
}

/// <summary>One segment of the risk-mix bar and its legend entry.</summary>
public sealed record RiskBucketViewModel(RiskLevel Risk, string Legend, System.Windows.GridLength Width)
{
    /// <summary>Splits by size; when nothing has a size (only empty folders), by item count instead.</summary>
    public static IReadOnlyList<RiskBucketViewModel> Build(IReadOnlyList<ScanItemViewModel> items)
    {
        var levels = Enum.GetValues<RiskLevel>();
        var bytes = levels.ToDictionary(r => r, r => items.Where(i => i.Risk == r).Sum(i => i.Bytes));
        var counts = levels.ToDictionary(r => r, r => items.Count(i => i.Risk == r));
        bool bySize = bytes.Values.Sum() > 0;
        double total = Math.Max(1, bySize ? bytes.Values.Sum() : counts.Values.Sum());

        return levels.Select(r => new RiskBucketViewModel(
                r,
                $"{r}: {PathUtil.FormatBytes(bytes[r])} in {counts[r]:N0} items",
                new System.Windows.GridLength((bySize ? bytes[r] : counts[r]) / total, System.Windows.GridUnitType.Star)))
            .ToList();
    }
}

/// <summary>A results tab's "Tick all" box for one risk level: ticked when all its rows are, part-ticked when some are.</summary>
public sealed class RiskTickViewModel : ObservableObject
{
    private readonly Action<RiskLevel, bool> _tickAll;
    private int _ticked;

    public RiskTickViewModel(RiskLevel risk, Action<RiskLevel, bool> tickAll)
    {
        Risk = risk;
        _tickAll = tickAll;
    }

    public RiskLevel Risk { get; }
    /// <summary>Read by the RiskPill template.</summary>
    public string RiskText => Risk.ToString();
    public string AutomationName => $"Tick all {Risk} items";
    /// <summary>Rows of this level that can be ticked.</summary>
    public int Count { get; private set; }
    public string CountText => Count == 1 ? "1 item" : $"{Count:N0} items";

    public bool? IsChecked
    {
        get => _ticked == 0 ? false : _ticked == Count ? true : null;
        // Clicking a part-ticked box arrives as false; it ticks the rest instead.
        set => _tickAll(Risk, value == true || IsChecked is null);
    }

    public void Update(int count, int ticked)
    {
        Count = count;
        _ticked = ticked;
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(IsChecked));
    }
}

public abstract class TabViewModel : ObservableObject
{
    public abstract string Header { get; }
}

public sealed partial class ResultTabViewModel : TabViewModel
{
    /// <summary>Runs a change to many rows' ticks with one totals refresh at the end.</summary>
    private readonly Action<Action> _batch;

    public ScanCategory Category { get; }
    public BulkObservableCollection<ScanItemViewModel> Items { get; }

    public ResultTabViewModel(ScanCategory category, IEnumerable<ScanItemViewModel> items, Action<Action> batch)
    {
        Category = category;
        Items = new BulkObservableCollection<ScanItemViewModel>(items);
        _batch = batch;
        TickLevels = Enum.GetValues<RiskLevel>().Where(SelectionPolicy.AllowsBulkTick)
            .Select(r => new RiskTickViewModel(r, TickLevel)).ToList();
        RefreshTicks();
    }

    // ── Ticking a whole risk level in this tab (never Risky: those are ticked one at a time) ──

    /// <summary>One "Tick all" box per level the selection policy lets the user tick in bulk.</summary>
    public IReadOnlyList<RiskTickViewModel> TickLevels { get; }
    public bool HasBulkTick => TickLevels.Any(l => l.Count > 0);
    /// <summary>Some rows can be ticked, so the bar (with "Untick all") is shown.</summary>
    public bool HasTickable => Items.Any(i => i.CanSelect);
    /// <summary>Some tickable rows are at a level that's only ticked one at a time (Risky).</summary>
    public bool HasOneAtATime => Items.Any(i => i.CanSelect && !SelectionPolicy.AllowsBulkTick(i.Risk));

    private IEnumerable<ScanItemViewModel> Tickable(RiskLevel risk) => Items.Where(i => i.Risk == risk && i.CanSelect);

    private void TickLevel(RiskLevel risk, bool tick)
    {
        var rows = Tickable(risk).Where(i => i.IsSelected != tick).ToList();
        _batch(() =>
        {
            foreach (var row in rows) row.IsSelected = tick; // each tick re-runs the safety checks
        });
    }

    [RelayCommand]
    private void UntickAll()
    {
        var rows = Items.Where(i => i.IsSelected).ToList();
        _batch(() =>
        {
            foreach (var row in rows) row.IsSelected = false;
        });
    }

    /// <summary>Recounts the level boxes after any tick changes.</summary>
    public void RefreshTicks()
    {
        foreach (var level in TickLevels)
        {
            var rows = Tickable(level.Risk).ToList();
            level.Update(rows.Count, rows.Count(i => i.IsSelected));
        }
        OnPropertyChanged(nameof(HasBulkTick));
        OnPropertyChanged(nameof(HasTickable));
        OnPropertyChanged(nameof(HasOneAtATime));
    }

    public override string Header => $"{ScanCategoryInfo.Title(Category)}  {Items.Count:N0}";
    public bool IsEmpty => Items.Count == 0;

    /// <summary>Drops rows that were cleaned up.</summary>
    public void Remove(IReadOnlySet<ScanItemViewModel> gone)
    {
        if (!Items.Any(gone.Contains)) return;
        Items.ReplaceAll(Items.Where(i => !gone.Contains(i)));
        OnPropertyChanged(nameof(Header));
        OnPropertyChanged(nameof(IsEmpty));
    }
    public bool ShowSize => Category is not (ScanCategory.OrphanedRegistry or ScanCategory.UnusedPrograms or ScanCategory.EmptyFolders);
    public bool ShowConfidence => Category == ScanCategory.Leftovers;
    public bool ShowLastUsed => Category == ScanCategory.UnusedPrograms;
    public bool ShowModified => Category != ScanCategory.UnusedPrograms;

    public string EmptyText => Category switch
    {
        ScanCategory.EmptyFolders => $"No empty folders older than {new ScanOptions().EmptyFolderMinAgeDays} days on this drive.",
        ScanCategory.Leftovers => "No leftover folders found. Every folder in Program Files, ProgramData and AppData belongs to something installed.",
        ScanCategory.OrphanedRegistry => "No orphaned registry entries found.",
        ScanCategory.UnusedPrograms => "Every program on this drive has been used recently, or is a shared component.",
        ScanCategory.TempAndOldFiles => "No old temp files, large files or old downloads found.",
        _ => "Nothing found.",
    };
}

public sealed class SkippedTabViewModel : TabViewModel
{
    public BulkObservableCollection<SkippedEntry> Items { get; }
    public int Total { get; private set; }

    public SkippedTabViewModel(IReadOnlyList<SkippedEntry> items, int total)
    {
        Items = new(items);
        Total = total;
    }

    /// <summary>Adds items a cleanup left alone, at the top so they're seen first.</summary>
    public void Prepend(IReadOnlyList<SkippedEntry> entries)
    {
        if (entries.Count == 0) return;
        Items.ReplaceAll(entries.Concat(Items));
        Total += entries.Count;
        OnPropertyChanged(nameof(Header));
        OnPropertyChanged(nameof(Summary));
    }

    public override string Header => $"Skipped  {Total:N0}";
    public string Summary => Total > Items.Count
        ? $"Showing the first {Items.Count:N0} of {Total:N0} skipped paths. The log file has all of them."
        : "Paths DiskSweep didn't look into or won't offer, and why. Nothing here was changed.";
}
