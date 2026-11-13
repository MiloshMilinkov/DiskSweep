using System.Diagnostics;

namespace DiskSweep.Core.Tests;

/// <summary>A throw-away folder under %TEMP% that tests can build real trees, junctions and links in.</summary>
internal sealed class TempTree : IDisposable
{
    public string Root { get; }

    public TempTree()
    {
        Root = Path.Combine(Path.GetTempPath(), "DiskSweepTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Dir(string relative)
    {
        string p = Path.Combine(Root, relative);
        Directory.CreateDirectory(p);
        return p;
    }

    public string File(string relative, string content = "x")
    {
        string p = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        System.IO.File.WriteAllText(p, content);
        return p;
    }

    /// <summary>Directory junctions need no special rights, so these tests always run.</summary>
    public string Junction(string relative, string target)
    {
        string link = Path.Combine(Root, relative);
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        if (p.ExitCode != 0 || !Directory.Exists(link))
            throw new InvalidOperationException($"mklink /J failed: {p.StandardError.ReadToEnd()}");
        return link;
    }

    /// <summary>Symbolic links need Developer Mode or admin rights; returns null when Windows refuses.</summary>
    public string? TryFileSymlink(string relative, string target)
    {
        string link = Path.Combine(Root, relative);
        try
        {
            System.IO.File.CreateSymbolicLink(link, target);
            return link;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Age(string path, int days)
    {
        var when = DateTime.UtcNow.AddDays(-days);
        if (Directory.Exists(path))
        {
            Directory.SetCreationTimeUtc(path, when);
            Directory.SetLastWriteTimeUtc(path, when);
        }
        else
        {
            System.IO.File.SetCreationTimeUtc(path, when);
            System.IO.File.SetLastWriteTimeUtc(path, when);
        }
    }

    public void Dispose()
    {
        // Remove links first (Directory.Delete on a junction removes only the link) so targets are never walked.
        try
        {
            foreach (var d in new DirectoryInfo(Root).EnumerateFileSystemInfos("*", new EnumerationOptions
                     { RecurseSubdirectories = true, AttributesToSkip = 0 }))
            {
                if (d.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    if (d is DirectoryInfo di) di.Delete(); else d.Delete();
                }
            }
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
