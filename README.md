# DiskSweep

[![CI](https://github.com/MiloshMilinkov/DiskSweep/actions/workflows/ci.yml/badge.svg)](https://github.com/MiloshMilinkov/DiskSweep/actions/workflows/ci.yml)

> **⚠️ View only — not open source.**
> This code is public for viewing only. Copying, modifying, deploying, redistributing or reusing any part of it (code, design, texts or branding), or using it to train AI models, is not allowed without written permission. See [LICENSE](LICENSE).

A Windows desktop app that scans one drive and finds clutter that is safe to remove: empty folders, leftovers of uninstalled programs, programs you haven't used in a long time, and old temp, large and downloaded files.

Safety comes first. Windows, installed programs, user profile roots, cloud-synced folders, repositories and links (junctions, symbolic links) can never be selected. Only items marked **Safe** are ticked automatically; **Review** and **Risky** items always need a manual tick.

**Status: phase 2, plus an MCP server.** These work today:

- scanning
- cleanup, to the Recycle Bin or to a per-drive quarantine you can restore from
- System Restore points
- launching a program's own uninstaller
- an MCP server, so Claude can inspect clutter and propose cleanups

Settings, exclusions and CSV export come in phase 3. See [docs/design.md](docs/design.md).

## Requirements

- Windows 10 or 11
- .NET 8 SDK (pinned in `global.json`)

## Build, test, run

```bash
dotnet build
dotnet test
dotnet run --project src/DiskSweep.App
```

The app runs without administrator rights. Use **Restart as administrator** in the app to include Windows\Temp, Prefetch, every scheduled task and other users' folders.

Logs: `%LOCALAPPDATA%\DiskSweep\logs\disksweep-YYYY-MM-DD.log`

## Use with Claude

`src/DiskSweep.Mcp` is a small [MCP](https://modelcontextprotocol.io) server. It lets Claude Code or Claude Desktop scan for clutter, explain what DiskSweep found, and propose a cleanup. The agent never decides on its own what moves: a person approves every move in a Windows dialog the server shows, and everything moved can be restored.

<!-- Demo GIF goes here: docs/media/mcp-demo.gif -->
*Demo GIF coming soon: Claude proposes a plan for Downloads, the approval dialog, and restoring from quarantine.*

Build it once. A separate publish folder means your dev builds don't hit a file lock while a client has the server running.

```powershell
dotnet publish src/DiskSweep.Mcp -c Release -o publish/DiskSweep.Mcp
```

**Claude Code:**

```powershell
claude mcp add --scope user disksweep -- "<repo>\publish\DiskSweep.Mcp\DiskSweep.Mcp.exe"
```

**Claude Desktop:** add this to `%APPDATA%\Claude\claude_desktop_config.json`, then restart Claude Desktop.

```json
{
  "mcpServers": {
    "disksweep": {
      "command": "C:\\path\\to\\repo\\publish\\DiskSweep.Mcp\\DiskSweep.Mcp.exe"
    }
  }
}
```

Then ask, for example, "What's taking up space on C:?" or "Clean my Downloads".

| The agent can | The agent can't |
|---|---|
| Scan a drive or a folder (read-only) | Delete anything, or use the Recycle Bin |
| List what DiskSweep found, with risk level and reason | Pass a path to a tool that changes anything: write tools take only ids the server issued |
| Explain each safety check an item passes or fails | Plan Risky items, programs, registry entries, or anything the safety guard blocks |
| Propose a plan of Safe and Review files and folders | Move anything without a person clicking **Yes** in the Windows dialog |
| Move an approved plan into DiskSweep's quarantine | Use a plan twice, after 10 minutes, or from another session |
| List quarantine and restore items, never overwriting | Plan more than 100 items or 10 GB at once, or use a scan older than 60 minutes |
| | Run uninstallers, create restore points, or touch the registry |

The server checks every limit in its own code, not just in the tool descriptions. A test also reads the compiled server and fails if it ever references an uninstaller, the Recycle Bin, the registry, or a delete call. Hand-run scenarios that try to get around these rules are in [evals/](evals/).

## How it was built

DiskSweep is built with Claude Code, following a written workflow:

- the project instructions in [CLAUDE.md](CLAUDE.md)
- the design decisions in [docs/design.md](docs/design.md)
- for the MCP server, a [spec](docs/superpowers/specs/2026-11-26-mcp-server-design.md) and an [implementation plan](docs/superpowers/plans/2026-11-26-mcp-server.md) that ends with its review results

The general workflow is described in [claude-workflow](https://github.com/MiloshMilinkov/claude-workflow).

## License

Copyright © 2026 Miloš Milinkov. All rights reserved.

This project is proprietary and **not** open source. No licence is granted to use, copy, modify, deploy or distribute it. See [LICENSE](LICENSE) for the full terms. For permission requests, contact milos300399@gmail.com.

Security issues: see [SECURITY.md](SECURITY.md).
