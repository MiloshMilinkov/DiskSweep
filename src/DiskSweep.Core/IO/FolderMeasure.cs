namespace DiskSweep.Core.IO;

/// <param name="NewestChangeUtc">
/// Newest creation or last-write time of the folder itself and everything inside it. Scanners store it as
/// <c>ScanItem.LastModifiedUtc</c>, and cleanup refuses a folder whose value has moved on since the scan.
/// </param>
public sealed record FolderStats(
    long TotalBytes,
    int FileCount,
    int FolderCount,
    int ExecutableCount,
    DateTime NewestChangeUtc,
    string? FirstReparsePoint,
    string? FirstCloudItem,
    string? FirstUnreadable,
    bool Truncated)
{
    public bool IsFullyReadable => FirstUnreadable is null && !Truncated;
}

/// <summary>Walks a folder tree without following links and summarises it.</summary>
public static class FolderMeasure
{
    public static FolderStats Measure(string root, CancellationToken token, int maxEntries = 250_000, Action<string>? onFile = null)
    {
        long bytes = 0;
        int files = 0, folders = 0, executables = 0, seen = 0;
        string? reparse = null, cloud = null, unreadable = null;

        // The folder's own times count too (renaming or emptying it changes its last-write time).
        var self = new DirectoryInfo(root);
        DateTime newest = self.Exists
            ? (self.CreationTimeUtc > self.LastWriteTimeUtc ? self.CreationTimeUtc : self.LastWriteTimeUtc)
            : DateTime.MinValue;
        bool truncated = false;

        var stack = new Stack<string>();
        stack.Push(root);
        var buffer = new List<FsEntry>(256);

        while (stack.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            string dir = stack.Pop();
            if (!FastDirectory.TryList(dir, buffer, out int error))
            {
                unreadable ??= $"{dir} ({FastDirectory.DescribeError(error)})";
                continue;
            }

            foreach (var e in buffer)
            {
                if (++seen > maxEntries) { truncated = true; break; }
                if (e.NewestChangeUtc > newest) newest = e.NewestChangeUtc;

                if (e.IsReparsePoint)
                {
                    reparse ??= $"{e.FullPath} ({FileAttributeFlags.DescribeReparseTag(e.ReparseTag)})";
                    continue; // never followed
                }
                if (e.IsCloudPlaceholder) cloud ??= e.FullPath;

                if (e.IsDirectory)
                {
                    folders++;
                    stack.Push(e.FullPath);
                }
                else
                {
                    files++;
                    bytes += e.Size;
                    if (FileKinds.IsExecutable(e.Name)) executables++;
                    onFile?.Invoke(e.FullPath);
                }
            }
            if (truncated) break;
        }

        return new FolderStats(bytes, files, folders, executables, newest, reparse, cloud, unreadable, truncated);
    }
}
