/*
 * DiskSweep
 * Copyright (c) 2026 Miloš Milinkov. All rights reserved.
 * Proprietary and confidential. Use, copying, modification or distribution
 * without written permission is prohibited. See the LICENSE file.
 */

using DiskSweep.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace DiskSweep.Mcp;

/// <summary>The server's services and tools, shared by Program (stdio) and the protocol tests (streams).</summary>
public static class ServerSetup
{
    public static IMcpServerBuilder AddDiskSweep(this IServiceCollection services, SweepEnvironment env)
    {
        services.AddSingleton(env);
        services.AddSingleton<SweepSession>();
        return services
            .AddMcpServer(o => o.ServerInfo = new() { Name = "disksweep", Version = "0.1.0" })
            .WithTools<ScanTools>();
    }
}
