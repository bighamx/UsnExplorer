# USN Journal Explorer

[English](README.en.md) · [中文](README.md)

A viewer and search tool for the NTFS USN (Update Sequence Number) change journal — it answers *"what touched which files on this volume, and when?"*

![USN Journal Explorer](docs/screenshot.png)

Windows 10/11 · .NET 8 · WPF · requires Administrator

---

## What it reads

NTFS keeps a per-volume change journal at `C:\$Extend\$UsnJrnl:$J`, recording every create, delete, rename, write and attribute change. This tool reads it and presents it as a searchable table.

**That file cannot be opened through the normal file API.** The filesystem layer blocks it — an elevated process with `SeBackupPrivilege` *and* `SeRestorePrivilege` still gets `ERROR_ACCESS_DENIED`. Every path form fails the same way:

```
C:\$Extend\$UsnJrnl:$J
\\?\C:\$Extend\$UsnJrnl:$J
\\.\C:\$Extend\$UsnJrnl:$J
\\?\Volume{guid}\$Extend\$UsnJrnl:$J
```

`icacls` cannot even parse that path, while `GetFileAttributesEx` succeeds and reports the sparse logical length — a good sign the block is at the filesystem layer, not the DACL.

The only way in is a volume handle plus `FSCTL`:

```
CreateFileW("\\.\C:", GENERIC_READ, ...)
FSCTL_QUERY_USN_JOURNAL  0x000900F4   read journal config
FSCTL_READ_USN_JOURNAL   0x000900BB   stream the change records
FSCTL_ENUM_USN_DATA      0x000900B3   enumerate every MFT entry
```

That is what this tool does. It also **reconstructs full paths**: a USN record carries only a file reference number, a parent reference number and the name *as it was at change time* — no path. Paths are recovered by enumerating the MFT into a `FileId → (ParentId, Name)` map and walking parents up to the root.

## Running it

```
publish\UsnExplorer.exe
```

It requests Administrator via UAC — required for the volume handle. On launch it discovers NTFS volumes and scans them.

Build from source (needs the .NET 8 SDK):

```
build.cmd
```

Output is `publish\UsnExplorer.exe` — a single 270 KB file that uses the installed .NET 8 desktop runtime. The script also runs the engine self-test as its last step.

## Features

- **Volume scan** — auto-discovers NTFS volumes and reads each journal
- **Full path reconstruction** — via MFT enumeration, with cycle protection and visible `<deleted>` placeholders when an ancestor directory is already gone
- **Filtering**
  - Filename keyword and path keyword as two independent channels (case-insensitive, live as you type)
  - Date + time range to the second, with quick presets; pre-filled with the real data range after a scan
  - Operation type: create / delete / rename / write / attributes / security / stream / hard link / transacted / close
  - Volume, and an "exclude directories" toggle
- **Sorting** — click any column header; sorts off the UI thread and keeps the table live while it runs
- **Detail panel** — full path, local + UTC timestamps, MFT record numbers, raw reason bitmap
- **CSV export** of the current filtered view (RFC 4180 escaping)
- **Shortcuts** — `F5` scan · `Ctrl+F` filename · `Ctrl+Shift+F` path · `Ctrl+E` export · `Esc` cancel · double-click a row to copy its path

## Performance

Measured on an NVMe-backed volume, 2.64M records and 1.38M MFT entries:

```
journal read    2,644,894 records  /  886 ms   (~3M records/sec)
MFT enumeration 1,377,396 entries  /  2.3 s
path resolution 100%              (2000-record sample)
filter (full set)                  < 400 ms
sort by time                        ~200 ms
```

Records are held in memory (~450 MB per 2.6M). There is no paging or on-disk index — at this scale full memory residency buys zero-latency filtering.

Two things make the difference at million-row scale:

