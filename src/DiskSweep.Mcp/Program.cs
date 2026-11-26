/*
 * DiskSweep
 * Copyright (c) 2026 Miloš Milinkov. All rights reserved.
 * Proprietary and confidential. Use, copying, modification or distribution
 * without written permission is prohibited. See the LICENSE file.
 */

using DiskSweep.Core;
using DiskSweep.Core.Logging;
using DiskSweep.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Stdio MCP server. Stdout carries the protocol, so every log line goes to stderr or DiskSweep's action log.
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

using var log = new ActionLog(Path.Combine(AppPaths.LogDirectory, "mcp"));
builder.Services.AddDiskSweep(SweepEnvironment.ForThisPc(log, new WindowsApprovalPrompt())).WithStdioServerTransport();

await builder.Build().RunAsync();
