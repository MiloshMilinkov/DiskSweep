# DiskSweep

Windows desktop app (C# / .NET 8, WPF, MVVM via CommunityToolkit.Mvvm) that finds safe-to-remove clutter on one drive.
**Safety is the top priority**: never damage the OS, installed programs or user data. Ask the owner before any assumption that affects safety.

Proprietary (all rights reserved). Design decisions and phase status: `docs/design.md`.

## Commands

```bash
dotnet build                                  # whole solution (SDK 8 pinned by global.json)
dotnet test                                   # xUnit: tests/DiskSweep.Core.Tests and tests/DiskSweep.Mcp.Tests
dotnet run --project src/DiskSweep.App        # launch the app
dotnet test --filter "FullyQualifiedName~SafetyGuard"   # just the safety rules
dotnet test tests/DiskSweep.Mcp.Tests          # just the MCP server
dotnet publish src/DiskSweep.Mcp -c Release -o publish/DiskSweep.Mcp   # MCP server exe for clients
claude mcp add --scope user disksweep -- "<repo>\publish\DiskSweep.Mcp\DiskSweep.Mcp.exe"
```

MCP evals are run by hand: `evals/README.md`.

No lint step beyond compiler warnings (keep them at 0). No deploy yet.

## Layout

- `src/DiskSweep.Core` — engine class library. **No UI code here.**
  - `Safety/` — `SafetyGuard` (the single gate for every selectable/removable path), `PathNormalizer`, `ProtectionLayout`, `SelectionPolicy`/`SelectableItem`, `KnownSystemFolders`.
  - `IO/` — `FastDirectory` (FindFirstFileEx; never follows reparse points), `FolderMeasure`, `FileLockProbe`, long-path helpers.
  - `SystemInfo/` — read-only readers: profiles, installed programs, Store packages, services/tasks/startup references, processes, cloud roots, usage (Prefetch, UserAssist).
  - `Scanning/` — `ScanEngine` plus one `IScanner` per category.
  - `Cleanup/` — `CleanupService` (re-checks every item right before it moves), `QuarantineStore`/`QuarantineRestorer`, `WindowsRecycleBin` + `RecycleBinPolicy`, `RestorePoint` (WMI), `UninstallLauncher`.
- `src/DiskSweep.App` — WPF UI (`MainWindow.xaml`, `ViewModels/`, `Themes/Theme.xaml`).
- `src/DiskSweep.Mcp` — stdio MCP server (ModelContextProtocol SDK 2.4). Thin `Tools/` over `SweepSession` (scans, ids), `Plans/` (`CleanupPlanner`, `PlanStore`, `PlanExecutor`), `QuarantineBrowser`. Everything machine-facing is in `SweepEnvironment`.
- `tests/DiskSweep.Core.Tests` — safety-layer tests (synthetic drive `Q:` via `TestLayouts`, real junctions via `TempTree`) and scanner tests.
- `tests/DiskSweep.Mcp.Tests` — tool and guardrail tests on `McpTestBed` (links `TempTree`/`TestLayouts`), the protocol test, and `NoForbiddenApisTests`.

## Rules

- Phase order: 1 read-only scan (done) → 2 cleanup/restore/uninstaller launch (done) → 3 settings/exclusions/CSV. Show the owner each phase before starting the next.
- Tests must never touch the user's real Recycle Bin or create `X:\DiskSweep Quarantine`: use `FakeRecycleBin` and a `QuarantineStore` rooted in a `TempTree` (see `CleanupTests`).
- Never launch a real uninstaller or create a restore point while testing.
- Never delete permanently; phase 2 moves to Recycle Bin or quarantine only. Re-run `SafetyGuard.Check` and re-walk folders for reparse points immediately before any move.
- Never edit the registry: open keys only through `RegistryReader` (read-only).
- Only `RiskLevel.Safe` items may be pre-selected. Leftovers are never Safe.
- Bulk "Tick all" covers Safe and Review only (`SelectionPolicy.AllowsBulkTick`); Risky items are ticked one at a time.
- Program removal may only launch the program's own `UninstallString` / `QuietUninstallString`.
- New protections go in `SafetyGuard` with a test in `SafetyGuardTests`; never weaken an existing rule without the owner's OK.
- Runs as `asInvoker`; admin is optional ("Restart as administrator").

## MCP server rules

- No scanning, safety or move logic in `DiskSweep.Mcp`: reuse Core (`ScanEngine`, `SafetyGuard`, `CleanupService.Preflight`/`Run`, `QuarantineStore`/`QuarantineRestorer`).
- Write tools take only ids the server issued (item ids of the latest scan, plan ids, quarantine ids), never paths.
- Plans: Safe/Review files and folders only (`PlanItemRule`), at most 100 items / 10 GB, expire after 10 min, single use, scans older than 60 min refused.
- `quarantine_plan` moves only after a person clicks Yes in `WindowsApprovalPrompt` (default No). Destination is always quarantine; every `CleanupService`/`QuarantineRestorer` gets `RefusingRecycleBin`.
- No uninstallers, restore points, registry or deletes: `NoForbiddenApisTests` reads the compiled assembly and must stay green.
- Every tool description ends with a `Can't:` sentence; limits are enforced in code, never only in descriptions.
- Tests use `McpTestBed`/`FakeApproval`: never show a dialog, scan a real drive or touch real quarantine.

## Gotchas

- `global.json` pins SDK 8.0.4xx; the machine also has SDK 10.
- Close any running DiskSweep before `dotnet build`, or copying `DiskSweep.Core.dll` fails (MSB3027 file lock). A client running the MCP server locks `publish\DiskSweep.Mcp`: close it (or `/mcp` → disable) before republishing.
- `%TEMP%` here is an 8.3 short path (`C:\Users\OPTIMU~1\…`); compare paths through `PathNormalizer`/`SweepSession.Normalized`, never raw strings.
- The MCP server writes only JSON-RPC to stdout; logs go to stderr and `%LOCALAPPDATA%\DiskSweep\logs\mcp\`. Smoke-test it by piping JSON-RPC lines into the published exe.
- XAML `Style.Triggers` must come after all `Setter`s or the build fails with MC3088.
- DataGrid columns aren't in the visual tree: bind their `Visibility` through the `BindingProxy` resource.
- Last-access times on .exe files are often refreshed by antivirus scans, so they only ever make programs look *more* used (safe direction).
- The app can be driven for checks with UI Automation (button names = their text, e.g. "Scan C:") and screenshotted with `PrintWindow`. Results-tab content is reachable only because the TabControl template names its presenter `PART_SelectedContentHost` (row boxes are "Select item", plus "Tick all Safe items", "Tick all Review items", "Untick all"). WPF ignores posted mouse messages, so use UIA patterns, not `PostMessage` clicks. Call `SetThreadDpiAwarenessContext(-4)` before `PrintWindow`, or the shot is cropped at display scaling above 100%.

## CI

`.github/workflows/ci.yml` calls the shared `dotnet-ci.yml` in `MiloshMilinkov/.github` (Windows runner, SDK from `global.json`): restore, build, test, plus a gitleaks scan. Check runs with `gh run list -L 3`.
