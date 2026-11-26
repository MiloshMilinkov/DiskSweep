/*
 * DiskSweep
 * Copyright (c) 2026 Miloš Milinkov. All rights reserved.
 * Proprietary and confidential. Use, copying, modification or distribution
 * without written permission is prohibited. See the LICENSE file.
 */

using DiskSweep.Core.Cleanup;

namespace DiskSweep.Mcp;

/// <summary>
/// Handed to every <see cref="CleanupService"/> and <see cref="QuarantineRestorer"/> the server builds, so even a bug
/// that asked for the Recycle Bin couldn't reach it. The MCP server only ever moves items into quarantine.
/// </summary>
internal sealed class RefusingRecycleBin : IRecycleBin
{
    public static readonly RefusingRecycleBin Instance = new();

    public RecycleResult Recycle(string path, long sizeBytes) =>
        new(RecycleStatus.Refused, "DiskSweep's MCP server never uses the Recycle Bin.");
}
