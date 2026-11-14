using System.Management;
using System.Runtime.InteropServices;
using DiskSweep.Core.Logging;
using DiskSweep.Core.SystemInfo;

namespace DiskSweep.Core.Cleanup;

public sealed record RestorePointResult(bool Created, string Message);

/// <summary>
/// Creates a Windows System Restore point (WMI root\default:SystemRestore). Needs admin rights and System Protection
/// on the system drive, and Windows makes at most one every 24 hours, so the result is checked rather than assumed.
/// A restore point covers Windows, programs and the registry, not personal files.
/// </summary>
public static class RestorePoint
{
    private const uint ModifySettings = 12;
    private const uint BeginSystemChange = 100;

    public static RestorePointResult TryCreate(string description, IActionLog log)
    {
        if (!SystemSnapshot.IsRunningAsAdministrator())
            return new(false, "Creating a restore point needs administrator rights.");

        try
        {
            DateTime? before = Newest();
            using var restore = new ManagementClass(new ManagementScope(@"\\.\root\default"), new ManagementPath("SystemRestore"), null);
            using var input = restore.GetMethodParameters("CreateRestorePoint");
            input["Description"] = description;
            input["RestorePointType"] = ModifySettings;
            input["EventType"] = BeginSystemChange;
            using var output = restore.InvokeMethod("CreateRestorePoint", input, null);
            uint rc = Convert.ToUInt32(output?["ReturnValue"] ?? 1u);
            if (rc != 0)
            {
                log.Warn($"Restore point not created: CreateRestorePoint returned {rc}.");
                return new(false, $"Windows didn't create it (error {rc}). System Protection may be turned off for the system drive.");
            }

            DateTime? after = Newest();
            if (after is null || (before is not null && after <= before))
            {
                string latest = before is { } b ? $" The latest one is from {b.ToLocalTime():g}." : "";
                log.Warn("Restore point request accepted but Windows skipped it (24-hour limit).");
                return new(false, $"Windows skipped it because it makes at most one restore point every 24 hours.{latest}");
            }

            log.Info($"Restore point created: {description} ({after.Value.ToLocalTime():g}).");
            return new(true, $"Restore point created ({after.Value.ToLocalTime():g}).");
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            log.Error("Restore point failed", ex);
            return new(false, $"Windows couldn't create it: {ex.Message}. System Protection may be turned off.");
        }
    }

    private static DateTime? Newest()
    {
        using var searcher = new ManagementObjectSearcher(@"root\default", "SELECT CreationTime FROM SystemRestore");
        DateTime? newest = null;
        foreach (ManagementBaseObject point in searcher.Get())
        {
            using (point)
            {
                if (point["CreationTime"] is not string text) continue;
                DateTime created = ManagementDateTimeConverter.ToDateTime(text).ToUniversalTime();
                if (newest is null || created > newest) newest = created;
            }
        }
        return newest;
    }
}
