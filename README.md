# GamePassStorage

A game-agnostic .NET library for reading and writing **Xbox Connected Storage ("wgs")** save
folders, the format Game Pass (Microsoft Store / Xbox app) PC titles use for their saves.

It handles the container layer only: `containers.index`, `container.N` manifests (one blob or
several named blobs per container) and GUID-named blobs, ETag and sync-state rules, snapshots,
orphan discovery, write ordering and atomic file replacement. What is inside a blob (compression,
bundles, save classes) belongs to the game and lives in a **game adapter**: one ships for Abiotic
Factor, more can load as plugins, and a generic model works for every other title. The library has
no package dependencies and no game code.

## What it can do

- **Read and write** containers and their blobs byte-for-byte, with multi-blob containers: read all
  blobs by name, replace one blob while the others stay byte-identical, create a multi-blob container.
- **Find stores** on the machine (`%LOCALAPPDATA%\Packages\...\wgs\...` and `XboxGames\GameSave\wgs`),
  filtered by package family name.
- **Delete** a container: never-uploaded ones are removed, ones the cloud knows become a `Deleted`
  tombstone that keeps its ETag.
- **Restore** a whole-folder backup, with a safety copy of the current store first.
- **Export** every container's blobs to a folder with a manifest, and **import** a folder back.
- **Unwrap** a save into the plain files the game uses outside Xbox (its Steam or Epic layout), and
  **wrap** edited files back, using a game's known layout (43 titles catalogued) or a generic one.
- **Gate writes** structurally and by process (refuse while the game runs), composable.
- **Inspect** what a blob holds through a game adapter, or generically (size, SHA-256, sniffing).
- **Snapshot and compare** a store around a cloud sync; **diagnose** and explicitly **repair**.

