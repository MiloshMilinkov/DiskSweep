# DiskSweep

> **⚠️ View only — not open source.**
> This code is public for viewing only. Copying, modifying, deploying, redistributing or reusing any part of it (code, design, texts or branding), or using it to train AI models, is not allowed without written permission. See [LICENSE](LICENSE).

A Windows desktop app that scans one drive and finds clutter that is safe to remove: empty folders, leftovers of uninstalled programs, programs you haven't used in a long time, and old temp, large and downloaded files.

Safety comes first. Windows, installed programs, user profile roots, cloud-synced folders, repositories and links (junctions, symbolic links) can never be selected. Only items marked **Safe** are ticked automatically; **Review** and **Risky** items always need a manual tick.

**Status: phase 2.** Scanning, cleanup (Recycle Bin or a per-drive quarantine you can restore from), System Restore points and launching a program's own uninstaller work. Settings, exclusions and CSV export come in phase 3. See [docs/design.md](docs/design.md).

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

## License

Copyright © 2026 Miloš Milinkov. All rights reserved.

This project is proprietary and **not** open source. No licence is granted to use, copy, modify, deploy or distribute it. See [LICENSE](LICENSE) for the full terms. For permission requests, contact milos300399@gmail.com.

Security issues: see [SECURITY.md](SECURITY.md).
