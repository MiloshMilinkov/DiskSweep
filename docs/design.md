# DiskSweep design notes

DiskSweep is a Windows 10/11 desktop app (C# / .NET 8, WPF, MVVM) that scans one drive for clutter that is safe to remove.
**Safety is the top priority: it must never damage the OS, installed programs or user data.**

## Phases

| Phase | Scope | Status |
|---|---|---|
| 1 | Drive picker, read-only scanners for all categories, results UI | Done |
| 2 | Recycle Bin / quarantine cleanup, restore view, launching a program's own uninstaller, restore point | Done |
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

## Phase 2 decisions (2026-11-14)

Made with the owner:

- **Quarantine** lives in `<drive>:\DiskSweep Quarantine` on the item's own drive, so a move is a rename (no copy, no extra space, permissions unchanged). One sub-folder per cleanup run, one numbered slot per item, and an append-only `journal.jsonl` recording each original path; a line is appended before and after every move (the last line per slot wins, a line cut short by a crash is ignored). `SafetyGuard` protects a top-level `DiskSweep Quarantine` folder on every drive, so it's never scanned or offered, and DiskSweep refuses to write into it if it has been replaced by a link.
- **Recycle Bin limits**: items Windows would delete permanently instead of recycling (removable drives, paths of 260+ characters, a Bin set to delete immediately, items bigger than the Bin's size limit, or 5% of the drive when no limit is recorded) are skipped and listed, never deleted. The Bin's current fill level plus everything recycled earlier in the run is counted too, so Windows never has to purge older Bin items to make room. `FOF_WANTNUKEWARNING` is set as a backstop.
- **Restore point** is offered (admin only) before cleaning; Windows' result is verified by listing restore points. If none is created, the user is warned and chooses to continue or cancel.
- **Uninstallers**: only the program's interactive `UninstallString` is launched, never `QuietUninstallString`; programs with only a silent uninstaller can't be ticked.
- **Bulk ticking** (2026-11-15): each results tab has separate "Tick all" boxes for Safe and Review (all / some / none), plus "Untick all". Risky items are always ticked one at a time (`SelectionPolicy.AllowsBulkTick`). Programs are included: "Tick all Review" in Unused programs queues each program's own interactive uninstaller. Every row still goes through `SelectableItem.TrySetSelected`.

Calls made during the build:

- Right before every move, `CleanupService.CheckBeforeMove` re-runs the selection policy and safety guard, re-walks folders for links, cloud files and unreadable parts, refuses anything that changed since the scan (newer timestamp or different size; empty folders that gained files), and probes files for being open.
- Restore never overwrites: if something exists at the original path, the item comes back as "name (restored)". It refuses when a parent folder on the way back is now a link or cloud folder, or the drive letter changed. Restoring and recycling from quarantine re-walk the item for links first.
- The journal is an ordinary file anyone can edit, so it's never trusted: a stored path that leaves the batch folder is ignored, and restore refuses original locations inside Windows (except Windows\Temp) or any System32/SysWOW64/WinSxS folder.
- An entry still marked Pending whose item is in the quarantine folder (the app stopped right after the move) can be restored like any other.
- Emptying quarantine means "Move to Recycle Bin" (with the same Bin checks). DiskSweep never deletes user items permanently.
- The uninstall command is re-read from the registry right before launch and must match the scan. Unquoted paths use the longest existing `.exe` prefix; bare names (msiexec.exe) resolve only in System32. Uninstallers run one at a time; afterwards the registry entry is checked to report whether the program is gone.
- Items that weren't cleaned are unticked and listed at the top of the Skipped tab with the reason.

## Architecture

```
src/DiskSweep.Core      engine, no UI code
  Safety/               PathNormalizer, SafetyGuard, ProtectionLayout, SelectionPolicy, KnownSystemFolders
  IO/                   FastDirectory (FindFirstFileEx, never follows links), FolderMeasure, FileLockProbe
  SystemInfo/           profiles, installed programs, Store packages, references, processes, cloud roots, usage
  Scanning/             ScanEngine + one IScanner per category
  Cleanup/              CleanupService, QuarantineStore/Restorer, WindowsRecycleBin, RestorePoint, UninstallLauncher
src/DiskSweep.App       WPF + CommunityToolkit.Mvvm
tests/DiskSweep.Core.Tests
```

Every path a user could tick goes through `SelectableItem.TrySetSelected`, which re-runs `SelectionPolicy.Evaluate` → `SafetyGuard.Check` at the moment of the tick. `CleanupService` runs the same checks again immediately before each move and re-walks folders for reparse points.

The registry is only ever opened read-only (`RegistryReader`). Disk writes are limited to the log in `%LOCALAPPDATA%\DiskSweep\logs`, the quarantine folders, and the moves the user confirms.
