using CommunityToolkit.Mvvm.ComponentModel;
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
            bool before = _selectable.IsSelected;
            _selectable.TrySetSelected(value); // re-runs the safety checks; may refuse
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanSelect));
            OnPropertyChanged(nameof(SelectionNote));
            if (before != _selectable.IsSelected) SelectionChanged?.Invoke(this, EventArgs.Empty);
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

public abstract class TabViewModel : ObservableObject
{
    public abstract string Header { get; }
}

public sealed class ResultTabViewModel : TabViewModel
{
    public ScanCategory Category { get; }
    public BulkObservableCollection<ScanItemViewModel> Items { get; }

    public ResultTabViewModel(ScanCategory category, IEnumerable<ScanItemViewModel> items)
    {
        Category = category;
        Items = new BulkObservableCollection<ScanItemViewModel>(items);
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
