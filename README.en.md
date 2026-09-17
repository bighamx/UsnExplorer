# USN Journal Explorer

A viewer for the NTFS USN change journal. To find out what touched which files on a volume, and when.

[English](README.en.md) · [中文](README.md)

![USN Journal Explorer](docs/screenshot.png)

Windows 10/11 · .NET 8 · WPF · requires Administrator

## The USN journal

NTFS keeps a per-volume change log at `C:\$Extend\$UsnJrnl:$J`. Every create, delete, rename, write and attribute change on that volume is recorded there.

That file cannot be opened through the normal file API, and it is not just a permissions matter — running as Administrator with `SeBackupPrivilege` and `SeRestorePrivilege` enabled still gets access denied. No path form works:

```
C:\$Extend\$UsnJrnl:$J
\\?\C:\$Extend\$UsnJrnl:$J
\\.\C:\$Extend\$UsnJrnl:$J
\\?\Volume{guid}\$Extend\$UsnJrnl:$J
```

The only way in is to open a volume handle and read it with FSCTL:

```
FSCTL_QUERY_USN_JOURNAL   0x000900F4   journal config
FSCTL_READ_USN_JOURNAL    0x000900BB   change records
FSCTL_ENUM_USN_DATA       0x000900B3   enumerate MFT entries
```

## Paths are reconstructed

A USN record holds a file reference number, a parent reference number, and the file name *as it was at the time of the change* — no path. And that name is a snapshot: if the file was renamed later, the record keeps the old name.

So the full path has to be rebuilt. The tool enumerates the whole volume's MFT into a "file id → parent + name" table, then walks each record up to the root. If an intermediate directory was deleted, the path breaks there and the UI says so rather than inventing one.

## Running it

```
publish\UsnExplorer.exe
```

It asks for Administrator at startup, then scans every NTFS volume it finds.

Build from source (needs the .NET 8 SDK):

```
build.cmd
```

Output is `publish\UsnExplorer.exe`, a single 270 KB file that uses the installed .NET 8 desktop runtime.

Pass `--selftest` on the command line to run the engine check without the UI — it prints journal config, record count, timing and path-resolution rate.

## Features

- Discovers NTFS volumes automatically and reads each journal
- Separate filename and path keyword filters, case-insensitive, applied as you type
- Date plus time-of-day range to the second, with quick presets
- Filter by operation type: create / delete / rename / write / attributes / security / stream / hard link / transacted / close
- Filter by volume, and exclude directories
- Click any column header to sort; the UI stays responsive while it runs
- Detail panel with the full path, local and UTC time, MFT record numbers, raw reason bitmap
- Export the current filtered set to CSV
- Shortcuts: `F5` scan · `Ctrl+F` filename · `Ctrl+Shift+F` path · `Ctrl+E` export · `Esc` cancel · double-click a row to copy its path

## Performance

Measured locally (NVMe, 2.64M records and 1.38M MFT entries):

```
journal read   2,644,894 records   886 ms
MFT enumerate  1,377,396 entries   2.3 s
path resolution 100%               (2000-record sample)
filter (full set)                  < 400 ms
sort by time                       ~200 ms
```

Records are held in memory — about 450 MB for 2.6M. At this scale there is no paging and no on-disk index, which is what buys latency-free filtering.

## Limits

- NTFS only. exFAT and FAT32 have no USN journal.
- The journal is a ring buffer. Once it reaches `MaxSize` (commonly 256 MB) the oldest records are overwritten, so only recent history exists. Clearing it discards all history irrecoverably and forces consumers like Everything into a full rescan.
- The file's reported logical length stays large (~2.5 GB) while actual allocation stays around 262 MB. It is sparse and only ever grows logically; the real disk cost is the 256 MB cap plus one growth step.
- Paths whose ancestor directories were deleted cannot be restored, and show as `<deleted>`.
- Filtered output above 20M rows is capped, and the UI says so — nothing is dropped silently.
- Read-only. It never modifies or clears the journal.

## License

[Selective Freedom License (SFL) v1.0](LICENSE) — [中文版](LICENSE-CN.md)

MIT-style grant with an exclusion clause: no license is granted to Huawei Technologies Co., Ltd. or its affiliates, subsidiaries, employees, representatives or contractors. See [LICENSE](LICENSE) for the full terms.

## Gotchas

The places this goes wrong quietly, if you are writing one yourself:

- `READ_USN_JOURNAL_DATA_V0` is 40 bytes (`<QIIQQQ>`), not 48. The wrong size returns zero bytes of output and no records.
- The `USN_RECORD_V2` header is 60 bytes (`<IHHQQQQIIIIHH>`). The version fields are 2 bytes each; write them as 4 and every field after is misaligned.
- Timestamps are FILETIME, counted from 1601. `DateTime.Ticks` counts from 0001. Using one as the other shifts every timestamp by 1600 years and still yields a plausible date.
- `TOKEN_PRIVILEGES` is 16 bytes. `LUID` is 8 bytes but 4-byte aligned, so declaring it as an 8-byte integer computes 24 and privilege enablement fails silently.
