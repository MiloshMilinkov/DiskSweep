using DiskSweep.Core.Matching;
using DiskSweep.Core.Safety;
using DiskSweep.Core.Scanning;
using DiskSweep.Core.SystemInfo;

namespace DiskSweep.Core.Tests;

public class MatchingAndParsingTests
{
    [Theory]
    [InlineData("JetBrains", "JetBrains s.r.o.")]
    [InlineData("Code", "Microsoft Visual Studio Code")]
    [InlineData("GitHubDesktop", "GitHub Desktop")]
    [InlineData("Mozilla Firefox", "Mozilla Firefox (x64 en-US)")]
    [InlineData("Notepad++", "Notepad++ (64-bit x64)")]
    [InlineData("nodejs", "Node.js")]
    [InlineData("VideoLAN", "VideoLAN")]
    public void Related_names_match(string folder, string program)
    {
        Assert.True(NameMatcher.Score(folder, program) >= NameMatcher.MatchThreshold,
            $"{folder} vs {program}: {NameMatcher.Score(folder, program):0.00}");
    }

    [Theory]
    [InlineData("OldGameStudio", "Contoso Editor")]
    [InlineData("Fabrikam", "Adobe Acrobat")]
    [InlineData("ZipperPro", "Paint.NET")]
    public void Unrelated_names_do_not_match(string folder, string program)
    {
        Assert.True(NameMatcher.Score(folder, program) < NameMatcher.MatchThreshold);
    }

    [Fact]
    public void Matcher_uses_publisher_and_install_folder()
    {
        var program = new InstalledProgram
        {
            KeyPath = "x", DisplayName = "Editor Pro 2024", Publisher = "Contoso Ltd.",
            InstallLocation = @"C:\Program Files\CtsoEdit",
        };
        var matcher = new NameMatcher([program], []);
        Assert.True(matcher.BestMatch("Contoso").Score >= NameMatcher.MatchThreshold);
        Assert.True(matcher.BestMatch("CtsoEdit").Score >= NameMatcher.MatchThreshold);
        Assert.True(matcher.BestMatch("Fabrikam").Score < NameMatcher.MatchThreshold);
    }

    [Theory]
    [InlineData("\"c:\\program files\\foo\\foo.exe\" -k netsvcs", "c:\\program files\\foo", true)]
    [InlineData("c:\\program files\\foo", "c:\\program files\\foo", true)]
    [InlineData("c:\\program files\\foobar\\x.exe", "c:\\program files\\foo", false)]
    [InlineData("<command>c:\\tools\\old\\run.cmd</command>", "c:\\tools\\old", true)]
    [InlineData("c:\\tools\\older\\run.cmd", "c:\\tools\\old", false)]
    public void Reference_matching_respects_path_boundaries(string text, string folder, bool expected)
    {
        Assert.Equal(expected, ReferenceIndex.ContainsPath(text, folder));
    }

    [Fact]
    public void References_expand_environment_variables()
    {
        string pf = Environment.GetEnvironmentVariable("ProgramFiles")!;
        var index = ReferenceIndex.FromEntries([("Service \"foo\"", @"""%ProgramFiles%\FooVendor\svc.exe"" -run")]);
        Assert.NotNull(index.FindReferenceInto(Path.Combine(pf, "FooVendor")));
        Assert.Null(index.FindReferenceInto(Path.Combine(pf, "BarVendor")));
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\Foo Bar\\uninst.exe\" /S", "C:\\Program Files\\Foo Bar\\uninst.exe")]
    [InlineData("C:\\App\\app.exe,0", "C:\\App\\app.exe")]
    [InlineData("rundll32.exe \"C:\\X\\y.dll\",Entry", "C:\\X\\y.dll")]
    public void Command_lines_yield_their_file_paths(string commandLine, string expected)
    {
        Assert.Contains(expected, CommandLinePaths.Extract(commandLine), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Msi_uninstall_strings_yield_no_paths()
    {
        Assert.Empty(CommandLinePaths.Extract("MsiExec.exe /X{12345678-1234-1234-1234-123456789012}"));
    }

    [Fact]
    public void Store_package_family_names_are_derived_from_full_names()
    {
        var pkg = StorePackageReader.Parse("Microsoft.WindowsCalculator_11.2405.2.0_x64__8wekyb3d8bbwe");
        Assert.NotNull(pkg);
        Assert.Equal("Microsoft.WindowsCalculator_8wekyb3d8bbwe", pkg!.FamilyName);
        Assert.Null(StorePackageReader.Parse("NotAPackage"));
    }

    [Fact]
    public void UserAssist_entries_are_decoded()
    {
        Assert.Equal("UEME_CTLSESSION", UserAssistReader.Rot13("HRZR_PGYFRFFVBA"));

        var when = new DateTime(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        byte[] data = new byte[72];
        BitConverter.GetBytes(12).CopyTo(data, 4);
        BitConverter.GetBytes(when.ToFileTimeUtc()).CopyTo(data, 60);

        var record = UserAssistReader.Decode(@"D:\Tools\app.exe", data);
        Assert.NotNull(record);
        Assert.Equal(12, record!.RunCount);
        Assert.Equal(when, record.LastRunUtc);
        Assert.Null(UserAssistReader.Decode(@"D:\Tools\app.exe", new byte[16]));

        var appId = UserAssistReader.Decode("Microsoft.WindowsTerminal_8wekyb3d8bbwe!App", data);
        Assert.NotNull(appId);
        Assert.True(appId!.IsAppId);
        Assert.Equal("Microsoft.WindowsTerminal", appId.MatchName);
        Assert.Null(UserAssistReader.Decode("UEME_CTLSESSION", data));
    }

    [Theory]
    [InlineData("Microsoft Visual C++ 2015-2022 Redistributable (x64)", true)]
    [InlineData("Microsoft .NET Runtime - 8.0.10 (x64)", true)]
    [InlineData("NVIDIA Graphics Driver 560.94", true)]
    [InlineData("VLC media player", false)]
    public void Shared_components_are_never_listed_as_unused(string name, bool shared)
    {
        var p = new InstalledProgram { KeyPath = "x", DisplayName = name };
        Assert.Equal(shared, UnusedProgramScanner.IsSharedComponent(p));
    }

    [Theory]
    [InlineData("Microsoft", true)]
    [InlineData("Windows Defender", true)]
    [InlineData("NVIDIA Corporation", true)]
    [InlineData("Package Cache", true)]
    [InlineData("Microsoft.WindowsAlarms_8wekyb3d8bbwe", true)]
    [InlineData(".vscode", true)]
    [InlineData("OldGameStudio", false)]
    public void OS_and_vendor_folders_are_never_flagged(string name, bool neverFlagged)
    {
        Assert.Equal(neverFlagged, KnownSystemFolders.IsNeverFlagged(name));
    }
}
