# DiskSweep

Windows desktop app (C# / .NET 8, WPF, MVVM via CommunityToolkit.Mvvm) that finds safe-to-remove clutter on one drive.
**Safety is the top priority**: never damage the OS, installed programs or user data. Ask the owner before any assumption that affects safety.

Proprietary (all rights reserved). Design decisions and phase status: `docs/design.md`.

## Commands

```bash
dotnet build                                  # whole solution (SDK 8 pinned by global.json)
dotnet test                                   # xUnit, tests/DiskSweep.Core.Tests
dotnet run --project src/DiskSweep.App        # launch the app
dotnet test --filter "FullyQualifiedName~SafetyGuard"   # just the safety rules
```

No lint step beyond compiler warnings (keep them at 0). No deploy yet.

## Layout

- `src/DiskSweep.Core` — engine class library. **No UI code here.**
  - `Safety/` — `SafetyGuard` (the single gate for every selectable/removable path), `PathNormalizer`, `ProtectionLayout`, `SelectionPolicy`/`SelectableItem`, `KnownSystemFolders`.
  - `IO/` — `FastDirectory` (FindFirstFileEx; never follows reparse points), `FolderMeasure`, `FileLockProbe`, long-path helpers.
  - `SystemInfo/` — read-only readers: profiles, installed programs, Store packages, services/tasks/startup references, processes, cloud roots, usage (Prefetch, UserAssist).
  - `Scanning/` — `ScanEngine` plus one `IScanner` per category.
  - `Cleanup/` — `CleanupService` (re-checks every item right before it moves), `QuarantineStore`/`QuarantineRestorer`, `WindowsRecycleBin` + `RecycleBinPolicy`, `RestorePoint` (WMI), `UninstallLauncher`.
- `src/DiskSweep.App` — WPF UI (`MainWindow.xaml`, `ViewModels/`, `Themes/Theme.xaml`).
- `tests/DiskSweep.Core.Tests` — safety-layer tests (synthetic drive `Q:` via `TestLayouts`, real junctions via `TempTree`) and scanner tests.

## Rules

- Phase order: 1 read-only scan (done) → 2 cleanup/restore/uninstaller launch (done) → 3 settings/exclusions/CSV. Show the owner each phase before starting the next.
- Tests must never touch the user's real Recycle Bin or create `X:\DiskSweep Quarantine`: use `FakeRecycleBin` and a `QuarantineStore` rooted in a `TempTree` (see `CleanupTests`).
- Never launch a real uninstaller or create a restore point while testing.
- Never delete permanently; phase 2 moves to Recycle Bin or quarantine only. Re-run `SafetyGuard.Check` and re-walk folders for reparse points immediately before any move.
- Never edit the registry: open keys only through `RegistryReader` (read-only).
- Only `RiskLevel.Safe` items may be pre-selected. Leftovers are never Safe.
- Program removal may only launch the program's own `UninstallString` / `QuietUninstallString`.
- New protections go in `SafetyGuard` with a test in `SafetyGuardTests`; never weaken an existing rule without the owner's OK.
- Runs as `asInvoker`; admin is optional ("Restart as administrator").

## Gotchas

- `global.json` pins SDK 8.0.4xx; the machine also has SDK 10.
- Close any running DiskSweep before `dotnet build`, or copying `DiskSweep.Core.dll` fails (MSB3027 file lock).
- XAML `Style.Triggers` must come after all `Setter`s or the build fails with MC3088.
- DataGrid columns aren't in the visual tree: bind their `Visibility` through the `BindingProxy` resource.
- Last-access times on .exe files are often refreshed by antivirus scans, so they only ever make programs look *more* used (safe direction).
- The app can be driven for checks with UI Automation (button names = their text, e.g. "Scan C:") and screenshotted with `PrintWindow`.
