namespace DiskSweep.Core.Safety;

/// <summary>
/// Folders in Program Files, ProgramData and AppData that belong to Windows, Microsoft runtimes or hardware vendors.
/// They are never reported as leftovers, even when no installed program claims them.
/// </summary>
public static class KnownSystemFolders
{
    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        // Windows and Microsoft infrastructure
        "Common Files", "Internet Explorer", "Reference Assemblies", "Uninstall Information", "WindowsApps",
        "ModifiableWindowsApps", "WindowsPowerShell", "PowerShell", "dotnet", "PackageManagement", "Package Cache",
        "Packages", "Programs", "Temp", "Comms", "ConnectedDevicesPlatform", "D3DSCache", "CrashDumps", "Publishers",
        "PlaceholderTileLogoFolder", "VirtualStore", "History", "IsolatedStorage", "PeerDistRepub", "assembly", "Apps",
        "Deployment", "Diagnostics", "ElevatedDiagnostics", "Application Data", "Temporary Internet Files",
        "Desktop", "Documents", "Start Menu", "Templates", "Favorites", "USOShared", "USOPrivate", "SoftwareDistribution",
        "ssh", "Identities", "Media Center Programs", "Mozilla Maintenance Service", "IIS", "IIS Express", "inetpub",
        "Hyper-V", "Installer", "Setup", "Updates", "Licenses", "UNP", "Feedback", "WinGet", "NuGet", "SquirrelTemp",
        "chocolatey", "regid", "Default", "Public", "Shared", "PerfLogs",

        // Hardware and PC vendors (drivers often install without an uninstall entry)
        "Intel", "Intel Corporation", "NVIDIA", "NVIDIA Corporation", "AMD", "ATI", "ATI Technologies", "Realtek",
        "Synaptics", "Elan", "ELAN", "Dolby", "Waves", "Conexant", "IDT", "Broadcom", "Qualcomm", "MediaTek", "Killer Networking",
        "Rivet Networks", "Lenovo", "Dell", "Dell Inc", "HP", "Hewlett-Packard", "ASUS", "ASUSTeK", "Acer", "MSI", "Gigabyte",
        "Samsung", "Logitech", "Razer", "Corsair", "SteelSeries", "Tobii", "Cirrus Logic", "Thunderbolt", "Alps",
    };

    private static readonly string[] Prefixes =
    [
        "Microsoft", "Windows", "MicrosoftEdge", "Microsoft.", "Windows.", "MSBuild", "regid.", ".", "{", "$",
    ];

    public static bool IsNeverFlagged(string folderName)
    {
        if (Names.Contains(folderName)) return true;
        foreach (string prefix in Prefixes)
            if (folderName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
