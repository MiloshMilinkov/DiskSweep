namespace DiskSweep.Core;

/// <summary>DiskSweep's own folders. They are protected from every scan (see <c>ProtectionLayout.AppOwnDirectories</c>).</summary>
public static class AppPaths
{
    public static string DataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskSweep");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");
}
