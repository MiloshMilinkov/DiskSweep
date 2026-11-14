using DiskSweep.Core.Cleanup;
using DiskSweep.Core.IO;
using DiskSweep.Core.Logging;
using DiskSweep.Core.Models;
using DiskSweep.Core.Safety;

namespace DiskSweep.Core.Tests;

/// <summary>Proves cleanup only ever moves (never deletes), re-checks everything first, and that restore never overwrites.</summary>
public class CleanupTests : IDisposable
{
    private readonly TempTree _tree = new();

    public void Dispose() => _tree.Dispose();

    /// <summary>Stands in for the real Recycle Bin: moves items into a folder so tests never touch the user's Bin.</summary>
    private sealed class FakeRecycleBin(string folder) : IRecycleBin
    {
        public string? Refuse { get; init; }
        public Action? AfterRecycle { get; init; }
        public List<string> Sent { get; } = [];

        public RecycleResult Recycle(string path, long sizeBytes)
        {
            if (Refuse is not null) return new(RecycleStatus.Refused, Refuse);
            Directory.CreateDirectory(folder);
            string target = Path.Combine(folder, Guid.NewGuid().ToString("N"));
            if (Directory.Exists(path)) Directory.Move(path, target); else File.Move(path, target);
            Sent.Add(path);
            AfterRecycle?.Invoke();
            return new(RecycleStatus.Recycled, "Moved to the Recycle Bin.");
        }
    }

    private string QuarantineRoot => Path.Combine(_tree.Root, "quarantine");

    private FakeRecycleBin NewBin(string? refuse = null) => new(Path.Combine(_tree.Root, "bin")) { Refuse = refuse };

    private CleanupService Service(IRecycleBin? bin = null, params string[] exclusions) =>
        new(new SafetyGuard(TestLayouts.Synthetic(exclusions), Win32FileAttributeProbe.Instance), NullActionLog.Instance,
            new QuarantineStore(_ => QuarantineRoot), bin ?? NewBin());

    private static ScanItem FileItem(string path, ScanCategory category = ScanCategory.TempAndOldFiles, RiskLevel risk = RiskLevel.Safe)
    {
        var info = new FileInfo(path);
        return new ScanItem
        {
            Category = category, Kind = ItemKind.File, Path = path, Reason = "test", Risk = risk,
            SizeBytes = info.Length, LastModifiedUtc = info.LastWriteTimeUtc,
        };
    }

    /// <param name="baseline">Scan date to record; defaults to the folder's newest change right now, as a scanner records it.</param>
    private static ScanItem FolderItem(string path, ScanCategory category = ScanCategory.TempAndOldFiles, DateTime? baseline = null)
    {
        var stats = FolderMeasure.Measure(path, CancellationToken.None);
        return new ScanItem
        {
            Category = category, Kind = ItemKind.Folder, Path = path, Reason = "test", Risk = RiskLevel.Safe,
            SizeBytes = stats.TotalBytes, LastModifiedUtc = baseline ?? stats.NewestChangeUtc,
        };
    }

    [Fact]
    public void Quarantine_moves_files_and_folders_and_restore_puts_them_back()
    {
        string file = _tree.File(@"data\old.log", "hello");
        _tree.File(@"data\cache\a.bin", "abc");
        string folder = Path.Combine(_tree.Root, @"data\cache");

        var report = Service().Run([FileItem(file), FolderItem(folder)], CleanupDestination.Quarantine);

        Assert.All(report.Outcomes, o => Assert.Equal(CleanupStatus.Moved, o.Status));
        Assert.False(File.Exists(file));
        Assert.False(Directory.Exists(folder));

        var batch = Assert.Single(QuarantineStore.LoadAll([QuarantineRoot]));
        Assert.Equal(2, batch.Entries.Count);
        Assert.All(batch.Entries, e => Assert.Equal(QuarantineState.Quarantined, e.State));
        Assert.All(batch.Entries, e => Assert.True(Path.Exists(batch.PathOf(e))));

        var restorer = new QuarantineRestorer(NullActionLog.Instance, NewBin());
        foreach (var entry in batch.Entries) Assert.Null(restorer.Restore(batch, entry));

        Assert.Equal("hello", File.ReadAllText(file));
        Assert.True(File.Exists(Path.Combine(folder, "a.bin")));
        var reloaded = Assert.Single(QuarantineStore.LoadAll([QuarantineRoot]));
        Assert.All(reloaded.Entries, e => Assert.Equal(QuarantineState.Restored, e.State));
    }

