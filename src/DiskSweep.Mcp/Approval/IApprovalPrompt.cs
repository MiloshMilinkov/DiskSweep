/*
 * DiskSweep
 * Copyright (c) 2026 Miloš Milinkov. All rights reserved.
 * Proprietary and confidential. Use, copying, modification or distribution
 * without written permission is prohibited. See the LICENSE file.
 */

namespace DiskSweep.Mcp;

/// <summary>What the person is asked to approve before a plan moves anything.</summary>
/// <param name="ClientName">The MCP client's own name for itself (self-reported, shown as is).</param>
public sealed record ApprovalRequest(string ClientName, int ItemCount, long TotalBytes, IReadOnlyList<string> Paths);

/// <summary>Asks the person at the PC, not the agent. Only a clear yes returns true.</summary>
public interface IApprovalPrompt
{
    bool Confirm(ApprovalRequest request);
}
