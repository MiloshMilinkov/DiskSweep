using DiskSweep.Core.Models;

namespace DiskSweep.Core.SystemInfo;

public static class DriveCatalog
{
    /// <summary>Fixed and removable drives that are ready. Network, optical and RAM drives are left out.</summary>
    public static IReadOnlyList<DriveSummary> GetDrives()
    {
        string systemRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\";
        var result = new List<DriveSummary>();
        foreach (var d in DriveInfo.GetDrives())
        {
            if (d.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
            try
            {
                if (!d.IsReady) continue;
                result.Add(new DriveSummary(
                    d.RootDirectory.FullName,
                    string.IsNullOrWhiteSpace(d.VolumeLabel) ? (d.DriveType == DriveType.Removable ? "Removable drive" : "Local disk") : d.VolumeLabel,
                    d.DriveFormat,
                    d.DriveType,
                    d.TotalSize,
                    d.AvailableFreeSpace,
                    IO.PathUtil.AreSame(d.RootDirectory.FullName, systemRoot)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Drive vanished or is locked (BitLocker); leave it out.
            }
        }
        return result;
    }
}
