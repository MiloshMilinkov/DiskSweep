using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace DiskSweep.Mcp.Tests;

/// <summary>
/// Reads the compiled MCP server and proves what it can't call: no uninstaller, restore point, Recycle Bin, registry
/// or delete. A tool description can promise this; only the binary proves it.
/// </summary>
public sealed class NoForbiddenApisTests
{
    private static readonly string[] ForbiddenTypes =
        ["UninstallLauncher", "RestorePoint", "WindowsRecycleBin", "Registry", "RegistryKey", "RegistryReader"];

    private static readonly string[] ForbiddenMembers = ["SendToRecycleBin", "Delete"];

    [Fact]
    public void Mcp_assembly_never_references_uninstall_restore_point_recycle_bin_registry_or_delete()
    {
        using var stream = File.OpenRead(typeof(SweepSession).Assembly.Location);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();

        var types = md.TypeReferences.Select(h => md.GetTypeReference(h))
            .Select(t => (Namespace: md.GetString(t.Namespace), Name: md.GetString(t.Name)))
            .ToList();
        var members = md.MemberReferences.Select(h => md.GetString(md.GetMemberReference(h).Name)).ToList();

        // Sanity: it does reference the Core pieces it is built on.
        Assert.Contains(types, t => t.Name == "CleanupService");
        Assert.Contains(types, t => t.Name == "QuarantineRestorer");

        Assert.DoesNotContain(types, t => ForbiddenTypes.Contains(t.Name) || t.Namespace.StartsWith("Microsoft.Win32", StringComparison.Ordinal));
        Assert.DoesNotContain(members, ForbiddenMembers.Contains);
    }
}
