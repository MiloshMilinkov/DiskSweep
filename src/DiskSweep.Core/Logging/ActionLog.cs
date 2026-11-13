using System.Collections.Concurrent;
using DiskSweep.Core.Models;

namespace DiskSweep.Core.Logging;

public interface IActionLog
{
    string? FilePath { get; }
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? ex = null);
    void Skip(string path, string reason);
    void Flush();
}

public sealed class NullActionLog : IActionLog
{
    public static readonly NullActionLog Instance = new();
    public string? FilePath => null;
    public void Info(string message) { }
    public void Warn(string message) { }
    public void Error(string message, Exception? ex = null) { }
    public void Skip(string path, string reason) { }
    public void Flush() { }
}

/// <summary>Appends every action to %LOCALAPPDATA%\DiskSweep\logs\disksweep-YYYY-MM-DD.log.</summary>
public sealed class ActionLog : IActionLog, IDisposable
{
    private readonly object _gate = new();
    private readonly string _directory;
    private StreamWriter _writer = null!;
    private DateTime _day;

    /// <summary>Today's log file. Rolls over to a new file after midnight.</summary>
    public string? FilePath { get; private set; }

    public ActionLog(string? directory = null)
    {
        _directory = directory ?? AppPaths.LogDirectory;
        Directory.CreateDirectory(_directory);
        OpenForToday();
    }

    private void OpenForToday()
    {
        _day = DateTime.Today;
        FilePath = Path.Combine(_directory, $"disksweep-{_day:yyyy-MM-dd}.log");
        var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _writer = new StreamWriter(stream) { AutoFlush = false };
    }

    public void Info(string message) => Write("INFO ", message, flush: true);
    public void Warn(string message) => Write("WARN ", message, flush: true);
    public void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message} | {ex.GetType().Name}: {ex.Message}", flush: true);
    public void Skip(string path, string reason) => Write("SKIP ", $"{path} | {reason}", flush: false);

    public void Flush()
    {
        lock (_gate) _writer.Flush();
    }

    private void Write(string level, string message, bool flush)
    {
        lock (_gate)
        {
            if (DateTime.Today != _day)
            {
                _writer.Dispose();
                OpenForToday();
            }
            _writer.Write(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            _writer.Write(' ');
            _writer.Write(level);
            _writer.Write(' ');
            _writer.WriteLine(message);
            if (flush) _writer.Flush();
        }
    }

    public void Dispose()
    {
        lock (_gate) _writer.Dispose();
    }
}

/// <summary>Collects skipped paths for the "Skipped" tab and writes each one to the log.</summary>
public sealed class SkipLog
{
    private const int MaxKept = 50_000;
    private readonly ConcurrentQueue<SkippedEntry> _entries = new();
    private readonly IActionLog _log;
    private int _total;

    public SkipLog(IActionLog log) => _log = log;

    public int Total => Volatile.Read(ref _total);

    public void Add(string path, string reason)
    {
        _log.Skip(path, reason);
        if (Interlocked.Increment(ref _total) <= MaxKept) _entries.Enqueue(new SkippedEntry(path, reason));
    }

    public IReadOnlyList<SkippedEntry> Snapshot() => _entries.ToArray();
}
