# CLI reference: wgs

`wgs` is a thin command-line front end over the library. Every command reads unless it says
otherwise. The commands that write are [`put`](#wgs-put), [`delete`](#wgs-delete),
[`restore`](#wgs-restore) and [`import`](#wgs-import). Each one needs `--backup <dir>` (or
`--dry-run`) and goes through the same write gate and concurrent-change check as the library.

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
wgs find      [--package <text>] [--exact] [--json]
                                                   Find wgs stores on this machine.
wgs blobs     <store> <container> [--json]         List the blobs inside a container.
wgs delete    <store> <container> --backup <dir> [--dry-run]
                                                   Delete a container (never-uploaded: removed;
                                                   known to the cloud: kept as a Deleted tombstone).
wgs restore   <store> <backup-folder> --backup <dir> [--dry-run]
                                                   Restore a `wgs backup` over the store; the
                                                   current store is copied to <dir> first.
wgs adapters  [--json]                             List the game adapters in use.
wgs inspect   <store> [<container>] [--json]       Describe what containers hold, using the
                                                   matching game adapter (generic if none).
wgs export    <store> <out-folder>                 Write every container's blobs and a manifest.
wgs import    <store> <folder> --backup <dir> [--dry-run]
                                                   Add or replace containers from an export folder.
wgs help | --help | -h                             Show usage.
wgs version | --version                            Print the tool version.
```

`<store>` is a wgs folder (the one holding `containers.index`) or a folder above it, or a GUID
sub-folder of one. It is resolved with `WgsStore.ResolveContainerFolder`. Options can appear
anywhere after the command name. An unknown option is a usage error.

Close the game and the Xbox app before any write; local writes cannot control cloud sync. See the
[safety model](/guide/safety).

### Global options

| Option | Meaning |
| --- | --- |
| `--adapters <dir>` | Load extra [game adapters](/guide/adapters) from this folder. Repeatable. The `WGS_ADAPTERS_DIR` environment variable names more folders (separated by `;` on Windows, `:` elsewhere). Adapters run with full trust. |
| `--no-builtin-adapters` | Do not register the adapters that ship with the tool, so every store is handled by the generic model. Plugin folders still load. |
| `--refuse-if-running <name[,name]>` | On `put`, `delete`, `restore` and `import`: refuse while a process with one of these names runs (case-insensitive, `.exe` optional, a trailing `*` matches a prefix). Added to the structural gate and to any adapter gate. |

A problem loading an adapter is printed as `warning: adapter: ...` and never stops the command.

## Exit codes

| Code | Meaning |
| --- | --- |
| `0` | Success. |
| `1` | Failure: the store could not be opened, a container was not found, a read or write failed or was refused, an IO error occurred, or (for `diagnose` and the `--dry-run` forms) writes are blocked. |
| `2` | Usage error: unknown command or option, a missing argument, or a write command without `--backup` or `--dry-run`. Also returned when `wgs` is run with no arguments (help is printed). |

Errors go to standard error as `error: ...`. `wgs compare` exits `0` whether or not it finds
differences: read its output.

## wgs list

```console
wgs list <store> [--json]
```

Prints the store path, the owning title (package family name), the index sync flags, and one line
per container: name, state, blob size, last-written time (UTC) and ETag (or `(no etag)`). An
`adapter:` line names the [game adapter](/guide/adapters) that matched the store, or `generic` when
none did.

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
are non-empty: invalid state, unsafe to build on, need repair, multi-blob (informational: these
containers are supported, see [`wgs blobs`](#wgs-blobs)), malformed manifest (damaged), orphaned
folders. It also prints which adapter matched. Then it prints each write concern (`BLOCKS WRITES` or `note`) and `writable: yes` or
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
`Ok` (missing blob, sync in flight, a malformed manifest). A container holding several blobs is
not a single blob: use [`wgs export`](#wgs-export) or read the blobs with the library. It notes when the blob was read through
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
wgs put <store> <container> <blob-file> --backup <dir> [--blob <name>] [--dry-run]
```

Adds a container, or replaces an existing container's blob, with the bytes of `<blob-file>`. A
container that holds several blobs is refused rather than have its other blobs dropped; use
[`wgs import`](#wgs-import) for those.

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

## wgs find

```console
wgs find [--package <text>] [--exact] [--json] [--local-app-data <dir>] [--drive-root <dir>]
```

Finds wgs stores on this machine without knowing the title: under
`%LOCALAPPDATA%\Packages\<PackageFamilyName>\SystemAppData\wgs\<XUID>_<SCID>` and
`<drive>:\XboxGames\GameSave\wgs\...`. It prints the package family name, XUID, SCID and store
path of each. `--package` filters by package family name (a case-insensitive substring, or the
whole name with `--exact`). Where a root does not exist it finds nothing and says
`no wgs stores found`; that is not an error. `--local-app-data` and `--drive-root` search other
roots. See [`WgsStoreDiscovery`](/api/discovery).

```console
$ wgs find --package abiotic
PlayStack.AbioticFactor_3wcqaesafpzfy  xuid 2535466000000000  scid 00000000-0000-0000-0000-000000000000  [LocalAppData]
  C:\Users\me\AppData\Local\Packages\PlayStack.AbioticFactor_3wcqaesafpzfy\SystemAppData\wgs\2535466000000000_00000000000000000000000000000000
```

## wgs blobs

```console
wgs blobs <store> <container> [--json]
```

Lists the blobs a container's manifest names: name, size on disk, blob id, and `(sync in flight)`
when the cloud id and the disk id differ. Most containers hold one blob named `Data`; some titles
keep several per container. Fails on a malformed manifest.

## wgs delete

```console
wgs delete <store> <container> --backup <dir> [--dry-run]
```

Deletes a container by one rule (see [the format reference](/wgs-format#deleting-a-container)):

- a container that was **never uploaded** (state `Created`, no ETag) has nothing to tell the cloud,
  so its index entry is removed and its files are cleared;
- a container **the cloud knows** (it has an ETag) becomes a `Deleted` tombstone that keeps its
  ETag, so the deletion can sync. Its files stay in place.

`--dry-run` prints which of the two would happen. A tombstone does not block writes to other
containers, is not "repaired" back to life, and cannot be written to.

## wgs restore

```console
wgs restore <store> <backup-folder> --backup <dir> [--dry-run]
```

Restores a folder made by `wgs backup` (or `CopyStoreTo`) over the store. `--backup <dir>` is where
a **safety copy of the current store** is taken first; it must be empty or absent. Refused, with
nothing changed, when the backup is not a valid store (a missing blob, an unreadable manifest, a
size the index does not match), when it belongs to a different title, or when the write gate
refuses. The restored index is stamped strictly newer than both the backup and the current store,
so cloud sync sees the restore as the latest local change; containers the backup recorded as
`Synced` are recorded as `Modified` (they keep their ETag). `--dry-run` validates the backup and
shows the gate verdict.

## wgs adapters

```console
wgs adapters [--json]
```

Lists the [game adapters](/guide/adapters) in use: id, display name, whether each is built in or a
plugin (and where it was loaded from), the package families it serves, its container-name
conventions, and whether it provides a blob inspector, a write gate and a codec. The generic
fallback is always listed last.

## wgs inspect

```console
wgs inspect <store> [<container>] [--json]
```

Describes what each container (or the named one) holds, using the adapter that matches the store.
For every blob it prints a kind, a one-line summary, the members the payload lists (name, size,
type, note), notes such as which body could not be decoded and why, decoded text where the payload
is text, and the adapter codec's verdict. With no matching adapter, or with `--no-builtin-adapters`,
the generic view reports size, SHA-256 and what the leading bytes show (GVAS magic, zlib, gzip, zip,
PNG, text or ini). Reads only.

```console
$ wgs inspect <store> TestWorld-WC
adapter: Abiotic Factor (Game Pass) (abiotic-factor)
TestWorld-WC  [world bundle]
  World 'TestWorld': 3 member(s), 256,497 bytes uncompressed (bundle version 3)
    Profile/Worlds/TestWorld/WorldSave_MetaData  48,097 bytes  Abiotic_WorldMetadataSave
    Profile/Worlds/TestWorld/WorldSave_V_Train  4,716 bytes  Abiotic_WorldSave
    Profile/Worlds/TestWorld/PlayerData/Player_2500000000000001  203,684 bytes  Abiotic_CharacterSave
  note: Payload method 1 (Oodle), 23,587 compressed bytes.
  note: Member contents were not decoded: World bundles hold an Oodle-compressed body ...
  codec Abiotic Factor payload codec (settings ini only): unavailable: World bundles hold an Oodle-compressed body ...
```

## wgs export

```console
wgs export <store> <out-folder>
```

Writes every container's blobs to `<out-folder>/<container>/<blob>` (names made safe for the host
filesystem) and a `wgs-export.json` manifest listing each container's name, state, ETag, container
number and each blob's real name, relative file, size and SHA-256. Deleted tombstones are skipped
and reported. The folder must be empty or absent. It reads the store only, so it needs no backup.

## wgs import

```console
wgs import <store> <folder> --backup <dir> [--dry-run]
```

Adds or replaces containers from a folder written by `wgs export` (or laid out the same way by
hand, as `<container>/<blob>` without a manifest). Every file is read first; with a manifest each
file's size and SHA-256 must match and no path may leave the folder, otherwise nothing is written.
A new container is created never-uploaded (no ETag). For an existing container the blobs in the
folder replace the same-named blobs and any other blobs stay. **ETags and states in the manifest
are never applied**: only the service issues an ETag. Containers are applied one at a time, each
through the gate and the concurrent-change check; the first failure stops the import, leaving the
earlier containers fully written and the later ones untouched. An imported-over tombstone is
refused. `--dry-run` lists what would be added or replaced and every problem found.

## wgs unwrap

```console
wgs unwrap <store> <out-folder> [--layout <spec>]
```

Writes the save as the game itself lays it out outside Xbox (the Steam or Epic files), without the
container wrapper. The layout is the matching game adapter's own (`wgs adapters` shows it; 76 titles
come with the built-in catalog), else `container-folders`, which is lossless for any store. `--layout`
overrides it:

| Spec | Files |
| --- | --- |
| `container-folders[:<suffix>]` | `<container>/<blob><suffix>` |
| `one-file[:<suffix>]` | `<container><suffix>` for each single-blob container |
| `blobs[:<container>]` | `<blob>` for one container's blobs (default: the first container) |

Containers and blobs the layout leaves out, and names that are not safe file names on this system,
are listed as skipped. Reads the store only. The folder must be empty or absent.

## wgs wrap

```console
wgs wrap <store> <folder> --backup <dir> [--layout <spec>] [--dry-run]
```

The reverse of `unwrap`: maps each file in the folder back to a container and blob with the same
layout and writes them through the import path. Every file is read and the whole plan checked
before the first write. Existing containers have only the mapped blobs replaced; new containers
are created never-uploaded (no ETag). Files the layout does not recognise are listed as ignored and
left alone. Layouts that cannot be inverted (Starfield, State of Decay 2, One Lonely Outpost) refuse.
As with every write, Xbox still has to accept the change on its next sync.

## wgs sanitize

```console
wgs sanitize <store> <out-folder>
```

Writes a copy of the store that can be shared in a bug report or kept as a test fixture.
`containers.index` and every `container.N` are copied byte for byte, except that the index's root GUID
is zeroed in place. Every other file in a container folder (current, previous and orphaned blobs) is
replaced with zero bytes of the same length. Files outside container folders are left out and listed.
The output keeps the package family name, container and blob names, sizes, states and ETags. Choose
a neutral output folder name: the store's own folder name is your account id (XUID). Reads the store only.

## wgs pgs

```console
wgs pgs find    [--game <id>] [--pgs <folder>] [--json]
wgs pgs list    <user-root> [--snapshot <n>] [--json]
wgs pgs extract <user-root> <out-folder> [--snapshot <n>]
wgs pgs backup  <user-root> <destination>
```

Newer GDK titles keep saves in a second, file-oriented layout under `<drive>:\XboxGames\GameSave\pgs`
(see the [format reference](/wgs-format#pgs-the-file-oriented-layout)). These commands only read it.

- `find` lists every `u_<xuid>_<gameId>` save root on each fixed drive (or under `--pgs`), with its
  snapshots and the one `current` points to. Known game ids are named (today Forza Horizon 6, `16D460`).
- `list` shows a snapshot's save files: the one `current` points to, or `--snapshot <n>`. With no
  usable `current` it refuses rather than guess, and names the snapshots to choose from.
- `extract` copies a snapshot's save files, untouched and with their real names. They are already
  the game's own files, so this is the native save. If anything changes during the copy (the game or
  a sync is writing) the copy is removed and the command fails: close the game, let it sync, retry.
- `backup` copies the whole save root, every snapshot and the metadata files, with the same check.
  The metadata holds Xbox user and device data: keep the copy private.

There is no write command. Nothing public says how the metadata, snapshots and cloud sync must
agree, and a wrong guess can lose a save; the same choice is made by XgpSaveTools.

## wgs games

```console
wgs games [--json] [--all]
```

Reviews every Xbox title with a save folder on this machine: each package under
`%LOCALAPPDATA%\Packages\*\SystemAppData\wgs` (and stores under `XboxGames\GameSave\wgs`), plus PGS
save roots. For each it shows which adapter serves it, what the folder holds (stores, containers,
multi-blob containers, or nothing: an empty save folder is normal for a cloud-only title or one not
played on this account), anything that would block a write (an unresolved cloud conflict, say), the
native layout `unwrap` would use, and what the tool can do with it. Packages with an empty folder and no
adapter are counted but only listed with `--all`. It opens stores read-only and never writes.
