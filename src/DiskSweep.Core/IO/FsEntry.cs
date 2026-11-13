namespace DiskSweep.Core.IO;

/// <summary>One directory entry as returned by <see cref="FastDirectory"/>, without following links.</summary>
public readonly record struct FsEntry(
    string Name,
    string FullPath,
    FileAttributes Attributes,
    long Size,
    DateTime CreationUtc,
    DateTime LastWriteUtc,
    DateTime LastAccessUtc,
    uint ReparseTag)
{
    public bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;
    public bool IsReparsePoint => FileAttributeFlags.IsReparsePoint(Attributes);
    public bool IsCloudPlaceholder => FileAttributeFlags.IsCloudManaged(Attributes);
    public bool IsHiddenOrSystem => (Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;

    /// <summary>Newest of creation and last-write time. Last-access time is ignored because Windows often doesn't update it.</summary>
    public DateTime NewestChangeUtc => CreationUtc > LastWriteUtc ? CreationUtc : LastWriteUtc;
}

public static class FileKinds
{
    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".sys", ".msi", ".com", ".scr", ".ocx", ".cpl",
    };

    /// <summary>Files that hold program code. A folder with these in it belongs to an application.</summary>
    public static bool IsExecutable(string fileName) => ExecutableExtensions.Contains(Path.GetExtension(fileName));
}

public static class FileAttributeFlags
{
    public const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    public const FileAttributes Pinned = (FileAttributes)0x00080000;
    public const FileAttributes Unpinned = (FileAttributes)0x00100000;
    public const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    /// <summary>Cloud placeholders and files managed by a sync engine (OneDrive Files On-Demand and similar).</summary>
    public static bool IsCloudManaged(FileAttributes a) =>
        (a & (RecallOnOpen | RecallOnDataAccess | Pinned | Unpinned | FileAttributes.Offline)) != 0;

    public static bool IsReparsePoint(FileAttributes a) => (a & FileAttributes.ReparsePoint) != 0;

    public static string DescribeReparseTag(uint tag) => tag switch
    {
        0xA0000003 => "junction or mount point",
        0xA000000C => "symbolic link",
        0x80000017 => "compressed system file (WOF)",
        0x80000013 => "deduplicated file",
        0x8000001B => "app execution alias",
        _ when (tag & 0xFFFF0FFF) == 0x9000001A => "cloud file placeholder",
        _ => "reparse point",
    };
}