    [Fact]
    public void Restore_never_overwrites_something_new_at_the_original_path()
    {
        string file = _tree.File(@"docs\report.txt", "old");
        Service().Run([FileItem(file)], CleanupDestination.Quarantine);
        File.WriteAllText(file, "new"); // something recreated it meanwhile

        var batch = Assert.Single(QuarantineStore.LoadAll([QuarantineRoot]));
        var restorer = new QuarantineRestorer(NullActionLog.Instance);
        Assert.Null(restorer.Restore(batch, batch.Entries.First()));

        Assert.Equal("new", File.ReadAllText(file));
        Assert.Equal("old", File.ReadAllText(Path.Combine(_tree.Root, @"docs\report (restored).txt")));
    }

    [Fact]
    public void Restore_refuses_to_go_through_a_junction()
    {
        string file = _tree.File(@"place\inner\note.txt", "x");
        Service().Run([FileItem(file)], CleanupDestination.Quarantine);

        // Replace the parent folder with a junction pointing elsewhere.
        Directory.Delete(Path.Combine(_tree.Root, @"place\inner"));
        string elsewhere = _tree.Dir("elsewhere");
        _tree.Junction(@"place\inner", elsewhere);

        var batch = Assert.Single(QuarantineStore.LoadAll([QuarantineRoot]));
        string? error = new QuarantineRestorer(NullActionLog.Instance).Restore(batch, batch.Entries.First());

        Assert.NotNull(error);
        Assert.Empty(Directory.EnumerateFileSystemEntries(elsewhere));
        Assert.Equal(QuarantineState.Quarantined, batch.Entries.First().State);
    }

    [Fact]
    public void Protected_paths_are_never_moved_even_when_ticked()
    {
        string keep = _tree.File(@"keep\important.doc", "x");
        var report = Service(null, Path.Combine(_tree.Root, "keep")).Run([FileItem(keep)], CleanupDestination.Quarantine);

        Assert.Equal(CleanupStatus.Skipped, Assert.Single(report.Outcomes).Status);
        Assert.True(File.Exists(keep));
    }

    [Fact]
    public void A_junction_is_never_moved_and_its_target_is_untouched()
    {
        string target = _tree.Dir("real");
        _tree.File(@"real\precious.txt", "x");
        string link = _tree.Junction("link", target);

        var item = new ScanItem
        {
            Category = ScanCategory.EmptyFolders, Kind = ItemKind.Folder, Path = link, Reason = "test",
            Risk = RiskLevel.Safe, LastModifiedUtc = DateTime.UtcNow.AddYears(1),
        };
        var report = Service().Run([item], CleanupDestination.RecycleBin);

        Assert.Equal(CleanupStatus.Skipped, Assert.Single(report.Outcomes).Status);
        Assert.True(Directory.Exists(link));
        Assert.True(File.Exists(Path.Combine(target, "precious.txt")));
    }

