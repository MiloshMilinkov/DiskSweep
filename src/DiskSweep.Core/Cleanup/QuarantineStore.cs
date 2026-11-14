using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiskSweep.Core.IO;

namespace DiskSweep.Core.Cleanup;

public enum QuarantineState { Pending, Quarantined, Failed, Restored, Recycled }

/// <summary>One item in a quarantine batch. <see cref="StoredRelativePath"/> is relative to the batch folder.</summary>
public sealed class QuarantineEntry
{
    public int Slot { get; set; }
    public required string OriginalPath { get; set; }
    public required string StoredRelativePath { get; set; }
    public bool IsDirectory { get; set; }
    public long SizeBytes { get; set; }
    public DateTime QuarantinedUtc { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public QuarantineState State { get; set; }

    public DateTime? ChangedUtc { get; set; }
    public string? RestoredTo { get; set; }
    public string? Note { get; set; }

    public void SetState(QuarantineState state, string? note = null)
    {
        State = state;
        if (note is not null) Note = note;
        ChangedUtc = DateTime.UtcNow;
    }
}

/// <summary>
/// A folder inside the quarantine root holding one cleanup run's items, one numbered sub-folder per item, plus
/// journal.jsonl: one line appended every time an entry changes (before and after every move). The last line for a
/// slot wins. Appending keeps each save small however many items the run has, and a half-written last line after a
/// crash is simply ignored.
/// </summary>
public sealed class QuarantineBatch
{
    public const string JournalName = "journal.jsonl";
    private static readonly JsonSerializerOptions JsonOptions = new();

    private readonly SortedDictionary<int, QuarantineEntry> _entries = [];
    private int _lastSlot;

    public string Folder { get; }
    public DateTime CreatedUtc { get; }
    public IReadOnlyCollection<QuarantineEntry> Entries => _entries.Values;

    private QuarantineBatch(string folder, DateTime createdUtc)
    {
        Folder = folder;
        CreatedUtc = createdUtc;
    }

    internal static QuarantineBatch Create(string folder)
    {
        Directory.CreateDirectory(folder);
        return new QuarantineBatch(folder, DateTime.UtcNow);
    }

    internal static QuarantineBatch? Load(string folder)
    {
        string journal = Path.Combine(folder, JournalName);
        try
        {
            if (!File.Exists(journal)) return null;
            var batch = new QuarantineBatch(folder, Directory.GetCreationTimeUtc(folder));
            foreach (string line in File.ReadLines(journal))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    if (JsonSerializer.Deserialize<QuarantineEntry>(line, JsonOptions) is { } entry)
                    {
                        batch._entries[entry.Slot] = entry;
                        batch._lastSlot = Math.Max(batch._lastSlot, entry.Slot);
                    }
                }
                catch (JsonException) { /* a line cut short by a crash; the earlier line for that slot still stands */ }
            }
            return batch;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Where the item is stored. The journal is an ordinary file anyone can edit, so a path that would leave the
    /// batch folder (for example "..\..\Windows\x") is never trusted: an empty string comes back, which doesn't exist.
    /// </summary>
    public string PathOf(QuarantineEntry entry)
    {
        string full = PathUtil.NormalizeLoose(Path.Join(Folder, entry.StoredRelativePath)) ?? "";
        string folder = PathUtil.NormalizeLoose(Folder) ?? Folder;
        return PathUtil.IsStrictlyUnder(full, folder) ? full : "";
    }

    /// <summary>Reserves a slot for <paramref name="originalPath"/> and records it as Pending before anything moves.</summary>
    internal QuarantineEntry Reserve(string originalPath, bool isDirectory, long size)
    {
        int slot = ++_lastSlot;
        Directory.CreateDirectory(Path.Join(Folder, slot.ToString()));
        var entry = new QuarantineEntry
        {
            Slot = slot,
            OriginalPath = originalPath,
            StoredRelativePath = Path.Join(slot.ToString(), Path.GetFileName(originalPath)),
            IsDirectory = isDirectory,
            SizeBytes = size,
            QuarantinedUtc = DateTime.UtcNow,
            State = QuarantineState.Pending,
        };
        _entries[slot] = entry;
        Record(entry);
        return entry;
    }

