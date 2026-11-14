using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;

namespace DiskSweep.App;

/// <summary>An ObservableCollection that can swap its whole contents with one change notification.</summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public BulkObservableCollection() { }

    public BulkObservableCollection(IEnumerable<T> items) : base(items) { }

    /// <summary>
    /// Replaces every item and raises a single Reset. The collection (and so the grid's sort order) stays the same object,
    /// so removing thousands of rows after a cleanup doesn't fire thousands of events.
    /// </summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        var list = items.ToList();
        CheckReentrancy();
        Items.Clear();
        foreach (var item in list) Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

/// <summary>Opens Explorer. Read-only: nothing on disk changes.</summary>
internal static class Explorer
{
    public static void Select(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });

    public static void Open(string folder) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
}
