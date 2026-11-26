/*
 * DiskSweep
 * Copyright (c) 2026 Miloš Milinkov. All rights reserved.
 * Proprietary and confidential. Use, copying, modification or distribution
 * without written permission is prohibited. See the LICENSE file.
 */

using System.Runtime.InteropServices;
using System.Text;
using DiskSweep.Core.IO;

namespace DiskSweep.Mcp;

/// <summary>
/// A Windows Yes/No box shown by the server itself, so the agent can't answer it and client auto-approve settings
/// don't apply. No is the default button; anything but a click on Yes (including a box that can't be shown) is a no.
/// </summary>
internal sealed class WindowsApprovalPrompt : IApprovalPrompt
{
    private const uint MB_YESNO = 0x4, MB_ICONWARNING = 0x30, MB_DEFBUTTON2 = 0x100, MB_SETFOREGROUND = 0x10000, MB_TOPMOST = 0x40000;
    private const int IDYES = 6;
    private const int MaxPathsShown = 10;
    private const int MaxClientNameLength = 60;

    public bool Confirm(ApprovalRequest request) =>
        MessageBoxW(IntPtr.Zero, Text(request), "DiskSweep: approve cleanup",
            MB_YESNO | MB_ICONWARNING | MB_DEFBUTTON2 | MB_SETFOREGROUND | MB_TOPMOST) == IDYES;

    internal static string Text(ApprovalRequest r)
    {
        var text = new StringBuilder()
            .AppendLine($"An AI assistant (\"{OneLine(r.ClientName)}\") asks DiskSweep to move {r.ItemCount} item(s), {PathUtil.FormatBytes(r.TotalBytes)}, into quarantine.")
            .AppendLine()
            .AppendLine("Nothing is deleted. You can put everything back from DiskSweep → Quarantine.")
            .AppendLine();
        foreach (string path in r.Paths.Take(MaxPathsShown)) text.AppendLine(path);
        if (r.Paths.Count > MaxPathsShown) text.AppendLine($"…and {r.Paths.Count - MaxPathsShown} more");
        return text.AppendLine().Append("Move these items to quarantine?").ToString();
    }

    /// <summary>The client names itself, so its name can't add lines or crowd out the rest of the message.</summary>
    private static string OneLine(string name)
    {
        string flat = string.Join(' ', (name ?? "").Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        flat = new string(flat.Where(c => !char.IsControl(c)).ToArray());
        return flat.Length <= MaxClientNameLength ? flat : flat[..MaxClientNameLength] + "…";
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
