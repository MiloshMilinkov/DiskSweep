using DiskSweep.Core.IO;

namespace DiskSweep.Core;

/// <summary>DiskSweep's own folders. They are protected from every scan and every cleanup.</summary>
public static class AppPaths
{
    public static string DataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskSweep");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>Quarantine lives directly under each drive root, so moving an item there is a rename on the same drive.</summary>
    public const string QuarantineFolderName = "DiskSweep Quarantine";

    /// <summary>"D:\DiskSweep Quarantine" for anything on D:.</summary>
    public static string QuarantineRootFor(string path) => Path.Combine(PathUtil.DriveRoot(path), QuarantineFolderName);
}
