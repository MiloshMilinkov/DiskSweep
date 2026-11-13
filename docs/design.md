# DiskSweep design notes

DiskSweep is a Windows 10/11 desktop app (C# / .NET 8, WPF, MVVM) that scans one drive for clutter that is safe to remove.
**Safety is the top priority: it must never damage the OS, installed programs or user data.**

## Phases

| Phase | Scope | Status |
|---|---|---|
| 1 | Drive picker, read-only scanners for all categories, results UI | Done |
| 2 | Recycle Bin / quarantine cleanup, restore view, launching a program's own uninstaller, restore point | Not started |
| 3 | Settings file, exclusion editor, CSV export / dry-run report, polish | Not started |

## Decisions made with the owner (2026-11-13)

- **Empty folders** are *Safe* only outside Program Files, ProgramData and AppData, and only if neither the folder nor anything in it is hidden or system. Otherwise *Review*.
- **Leftovers** are never *Safe*. Confidence ≥ 70 with every check complete is *Review*; anything else is *Risky*. When a check couldn't run (no admin: some scheduled tasks, processes, other users' registry), the item is *Risky* and the gap is listed as evidence.
- **Cloud-synced folders** (OneDrive, Dropbox, Google Drive, iCloud, anything registered with the Windows Cloud Files API) are excluded from every scan and blocked by the safety guard.
- **OS and vendor folders** (`KnownSystemFolders`: Microsoft*, Windows*, Common Files, Package Cache, NVIDIA, Intel, OEM tools…) are never reported as leftovers.
- Declined: protecting known user folders (Documents, Downloads…) themselves; extra reference checks via COM / App Paths / shell extensions.

## Calls made during the build (stricter than the brief; easy to revisit)

- Drive roots, `C:\Users` (it contains profile roots), the ProgramData root, every AppData / temp root and DiskSweep's own folders are blocked. A parent of anything protected is protected.
- The **Default user profile** is protected entirely (Windows copies it for every new account).
- **Version-control data** (`.git`, `.svn`, `.hg`) is protected entirely, so repository internals and pack files are never offered.
- Empty folders are *Review* (not *Safe*) when they sit inside a dot-folder (`.claude`, `.vscode`…), inside a folder that holds `.exe`/`.dll` files, or on the system drive outside user profiles. The empty-folder scan doesn't enter OS/vendor folders at all.
- Empty-folder age uses both creation and last-write time (both older than 7 days).
- A folder changed in the last 30 days is never a leftover (something still writes to it).
- Shared components (Visual C++ redistributables, .NET runtimes, drivers, SDKs…) are left out of *Unused programs*: they're used by other programs, not opened directly.
- Any reparse point (junction, symlink, mount point, cloud placeholder, WOF-compressed file) inside a folder makes that folder unselectable.
- An `InstallLocation` that is too broad (a drive root, Program Files, a profile root…) is not used as a protected folder; the program's uninstaller files are still protected individually.
- Large files: on the system drive, the six personal folders of each profile; on other drives, the whole drive. Old files: Downloads only, default 365 days. Both always *Review*.
- Temp items: top-level entries of each temp folder. A folder is reported only if nothing inside changed in 2 days, nothing inside is open, it has no links and everything inside could be read.
- "In use" is checked by opening for DELETE access with all share modes granted, so no other program is ever blocked.

## Architecture

```
src/DiskSweep.Core      engine, no UI code
  Safety/               PathNormalizer, SafetyGuard, ProtectionLayout, SelectionPolicy, KnownSystemFolders
  IO/                   FastDirectory (FindFirstFileEx, never follows links), FolderMeasure, FileLockProbe
  SystemInfo/           profiles, installed programs, Store packages, references, processes, cloud roots, usage
  Scanning/             ScanEngine + one IScanner per category
src/DiskSweep.App       WPF + CommunityToolkit.Mvvm
tests/DiskSweep.Core.Tests
```

Every path a user could tick goes through `SelectableItem.TrySetSelected`, which re-runs `SelectionPolicy.Evaluate` → `SafetyGuard.Check` at the moment of the tick. Phase 2 cleanup must call `SafetyGuard.Check` again immediately before moving anything, and walk folder items for reparse points again.

The registry is only ever opened read-only (`RegistryReader`). Nothing in phase 1 writes to disk except the log file in `%LOCALAPPDATA%\DiskSweep\logs`.
