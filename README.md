# GamePassStorage

A game-agnostic .NET library for reading and writing **Xbox Connected Storage ("wgs")** save
folders, the format Game Pass (Microsoft Store / Xbox app) PC titles use for their saves.

It handles the container layer only: `containers.index`, `container.N` manifests and GUID-named
blobs, ETag and sync-state rules, snapshots, orphan discovery, write ordering and atomic file
replacement. What is inside a blob (compression, bundles, save classes) belongs to the game and
plugs in through small adapter interfaces. The library has no package dependencies.

It was extracted from [Abiotic Editor](https://github.com/ChristopherVR/AbioticEditor), which uses
it for Abiotic Factor's Game Pass saves. That is the only title verified so far; see
[Supported titles](#supported-titles).

## Quick start

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
| `IWgsWriteGate` | Decides whether a write may proceed. `WgsWriteGates.Structural` (the default) refuses unresolved cloud conflicts and undefined container states; a platform or game layer can add checks such as "the game is running". |

## Safety rules the library enforces

- ETags are only ever echoed, never minted. A container with an ETag becomes `Modified` on write;
  one without stays `Created`.
- The index FILETIME strictly advances on every write, and `FullyUploaded` is cleared. An
  unresolved-conflict flag is never cleared locally.
- A write goes blob, then manifest, then index, each through an atomic replace, so a crash leaves
  the previous generation fully described.
- A store changed on disk after it was opened is refused (`ConcurrentChange`) before anything is
  written.
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
| Abiotic Factor | Yes | Yes | Real sanitized stores and in-game use through Abiotic Editor |
| Any other title | Unverified | Unverified | Needs real sanitized fixtures before a support claim |

The in-memory tests exercise the API boundary with synthetic stores; they are not evidence that a
particular game's layout (multiple blobs per container, different manifest versions) is supported.
`WgsStore` reports layouts it does not understand as `UnsupportedLayout` rather than guessing.

## Building

Requires the .NET 10 SDK.

```console
dotnet build GamePassStorage.slnx
dotnet test  GamePassStorage.slnx
```

## License

Apache-2.0. See [LICENSE](LICENSE).