It was extracted from [Abiotic Editor](https://github.com/ChristopherVR/AbioticEditor), which uses
it for Abiotic Factor's Game Pass saves. That is the only title verified so far; see
[Supported titles](#supported-titles).

## Install

```console
dotnet add package GamePassStorage          # the library
dotnet add package GamePassStorage.Adapters.AbioticFactor   # optional: the Abiotic Factor adapter
dotnet add package GamePassStorage.Adapters.Catalog         # optional: native layouts for 43 titles
dotnet tool install -g GamePassStorage.Tool # the `wgs` command-line tool (adapters built in)
```

## Command-line tool

```console
wgs list      <store> [--json]                  # containers, states, sizes, ETags
wgs diagnose  <store> [--json]                  # health report; exits 1 when writes are blocked
wgs extract   <store> <container> <out-file>    # copy a blob out
wgs backup    <store> <destination>             # whole-folder copy
wgs snapshot  <store> -o before.json            # SHA-256 fingerprint of every container
wgs compare   before.json after.json            # what a cloud sync changed
wgs put       <store> <container> <blob> --backup <dir> [--dry-run]
wgs find      [--package <text>] [--exact] [--json]   # find wgs stores on this machine
wgs blobs     <store> <container> [--json]            # blobs inside a container
wgs delete    <store> <container> --backup <dir> [--dry-run]
wgs restore   <store> <backup-folder> --backup <dir> [--dry-run]
wgs adapters  [--json]                                # game adapters in use
wgs inspect   <store> [<container>] [--json]          # what containers hold (adapter or generic)
wgs export    <store> <out-folder>                    # every container's blobs + manifest
wgs import    <store> <folder> --backup <dir> [--dry-run]
wgs unwrap    <store> <out-folder> [--layout <spec>]    # the save as the game lays it out, no Xbox wrapper
wgs wrap      <store> <folder> --backup <dir> [--layout <spec>] [--dry-run]
```

`put`, `delete`, `restore`, `import` and `wrap` write. Each refuses without a backup folder (or
`--dry-run`), takes the backup first, and goes through the same write gate and concurrent-change
check as the library. `--refuse-if-running <name>` adds a process check. `list` and `diagnose` say
which game adapter matched the store.

### Game adapters

`wgs` ships with the adapters in this repository (today Abiotic Factor) and loads more from
`--adapters <dir>` or the `WGS_ADAPTERS_DIR` environment variable. When two adapters match, the more
specific one wins: an exact package family name beats a looser match. `--no-builtin-adapters` forces
the generic model. Plugins are ordinary .NET code and **run with full trust**: load only assemblies
you built or trust.

```console
wgs adapters
wgs inspect <store>                 # world bundle table of contents, decoded settings ini, ...
wgs inspect <store> --no-builtin-adapters   # the generic view: sizes, SHA-256, sniffing
```

## Library quick start

```csharp
using GamePassStorage;

var opened = WgsStore.TryOpen(@"C:\Users\me\AppData\Local\Packages\<family>\SystemAppData\wgs\<xuid>_<scid>");
if (!opened.Succeeded) { Console.WriteLine(opened.Message); return; }
var store = opened.Store!;

foreach (var container in store.Containers)
    Console.WriteLine($"{container.Name}  {container.State}  {container.BlobSize} bytes");

var save = store.Find("MySave")!;
var read = store.TryReadBlob(save);            // Ok, MissingBlob, SyncInFlight, UnsupportedLayout, ...
if (read.Succeeded)
{
    var edited = EditMyGameSave(read.Blob!);   // your game-specific code
    store.CopyStoreTo(@"D:\backups\wgs-before-edit"); // whole-folder backup
    var commit = store.TryWriteBlob(save, edited); // Ok, Refused, LockConflict, ConcurrentChange, Failed
    Console.WriteLine(commit.Status);
}
```

## Extension points

All services are injected through `WgsStoreOptions`; every member has a real default.

| Interface | Purpose |
| --- | --- |
| `IWgsFileSystem` | File access. Swap in an in-memory implementation for tests or fault injection. |
| `IWgsClock` | Time source for the index and entry FILETIMEs. |
| `IWgsLog` | Receives what the store does. |
| `IWgsBlobInspector` | Game adapter: recognises a blob's payload, to label orphaned data and suggest a container name. |
| `IWgsWriteGate` | Decides whether a write may proceed. `WgsWriteGates.Structural` (the default) refuses unresolved cloud conflicts and undefined container states. `WgsWriteGates.RefuseWhileRunning("MyGame*")` refuses while a process runs, and `WgsWriteGates.Combine(...)` composes gates. |
| `IWgsGameAdapter` | A game adapter: matches a package family, classifies containers, describes blob contents, supplies an inspector, gate and codec. Resolved by `WgsGameAdapterRegistry`; `GenericWgsAdapter` is the always-last fallback. |
| `IWgsNativeLayout` | Maps containers and blobs to the game's own files and back, for unwrap and wrap. `WgsNativeLayouts` has the declarative ones and `Map(...)` for custom mappings; an adapter supplies one through `NativeLayout`. |
| `IWgsProcessLister` | Lists running processes, so a process gate is testable. |

## Safety rules the library enforces

- ETags are only ever echoed, never minted. A container with an ETag becomes `Modified` on write;
  one without stays `Created`.
- The index FILETIME strictly advances on every write, and `FullyUploaded` is cleared. An
  unresolved-conflict flag is never cleared locally.
- A write goes blob, then manifest, then index, each through an atomic replace, so a crash leaves
  the previous generation fully described.
- A store changed on disk after it was opened is refused (`ConcurrentChange`) before anything is
  written.
- Multi-blob writes replace only the blobs they name; untouched blobs keep their files and ids, and
  a write that would misstate an untouched blob is refused.
- Deleting never invents state: a never-uploaded container is removed, a cloud-known one becomes a
  tombstone with its ETag untouched. Restore and import go through the same gate, stamp the index
  strictly newer, and never apply an ETag from a file.
- Repair is an explicit operation (`ContainersNeedingRepair`, `RepairRecoveredManifests`); reading
  never repairs silently.

Local file access does not give control over Xbox cloud sync. Close the game (and ideally go
offline) before writing, and let the title upload the change on its next launch.

## Format reference

[docs/wgs-format.md](docs/wgs-format.md) documents the byte layouts, state and sync-flag meanings,
read and write rules, sources, and what is still unverified.

## Supported titles

| Title | Read | Write | Evidence |
| --- | --- | --- | --- |
| Abiotic Factor | Yes | Yes | Shipped adapter (`GamePassStorage.Adapters.AbioticFactor`, built into `wgs`); real sanitized stores and in-game use through Abiotic Editor |
| 43 catalogued titles (Palworld, Starfield, Forza Horizon 5, Hades, Remnant 2, ...) | Native layout | Native layout | `GamePassStorage.Adapters.Catalog`, built into `wgs`: community-sourced mappings from XGP-save-extractor, synthetic tests only. See [the list](docs/supported-titles.md#the-catalog) |
| Any other title | Unverified | Unverified | Works through the generic model; needs real sanitized fixtures before a support claim |

The in-memory tests exercise the API boundary with synthetic stores; they are not evidence that a
particular game's layout is supported. Multi-blob manifests are implemented from public sources
(XGP-save-extractor, libNOM.io, XblContainerReader, GPSaveConverter) but there is no real multi-blob
fixture yet. `WgsStore` reports layouts it does not understand as `UnsupportedLayout` rather than
guessing.

### Adding a game

A game adapter is a project `src/GamePassStorage.Adapters.<Game>` that references only the library
and implements `IWgsGameAdapter`. The Abiotic Factor adapter is the template. See
[Game adapters (plugins)](docs/guide/adapters.md) and [Contributing](docs/contributing.md) for the
project layout, registration in the tool's built-in set, tests and the fixtures policy.

## Building

Requires the .NET 10 SDK.

```console
dotnet build GamePassStorage.slnx
dotnet test  GamePassStorage.slnx
```

## Releasing

Pushing a version tag tests on Windows and Linux, publishes the packages to nuget.org
(`GamePassStorage`, `GamePassStorage.Adapters.AbioticFactor`, `GamePassStorage.Adapters.Catalog` and
`GamePassStorage.Tool`), and creates
a GitHub release:

```console
git tag v0.1.0
git push origin v0.1.0
```

The workflow (`.github/workflows/publish.yml`) uses NuGet trusted publishing (OIDC), so no API key is
stored. nuget.org needs a trusted publishing policy for this repository, `publish.yml` and the
`production` environment; set the
`NUGET_USER` repository variable if the nuget.org account name differs from the repository owner.
It can also be run by hand from the Actions tab with a version number.

## License

Apache-2.0. See [LICENSE](LICENSE).
