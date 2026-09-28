# CLI reference: wgs

`wgs` is a thin command-line front end over the library. Every command reads unless it says
otherwise. The only command that writes is [`put`](#wgs-put), and it goes through the same write
gate and concurrent-change check as the library.

## Install

```console
dotnet tool install -g GamePassStorage.Tool
wgs --version
```

## Usage

```text
wgs list      <store> [--json]                     List containers in the index.
wgs diagnose  <store> [--json]                     Report store health; changes nothing.
wgs extract   <store> <container> <out-file>       Copy a container's blob to a file.
wgs backup    <store> <destination>                Copy the whole store folder.
wgs snapshot  <store> [-o <file.json>]             Fingerprint every container (SHA-256).
wgs compare   <before.json> <after.json>           Describe what changed between snapshots.
wgs put       <store> <container> <blob-file> --backup <dir> [--dry-run]
                                                   Add or replace a container's blob.
wgs help | --help | -h                             Show usage.
wgs version | --version                            Print the tool version.
```

`<store>` is a wgs folder (the one holding `containers.index`) or a folder above it, or a GUID
sub-folder of one. It is resolved with `WgsStore.ResolveContainerFolder`. Options can appear
anywhere after the command name. An unknown option is a usage error.

Close the game and the Xbox app before `put`; local writes cannot control cloud sync. See the
[safety model](/guide/safety).

## Exit codes

| Code | Meaning |
| --- | --- |
| `0` | Success. |
| `1` | Failure: the store could not be opened, a container was not found, a read or write failed or was refused, an IO error occurred, or (for `diagnose` and `put --dry-run`) writes are blocked. |
| `2` | Usage error: unknown command or option, a missing argument, or `put` without `--backup` or `--dry-run`. Also returned when `wgs` is run with no arguments (help is printed). |

Errors go to standard error as `error: ...`. `wgs compare` exits `0` whether or not it finds
differences: read its output.

## wgs list

```console
wgs list <store> [--json]
```

Prints the store path, the owning title (package family name), the index sync flags, and one line
per container: name, state, blob size, last-written time (UTC) and ETag (or `(no etag)`).

```console
$ wgs list "C:\Users\me\AppData\Local\Packages\Some.Game_abc\SystemAppData\wgs"
C:\Users\me\...\wgs\0009AB12_00000000
title: Some.Game_abc!App  sync: FullyUploaded, FullyDownloaded  containers: 2
  ForScience-WC             Synced       1,204,331 bytes  2026-06-01 10:22:41Z  "0x8DEBCCC41BE9635"
  Backup-WC                 Created         88,020 bytes  2026-06-02 08:01:03Z  (no etag)
```

With `--json` it prints an array of objects with `Name`, `State`, `Etag`, `ContainerNumber`,
`BlobSize`, `FolderName` and `LastWrittenUtc`. Enum values are written as strings.

## wgs diagnose

```console
wgs diagnose <store> [--json]
```

Reports index version, owning title, sync state, container count, and any of these sections that
are non-empty: invalid state, unsafe to build on, need repair, multi-blob (unsupported), orphaned
folders. Then it prints each write concern (`BLOCKS WRITES` or `note`) and `writable: yes` or
`writable: no`. Changes nothing.

**Exit code:** `0` when writes are allowed, `1` when the write gate would refuse. This makes it
usable as a pre-flight check in scripts:

```console
wgs diagnose <store> || echo "not safe to write"
```

`--json` prints the whole `WgsDiagnosis` record.

## wgs extract

```console
wgs extract <store> <container> <out-file>
```

Copies one container's blob to a file. Fails if the container does not exist or the read is not
`Ok` (missing blob, sync in flight, unsupported layout). It notes when the blob was read through
the previous blob id.

```console
wgs extract <store> ForScience-WC ForScience.blob
```

## wgs backup

```console
wgs backup <store> <destination>
```

Copies the whole store folder. This is the real rollback for any write. Fails if the destination
exists and is not empty.

```console
wgs backup <store> D:\backups\wgs-2026-06-01
```

## wgs snapshot

```console
wgs snapshot <store> [-o <file.json>]
```

Fingerprints every container (name, container number, state, size, SHA-256) plus the index
timestamp. Prints JSON to standard output, or writes it to `-o <file>`. An unreadable blob is
recorded with an error instead of aborting.

Take one before and one after a cloud sync to see what happened:

```console
wgs snapshot <store> -o before.json
# ... launch the game, let it sync, close it ...
wgs snapshot <store> -o after.json
wgs compare before.json after.json
```

## wgs compare

```console
wgs compare <before.json> <after.json>
```

Compares two snapshots and prints one line per difference, or `identical`:

| Line | Meaning |
| --- | --- |
| `DROPPED` | The container was removed from the index (a sync discarded it). |
| `ROLLED BACK` | The container number went backwards: reverted to an older copy. |
| `CHANGED` | The content differs. |
| `RESOLVED` | Xbox marked a `Modified` container `Synced` with the content unchanged. |
| `ADDED` | A new container appeared. |
| `index timestamp advanced` / `WENT BACKWARDS` | The index FILETIME moved. Backwards is bad news. |

## wgs put

```console
wgs put <store> <container> <blob-file> --backup <dir> [--dry-run]
```

The only command that writes. It adds a container, or replaces an existing container's blob, with
the bytes of `<blob-file>`.

| Option | Meaning |
| --- | --- |
| `--backup <dir>` | Required unless `--dry-run`. A whole-folder copy is taken here first. Fails if the folder exists and is not empty. |
| `--dry-run` | Print the plan and the write concerns and change nothing. No backup needed. |

Order of events for a real run: open the store, take the backup, then write through
`TryWriteBlob` (existing container) or `TryAddOrReplaceContainer` (new container). If the write
gate refuses, the store changed concurrently, or the write fails, nothing is written, the status
and message are printed, and the backup path is reported.

```console
# Preview first
wgs put <store> ForScience-WC edited.blob --dry-run
would write 'ForScience-WC' as container.4 (1,204,500 bytes, state Modified)
  - ...

# Then do it
wgs put <store> ForScience-WC edited.blob --backup D:\backups\before-edit
wrote 'ForScience-WC' (1,204,500 bytes) as Modified; backup at D:\backups\before-edit
```

::: warning
The blob you provide must already be in the game's own format. `wgs` treats it as opaque bytes.
:::

## Commands in development

::: info Coming soon
The following commands are being added and are not in the tool yet. This section will document
each one (arguments, options, exit codes, examples) once they land:

- discovering wgs stores on the machine,
- deleting a container,
- restoring from a backup,
- exporting and importing containers,
- process-aware write protection for `put`.
:::
