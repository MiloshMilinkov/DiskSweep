using CommunityToolkit.Mvvm.ComponentModel;
using DiskSweep.Core.IO;

namespace DiskSweep.App.ViewModels;

/// <summary>The confirmation shown before anything moves: what, how much, where to, and the restore point option.</summary>
public sealed partial class CleanupDialogViewModel : ObservableObject
{
    public CleanupDialogViewModel(int fileCount, long bytes, IReadOnlyList<string> programs, bool isAdmin)
    {
        HasFiles = fileCount > 0;
        HasPrograms = programs.Count > 0;
        CanCreateRestorePoint = isAdmin;
        _createRestorePoint = isAdmin;

        int total = fileCount + programs.Count;
        Title = total == 1 ? "Clean 1 item?" : $"Clean {total:N0} items?";
        FilesText = fileCount == 1 ? $"1 file or folder, {PathUtil.FormatBytes(bytes)}" : $"{fileCount:N0} files and folders, {PathUtil.FormatBytes(bytes)}";
        ProgramsText = programs.Count == 1 ? $"1 program: {programs[0]}" : $"{programs.Count} programs: {string.Join(", ", programs)}";
        CleanButtonText = HasFiles ? (HasPrograms ? "Move and uninstall" : "Move items") : "Start uninstallers";
    }

    public string Title { get; }
    public bool HasFiles { get; }
    public string FilesText { get; }
    public bool HasPrograms { get; }
    public string ProgramsText { get; }
    public string CleanButtonText { get; }
    public bool CanCreateRestorePoint { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UseQuarantine))]
    private bool _useRecycleBin = true;

    public bool UseQuarantine
    {
        get => !UseRecycleBin;
        set => UseRecycleBin = !value;
    }

    [ObservableProperty] private bool _createRestorePoint;
}