- Records are a `List<T>` of **structs**, and row view-models are materialised lazily by the `DataGrid`'s `IList` indexer, so 3M rows never becomes 3M heap objects.
- Sorting is intercepted and done on the **int index array** only. The built-in `DataGrid` sort path goes through `ListCollectionView`, which enumerates every item — with a lazily-materialising indexer that means millions of objects plus reflection, and it hangs the UI. Sorting off-thread on indices keeps the UI responsive (measured: window never reports hung, 276 ms to re-sort 3M rows by filename).

## Architecture

```
Interop/NativeMethods.cs      P/Invoke: CreateFileW, DeviceIoControl, privilege
                              enablement, and the packed struct definitions
Core/UsnReader.cs             FSCTL wrappers: QUERY / READ_USN_JOURNAL / ENUM_USN_DATA
Core/UsnEntry.cs              compact struct record representation
Core/PathResolver.cs          MFT-map path walk, cached, cycle-guarded
Core/UsnFilter.cs             filter model + operation-type grouping
Core/UsnStore.cs              record storage, filtering, index sorting (double-buffered)
ViewModels/UsnRow.cs          on-demand IList row materialisation
ViewModels/MainViewModel.cs   scan orchestration, state, export
MainWindow.xaml               UI
Themes/Dark.xaml              dark theme (full control templates)
SelfTest.cs                   headless engine verification
```

## Headless self-test

Interop bugs show up as **"silently returned 0 records"**, which is indistinguishable from an empty journal when you are looking at a UI. The self-test prints journal config, record count, elapsed time, time span, reason-bit distribution, MFT entry count and path-resolution hit rate:

```
USN_SELFTEST_OUT=%TEMP%\out.txt UsnExplorer.exe --selftest [drive]
```

It also verifies the engine against independently recomputed expectations: 12 filter cases and 9 sort cases. That is what caught a real bug where descending folder sorts were returning 883 of 2999 rows out of order — the sort was permuting its own cached key array in place.

`build.cmd` runs it automatically.

## Known limits

- NTFS only. exFAT and FAT32 have no USN journal.
- The journal is a **ring buffer**: once it reaches `MaxSize` (commonly 256 MB) the oldest records are overwritten, so only recent history exists. Clearing it (`fsutil usn deletejournal /N X:`) discards all history irrecoverably and forces a full rescan for consumers like Everything — not a housekeeping task.
- The file's reported logical length stays huge (~2.5 GB) while actual allocation stays at the cap (~262 MB). It is a sparse file that only grows logically; the real disk cost is `MaxSize` + one `AllocationDelta`.
- Path reconstruction depends on the MFT's *current* state. If an ancestor directory was deleted, that path cannot be restored and is shown as `<deleted>`.
- Filtered output above 20M rows is capped, and the UI states when the cap is hit — results are never truncated silently.
- Read-only. It never modifies or clears the journal.

## Implementation notes

The details that cost the most time to get right, in case you are writing this yourself:

- `READ_USN_JOURNAL_DATA_V0` is exactly **40 bytes** (`<QIIQQQ>`), not 48 — two `DWORD`s sit between `DWORDLONG`s. Wrong size returns `ERROR_INVALID_PARAMETER` with zero output bytes.
- The `USN_RECORD_V2` header is exactly **60 bytes** (`<IHHQQQQIIIIHH>`). Major/minor version are `WORD`, not `DWORD`; getting this wrong silently misaligns every field after it.
- `TimeStamp` is a **FILETIME** (100 ns since 1601), *not* `DateTime.Ticks` (100 ns since 0001). Feeding it to `new DateTime(ticks)` shifts everything by exactly 1600 years and still produces a plausible-looking date.
- `TOKEN_PRIVILEGES` is **16 bytes, not 24**: `LUID` is 8 bytes but 4-byte aligned. Declaring it as `long` computes 24 and `AdjustTokenPrivileges` fails silently with error 1300. It also returns `true` when the privilege is absent, so check `GetLastWin32Error() != 1300`.
- `Array.Sort(keys, indices)` sorts `keys` **in place** — never pass a cached array you intend to reuse.
