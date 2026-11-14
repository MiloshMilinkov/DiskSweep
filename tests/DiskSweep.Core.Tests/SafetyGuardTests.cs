using DiskSweep.Core.Safety;

namespace DiskSweep.Core.Tests;

/// <summary>Proves the hard-coded protected list can't be selected, however the path is written.</summary>
public class SafetyGuardTests
{
    private readonly SafetyGuard _guard = TestLayouts.Guard();

    [Theory]
    // Windows and its system folders
    [InlineData(@"Q:\Windows")]
    [InlineData(@"Q:\Windows\explorer.exe")]
    [InlineData(@"Q:\Windows\System32")]
    [InlineData(@"Q:\Windows\System32\drivers\etc\hosts")]
    [InlineData(@"Q:\Windows\SysWOW64\kernel32.dll")]
    [InlineData(@"Q:\Windows\WinSxS\amd64_foo")]
    [InlineData(@"Q:\Windows\Temp")]                       // the folder itself
    [InlineData(@"Q:\Windows\TempFolder\x.tmp")]           // prefix trick, not Windows\Temp
    [InlineData(@"D:\Windows\System32\config")]           // another Windows install
    [InlineData(@"D:\Old\System32\x.dll")]                 // System32 anywhere
    // Drive-root system items, on any drive
    [InlineData(@"Q:\Boot")]
    [InlineData(@"Q:\Boot\BCD")]
    [InlineData(@"Q:\Recovery")]
    [InlineData(@"Q:\System Volume Information")]
    [InlineData(@"D:\System Volume Information\tracking.log")]
    [InlineData(@"Q:\$Recycle.Bin")]
    [InlineData(@"E:\$Recycle.Bin\S-1-5-21\$R1.txt")]
    [InlineData(@"Q:\pagefile.sys")]
    [InlineData(@"Q:\hiberfil.sys")]
    [InlineData(@"Q:\swapfile.sys")]
    [InlineData(@"Q:\bootmgr")]
    [InlineData(@"Q:\")]
    [InlineData(@"D:\")]
    // Program Files roots, ProgramData, ProgramData\Microsoft
    [InlineData(@"Q:\Program Files")]
    [InlineData(@"Q:\Program Files (x86)")]
    [InlineData(@"D:\Program Files")]
    [InlineData(@"Q:\ProgramData")]
    [InlineData(@"Q:\ProgramData\Microsoft")]
    [InlineData(@"Q:\ProgramData\Microsoft\Windows\Start Menu")]
    // User profile roots (and their parent)
    [InlineData(@"Q:\Users\alice")]
    [InlineData(@"Q:\Users\Public")]
    [InlineData(@"Q:\Users\Default")]
    [InlineData(@"Q:\Users\Default\AppData\Local\Microsoft\Windows\CloudStore")] // new-account template
    [InlineData(@"Q:\Users")]
    // Installed programs, their parents and their uninstallers
    [InlineData(@"Q:\Program Files\Contoso\Editor")]
    [InlineData(@"Q:\Program Files\Contoso\Editor\editor.exe")]
    [InlineData(@"Q:\Program Files\Contoso")]               // contains an installed program
    [InlineData(@"Q:\Users\alice\AppData\Local\Programs\Fabrikam\app.exe")]
    [InlineData(@"Q:\Games")]
    [InlineData(@"Q:\Users\alice\Downloads\setup-uninstall.exe")]
    // Folders DiskSweep scans inside, cloud folders, its own folder
    [InlineData(@"Q:\Users\alice\AppData")]
    [InlineData(@"Q:\Users\alice\AppData\Local")]
    [InlineData(@"Q:\Users\alice\AppData\Local\Temp")]
    [InlineData(@"Q:\Users\alice\OneDrive")]
    [InlineData(@"Q:\Users\alice\OneDrive\Documents\cv.docx")]
    [InlineData(@"G:\My Drive\notes.txt")]
    [InlineData(@"Q:\Users\alice\AppData\Local\DiskSweep\logs\today.log")]
    // DiskSweep's quarantine on any drive
    [InlineData(@"Q:\DiskSweep Quarantine")]
    [InlineData(@"D:\DiskSweep Quarantine\2026-11-14_101010_abc123\1\old.log")]
    [InlineData(@"E:\diskSWEEP quarantine\x")]
    // Version-control data, anywhere
    [InlineData(@"D:\Projects\app\.git")]
    [InlineData(@"D:\Projects\app\.git\refs\tags")]
    [InlineData(@"Q:\Users\alice\code\.git\objects\pack\pack-1.pack")]
    [InlineData(@"D:\Old\.svn\pristine")]
    [InlineData(@"D:\Old\.hg\store")]
    public void Protected_paths_are_blocked(string path)
    {
        var verdict = _guard.Check(path);
        Assert.False(verdict.IsAllowed, $"{path} should be blocked");
        Assert.False(string.IsNullOrWhiteSpace(verdict.Reason));
    }

    [Theory]
    [InlineData(@"Q:\Windows\Temp\old.tmp")]
    [InlineData(@"Q:\Windows\Temp\Setup123\log.txt")]
    [InlineData(@"Q:\Program Files\OldVendor")]
    [InlineData(@"Q:\Program Files\Contoso\Editor Backup")]    // sibling that only shares a prefix
    [InlineData(@"Q:\ProgramData\OldApp")]
    [InlineData(@"Q:\ProgramData\MicrosoftEdgeOldCache")]       // rules only care about ProgramData\Microsoft itself
    [InlineData(@"Q:\Users\alice\Downloads\big.iso")]
    [InlineData(@"Q:\Users\alice\AppData\Roaming\OldApp")]
    [InlineData(@"Q:\Users\alice\AppData\Local\Temp\abc.tmp")]
    [InlineData(@"D:\Stuff\Empty")]
    public void Ordinary_paths_are_allowed(string path)
    {
        var verdict = _guard.Check(path);
        Assert.True(verdict.IsAllowed, $"{path} was blocked: {verdict.Reason}");
    }

    [Theory]
    [InlineData(@"q:\windows\system32")]                              // case
    [InlineData(@"Q:/Windows/System32")]                              // forward slashes
    [InlineData(@"Q:\Windows\System32\")]                             // trailing separator
    [InlineData(@"Q:\Windows\Temp\..\System32")]                      // climbing out of Windows\Temp
    [InlineData(@"Q:\Windows\Temp\..\..\Windows\notepad.exe")]
    [InlineData(@"Q:\Users\alice\Downloads\..")]                      // resolves to the profile root
    [InlineData(@"Q:\Program Files\.\")]
    [InlineData(@"\\?\Q:\Windows\System32")]                          // extended-length prefix
    [InlineData(@"\??\Q:\Windows")]
    [InlineData(@"Q:\Windows.")]                                      // trailing dot is stripped by Windows
    [InlineData(@"Q:\Windows ")]
    [InlineData(@"  Q:\Windows  ")]
    public void Path_tricks_cannot_reach_protected_paths(string path)
    {
        Assert.False(_guard.Check(path).IsAllowed, $"{path} should be blocked");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData(@"Windows\System32")]                  // relative
    [InlineData(@"Q:Windows")]                         // drive-relative
    [InlineData(@"\Windows")]                          // root-relative
    [InlineData(@"\\server\share\file.txt")]           // network
    [InlineData(@"\\?\UNC\server\share\file.txt")]
    [InlineData(@"\\.\PhysicalDrive0")]                // device
    [InlineData(@"Q:\Users\alice\Downloads\a.txt:hidden")]    // alternate data stream
    [InlineData(@"Q:\Users\alice\Downloads\a.txt::$DATA")]
    [InlineData(@"Q:\Users\alice\Downloads\a*.txt")]  // wildcard
    public void Malformed_paths_are_blocked(string? path)
    {
        Assert.False(_guard.Check(path!).IsAllowed);
    }

    [Fact]
    public void Exclusions_protect_the_folder_its_contents_and_its_parents()
    {
        var guard = TestLayouts.Guard(null, @"Q:\Users\alice\Downloads\Keep");
        Assert.False(guard.Check(@"Q:\Users\alice\Downloads\Keep").IsAllowed);
        Assert.False(guard.Check(@"Q:\Users\alice\Downloads\Keep\a.iso").IsAllowed);
        Assert.False(guard.Check(@"Q:\Users\alice\Downloads").IsAllowed);
        Assert.True(guard.Check(@"Q:\Users\alice\Downloads\Other.iso").IsAllowed);
    }

    [Fact]
    public void Cloud_placeholder_attributes_are_blocked()
    {
        const string path = @"Q:\Users\alice\Documents\report.pdf";
        var probe = new TestLayouts.FakeProbe()
            .Set(path, FileAttributes.Archive | (FileAttributes)0x00400000); // RECALL_ON_DATA_ACCESS
        Assert.False(TestLayouts.Guard(probe).Check(path).IsAllowed);

        probe.Set(path, FileAttributes.Archive | FileAttributes.Offline);
        Assert.False(TestLayouts.Guard(probe).Check(path).IsAllowed);
    }

    [Fact]
    public void Reparse_point_items_and_their_contents_are_blocked()
    {
        var probe = new TestLayouts.FakeProbe()
            .Set(@"D:\Data\Link", FileAttributes.Directory | FileAttributes.ReparsePoint);
        var guard = TestLayouts.Guard(probe);

        Assert.False(guard.Check(@"D:\Data\Link").IsAllowed);
        Assert.False(guard.Check(@"D:\Data\Link\file.txt").IsAllowed);
        Assert.False(guard.Check(@"D:\Data\Link\Deeper\Folder").IsAllowed);
        Assert.True(guard.Check(@"D:\Data\NotALink").IsAllowed);
    }

    [Fact]
    public void Missing_or_unreadable_paths_are_blocked()
    {
        var probe = new TestLayouts.FakeProbe().Set(@"D:\Gone", null);
        var guard = TestLayouts.Guard(probe);
        Assert.False(guard.Check(@"D:\Gone").IsAllowed);
        Assert.False(guard.Check(@"D:\Gone\child.txt").IsAllowed); // parent can't be read
    }

    [Theory]
    [InlineData(@"Q:\Windows", true)]
    [InlineData(@"Q:\Windows\System32", true)]
    [InlineData(@"Q:\Windows\Temp", false)]
    [InlineData(@"Q:\Windows\Temp\x", false)]
    [InlineData(@"Q:\ProgramData\Microsoft\Windows", true)]
    [InlineData(@"Q:\Program Files\Contoso\Editor\bin", true)]
    [InlineData(@"Q:\Program Files\Contoso", false)] // blocked as an item, but other vendors' folders inside may be scanned
    [InlineData(@"Q:\Users\alice\OneDrive\x", true)]
    [InlineData(@"G:\anything", true)]
    [InlineData(@"Q:\$Recycle.Bin", true)]
    [InlineData(@"Q:\Users\alice", false)]
    public void Protected_subtrees_are_not_descended_into(string path, bool expected)
    {
        Assert.Equal(expected, _guard.IsProtectedSubtree(path));
    }

    [Theory]
    [InlineData(@"Q:\Program Files\Windows Defender\Platform", true)]
    [InlineData(@"Q:\Program Files (x86)\Common Files\Shared", true)]
    [InlineData(@"D:\Program Files\NVIDIA Corporation\Empty", true)]
    [InlineData(@"Q:\ProgramData\Microsoft Help\x", true)]
    [InlineData(@"Q:\Users\alice\AppData\Local\Microsoft\Windows\INetCache", true)]
    [InlineData(@"Q:\Users\alice\AppData\Local\Packages\Microsoft.Photos_8wekyb3d8bbwe\AC", true)]
    [InlineData(@"Q:\Users\alice\AppData\Local\Programs\OldTool\cache", false)] // judged by "OldTool", not "Programs"
    [InlineData(@"Q:\Program Files\OldVendor\Empty", false)]
    [InlineData(@"Q:\Users\alice\AppData\Roaming\OldTool", false)]
    [InlineData(@"Q:\Users\alice\Documents\Microsoft\x", false)]
    public void OS_and_vendor_folders_in_app_areas_are_recognised(string path, bool expected)
    {
        Assert.Equal(expected, _guard.IsInOsOrVendorFolder(path));
    }

    [Theory]
    [InlineData(@"Q:\Program Files\X", true)]
    [InlineData(@"Q:\Program Files (x86)\X", true)]
    [InlineData(@"D:\Program Files\X", true)]
    [InlineData(@"Q:\ProgramData\X", true)]
    [InlineData(@"Q:\Users\alice\AppData\Roaming\X", true)]
    [InlineData(@"Q:\Users\alice\Documents\X", false)]
    [InlineData(@"D:\Projects\X", false)]
    public void App_areas_are_recognised(string path, bool expected)
    {
        Assert.Equal(expected, _guard.IsInAppArea(path));
    }
}