    /// <summary>Appends the entry's current state to the journal and flushes it.</summary>
    public void Record(QuarantineEntry entry)
    {
        byte[] line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry, JsonOptions) + "\n");
        using var stream = new FileStream(Path.Combine(Folder, JournalName), FileMode.Append, FileAccess.Write, FileShare.Read);
        stream.Write(line);
        stream.Flush();
    }

    /// <summary>Removes the item's numbered sub-folder once it's empty (never anything with content in it).</summary>
    internal void RemoveEmptySlot(QuarantineEntry entry)
    {
        string slot = Path.Join(Folder, entry.Slot.ToString());
        var children = new List<FsEntry>();
        if (FastDirectory.GetAttributes(slot, out _) is { } a && !FileAttributeFlags.IsReparsePoint(a) &&
            FastDirectory.TryList(slot, children, out _) && children.Count == 0)
        {
            try { Directory.Delete(slot); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}

/// <summary>Per-drive quarantine: "&lt;drive&gt;:\DiskSweep Quarantine". Same drive as the item, so moving is a rename.</summary>
public sealed class QuarantineStore
{
    private const string ReadMe =
        "This folder holds items DiskSweep moved here instead of deleting them.\r\n" +
        "Open DiskSweep and choose Quarantine to put them back where they came from.\r\n" +
        "Each sub-folder is one cleanup run; journal.jsonl lists every item's original location.\r\n";

    private readonly Func<string, string> _rootFor;
    private readonly Dictionary<string, QuarantineBatch> _openBatches = new(PathUtil.Comparer);

    /// <param name="rootFor">Maps an item path to its quarantine root. Tests pass a temp folder; the app uses the item's drive.</param>
    public QuarantineStore(Func<string, string>? rootFor = null) => _rootFor = rootFor ?? AppPaths.QuarantineRootFor;

    /// <summary>The batch for this cleanup run on the item's drive, created on first use.</summary>
    internal QuarantineBatch BatchFor(string itemPath)
    {
        string root = _rootFor(itemPath);
        if (_openBatches.TryGetValue(root, out var batch)) return batch;

        // Never write through a link: if the quarantine folder was replaced by a junction, items would land elsewhere.
        if (FastDirectory.GetAttributes(root, out _) is { } a && (FileAttributeFlags.IsReparsePoint(a) || FileAttributeFlags.IsCloudManaged(a)))
            throw new IOException($"{root} is a link or a cloud folder, so DiskSweep won't put anything in it.");

        Directory.CreateDirectory(root);
        string readMe = Path.Combine(root, "README.txt");
        if (FastDirectory.Exists(readMe) == false) File.WriteAllText(readMe, ReadMe);

        string folder = Path.Combine(root, $"{DateTime.Now:yyyy-MM-dd_HHmmss}_{Guid.NewGuid().ToString("N")[..6]}");
        batch = QuarantineBatch.Create(folder);
        _openBatches[root] = batch;
        return batch;
    }

    /// <summary>Every batch under the given quarantine roots, newest first. Links are never followed; unreadable batches are skipped.</summary>
    public static IReadOnlyList<QuarantineBatch> LoadAll(IEnumerable<string> roots)
    {
        var batches = new List<QuarantineBatch>();
        var folders = new List<FsEntry>();
        foreach (string root in roots.Distinct(PathUtil.Comparer))
        {
            if (FastDirectory.GetAttributes(root, out _) is not { } a || FileAttributeFlags.IsReparsePoint(a)) continue;
            if (!FastDirectory.TryList(root, folders, out _)) continue;
            foreach (var folder in folders.Where(f => f.IsDirectory && !f.IsReparsePoint))
                if (QuarantineBatch.Load(folder.FullPath) is { } batch) batches.Add(batch);
        }
        return batches.OrderByDescending(b => b.CreatedUtc).ToList();
    }
}