    [Fact]
    public void A_folder_that_gained_a_junction_after_the_scan_is_not_moved()
    {
        string folder = _tree.Dir("leftover");
        _tree.File(@"leftover\a.txt", "x");
        // Scanned while it had no link (baseline in the future so the "changed since scan" check doesn't trigger first).
        var item = FolderItem(folder, baseline: DateTime.UtcNow.AddYears(1));
        string target = _tree.Dir("target");
        _tree.File(@"target\keep.txt", "x");
        _tree.Junction(@"leftover\link", target);

        var bin = NewBin();
        var outcome = Assert.Single(Service(bin).Run([item], CleanupDestination.RecycleBin).Outcomes);

        Assert.Equal(CleanupStatus.Skipped, outcome.Status);
        Assert.Contains("junction", outcome.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(bin.Sent);
        Assert.True(File.Exists(Path.Combine(target, "keep.txt")));
    }

    [Fact]
    public void Items_that_changed_since_the_scan_are_skipped()
    {
        string file = _tree.File("log.txt", "one");
        TempTree.Age(file, 30);
        var item = FileItem(file);
        File.WriteAllText(file, "two, longer");

        var outcome = Assert.Single(Service().Run([item], CleanupDestination.Quarantine).Outcomes);
        Assert.Equal(CleanupStatus.Skipped, outcome.Status);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void An_empty_folder_that_is_no_longer_empty_is_skipped()
    {
        string folder = _tree.Dir("empty");
        var item = FolderItem(folder, ScanCategory.EmptyFolders, baseline: DateTime.UtcNow.AddYears(1));
        _tree.File(@"empty\new.txt", "x");

        var outcome = Assert.Single(Service().Run([item], CleanupDestination.Quarantine).Outcomes);
        Assert.Equal(CleanupStatus.Skipped, outcome.Status);
        Assert.True(File.Exists(Path.Combine(folder, "new.txt")));
    }

    [Fact]
    public void Programs_and_registry_items_are_never_moved()
    {
        var program = new ScanItem
        {
            Category = ScanCategory.UnusedPrograms, Kind = ItemKind.Program, Path = _tree.Dir("app"),
            Reason = "test", Risk = RiskLevel.Review, UninstallCommand = "x", LastModifiedUtc = DateTime.UtcNow,
        };
        var key = new ScanItem
        {
            Category = ScanCategory.OrphanedRegistry, Kind = ItemKind.RegistryKey, Path = @"HKCU\Software\Old",
            Reason = "test", Risk = RiskLevel.Review, IsReportOnly = true,
        };

        var report = Service().Run([program, key], CleanupDestination.RecycleBin);
        Assert.All(report.Outcomes, o => Assert.Equal(CleanupStatus.Skipped, o.Status));
        Assert.True(Directory.Exists(program.Path));
    }

    [Fact]
    public void Items_the_recycle_bin_cannot_take_are_skipped_not_deleted()
    {
        string file = _tree.File("big.iso", "x");
        var bin = NewBin("Bigger than the Recycle Bin's size limit.");

        var outcome = Assert.Single(Service(bin).Run([FileItem(file)], CleanupDestination.RecycleBin).Outcomes);

        Assert.Equal(CleanupStatus.Skipped, outcome.Status);
        Assert.Empty(bin.Sent);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void Items_the_recycle_bin_accepts_are_sent_there()
    {
        string file = _tree.File("old.tmp", "x");
        var bin = NewBin();

        var outcome = Assert.Single(Service(bin).Run([FileItem(file)], CleanupDestination.RecycleBin).Outcomes);

        Assert.Equal(CleanupStatus.Moved, outcome.Status);
        Assert.Equal(file, Assert.Single(bin.Sent));
    }

    [Fact]
    public void A_quarantine_folder_replaced_by_a_junction_is_never_written_through()
    {
        string elsewhere = _tree.Dir("elsewhere");
        _tree.Junction("quarantine", elsewhere); // QuarantineRoot is now a link
        string file = _tree.File("old.tmp", "x");

        var outcome = Assert.Single(Service().Run([FileItem(file)], CleanupDestination.Quarantine).Outcomes);

        Assert.Equal(CleanupStatus.Failed, outcome.Status);
        Assert.True(File.Exists(file));
        Assert.Empty(Directory.EnumerateFileSystemEntries(elsewhere));
    }

    [Fact]
    public void A_tampered_journal_cannot_point_restore_outside_the_quarantine_or_into_windows()
    {
        string file = _tree.File("old.tmp", "x");
        Service().Run([FileItem(file)], CleanupDestination.Quarantine);
        var batch = Assert.Single(QuarantineStore.LoadAll([QuarantineRoot]));
        var entry = batch.Entries.First();
        var restorer = new QuarantineRestorer(NullActionLog.Instance, NewBin());

        entry.StoredRelativePath = @"..\..\elsewhere.txt"; // escapes the batch folder
        Assert.Equal("", batch.PathOf(entry));
        Assert.NotNull(restorer.Restore(batch, entry));

        entry.StoredRelativePath = Path.Join("1", "old.tmp");
        entry.OriginalPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"System32\evil.dll");
        Assert.NotNull(restorer.Restore(batch, entry));
        Assert.True(File.Exists(batch.PathOf(entry))); // still safely in quarantine
    }

    [Fact]
    public void An_interrupted_move_that_finished_can_still_be_restored()
    {
        string file = _tree.File(@"work\old.tmp", "x");
        Service().Run([FileItem(file)], CleanupDestination.Quarantine);
        var batch = Assert.Single(QuarantineStore.LoadAll([QuarantineRoot]));
        var entry = batch.Entries.First();
        entry.SetState(QuarantineState.Pending); // as if the app died right after the move

        Assert.True(QuarantineRestorer.IsInQuarantine(batch, entry));
        Assert.Null(new QuarantineRestorer(NullActionLog.Instance, NewBin()).Restore(batch, entry));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void A_journal_line_cut_short_by_a_crash_is_ignored()
    {
        string file = _tree.File("old.tmp", "x");
        Service().Run([FileItem(file)], CleanupDestination.Quarantine);
        var batch = Assert.Single(QuarantineStore.LoadAll([QuarantineRoot]));
        File.AppendAllText(Path.Combine(batch.Folder, QuarantineBatch.JournalName), "{\"Slot\":1,\"State\":\"Rest");

        var reloaded = Assert.Single(QuarantineStore.LoadAll([QuarantineRoot]));
        Assert.Equal(QuarantineState.Quarantined, Assert.Single(reloaded.Entries).State);
    }

    [Fact]
    public void Stopping_leaves_the_remaining_items_alone()
    {
        string a = _tree.File("a.tmp", "x");
        string b = _tree.File("b.tmp", "x");
        using var cts = new CancellationTokenSource();
        var bin = new FakeRecycleBin(Path.Combine(_tree.Root, "bin")) { AfterRecycle = cts.Cancel }; // Stop pressed during the first move
        var report = Service(bin).Run([FileItem(a), FileItem(b)], CleanupDestination.RecycleBin, token: cts.Token);

        Assert.True(report.WasCancelled);
        Assert.Equal(CleanupStatus.Moved, report.Outcomes[0].Status);
        Assert.Equal(CleanupStatus.Skipped, report.Outcomes[1].Status);
        Assert.True(File.Exists(b));
    }

    [Theory]
    [InlineData(300, DriveType.Fixed, false, 1_000_000L, 0L, 0L, 10L, true)]          // path too long
    [InlineData(50, DriveType.Removable, false, 1_000_000L, 0L, 0L, 10L, true)]       // removable: no Bin
    [InlineData(50, DriveType.Fixed, true, 1_000_000L, 0L, 0L, 10L, true)]            // Bin deletes immediately
    [InlineData(50, DriveType.Fixed, false, 100L, 0L, 0L, 1_000L, true)]              // bigger than the Bin
    [InlineData(50, DriveType.Fixed, false, null, 20_000L, 0L, 2_000L, true)]         // no limit recorded: 5% of drive
    [InlineData(50, DriveType.Fixed, false, 1_000L, 0L, 950L, 100L, true)]            // Bin nearly full: Windows would purge
    [InlineData(50, DriveType.Fixed, false, null, 20_000L, 0L, 500L, false)]
    [InlineData(50, DriveType.Fixed, false, 1_000_000L, 0L, 900L, 10L, false)]
    public void Recycle_bin_policy_refuses_anything_windows_would_delete_permanently(
        int pathLength, DriveType type, bool nuke, long? capacity, long total, long used, long size, bool refused)
    {
        Assert.Equal(refused, RecycleBinPolicy.WhyNot(pathLength, type, nuke, capacity, total, used, size) is not null);
    }

    [Fact]
    public void Free_name_adds_restored_suffixes()
    {
        string file = _tree.File("x.txt", "1");
        Assert.Equal(Path.Combine(_tree.Root, "x (restored).txt"), QuarantineRestorer.FreeName(file, false));
        _tree.File("x (restored).txt", "2");
        Assert.Equal(Path.Combine(_tree.Root, "x (restored 2).txt"), QuarantineRestorer.FreeName(file, false));
        Assert.Equal(Path.Combine(_tree.Root, "free.txt"), QuarantineRestorer.FreeName(Path.Combine(_tree.Root, "free.txt"), false));
    }
}
