# WgsStore

```csharp
public sealed class WgsStore
```

An open wgs store: the parsed `containers.index` plus the operations on it. Reads never change
anything. Every write goes through the write gate. See the [safety model](/guide/safety).

## Constants

| Member | Value | Notes |
| --- | --- | --- |
| `const string IndexFileName` | `"containers.index"` | |
| `const uint KnownIndexVersion` | `14` | The only index version observed in real stores. |
| `const string CloudConflictLabel` | `"the unsettled Xbox conflict"` | Label used for the index-level conflict marker in repair lists. |

## Properties

| Property | Type | Description |
| --- | --- | --- |
| `RootPath` | `string` | The store folder. |
| `IndexVersion` | `uint` | Version read from the index. |
| `Containers` | `IReadOnlyList<WgsContainer>` | Every container in the index. |
| `PackageFamilyName` | `string` | Identifies the owning title. |
| `IndexFileTime` | `long` | The index-level FILETIME, the recency token cloud sync compares. |
| `SyncState` | `WgsSyncState` | Index sync flags. |
| `HasUnresolvedConflicts` | `bool` | `SyncState` has `HasUnresolvedConflicts`. |
| `RecoveredContainers` | `IReadOnlyList<string>` | Containers read through a fallback: a reliable sign the store is mid-sync. |
| `NeededBlobFallback` | `bool` | `RecoveredContainers` is non-empty. |
| `InvalidStateContainers` | `IReadOnlyList<string>` | Containers with a state outside the format, or a state that contradicts the ETag. |
| `UnsafeStateContainers` | `IReadOnlyList<string>` | Containers a write must not build on: undefined state or a deletion tombstone. |
| `ContradictoryStateContainers` | `IReadOnlyList<string>` | Containers whose state and ETag disagree (a write puts them right). |

## Opening

```csharp
public static bool IsContainerFolder(string folder, IWgsFileSystem? fileSystem = null)
public static string? ResolveContainerFolder(string folder, IWgsFileSystem? fileSystem = null)
public static WgsStore Open(string folder, WgsStoreOptions? options = null)
public static WgsOpenResult TryOpen(string folder, WgsStoreOptions? options = null)
```

- `IsContainerFolder`: true when the folder directly holds `containers.index`.
- `ResolveContainerFolder`: maps a folder a user picked to the store folder. It accepts the folder
  itself, a parent whose child is a store, or a GUID sub-folder whose parent is a store. Returns
  `null` when nothing nearby is a store. Never throws on an unreadable folder.
- `Open`: throws on any failure.
- `TryOpen`: reports failures as a [`WgsOpenStatus`](/api/results#wgsopenstatus).

## Finding containers

```csharp
public WgsContainer? Find(string name)
```

Looks a container up by name (or `null`).

## Reading

```csharp
public byte[] ReadBlob(WgsContainer container)
public WgsReadResult TryReadBlob(WgsContainer container)
```

`ReadBlob` throws `InvalidDataException` when the blob cannot be read safely. `TryReadBlob` returns a
typed result and also reports a manifest declaring more than one blob as `UnsupportedLayout`. The
resolution rules (both ids present and different means `SyncInFlight`, current missing and previous
present means use the previous, neither present means a size-checked folder scan) are in the
[format reference](/wgs-format#container-n-holds-two-blob-ids-and-they-are-not-a-duplicate).

## Writing

```csharp
public void WriteBlob(WgsContainer container, byte[] blob)
public WgsCommitResult TryWriteBlob(WgsContainer container, byte[] blob)
public void AddOrReplaceContainer(string containerName, byte[] blob)
public WgsCommitResult TryAddOrReplaceContainer(string containerName, byte[] blob)
public static void WriteNewContainer(string destFolder, string containerName, byte[] blob,
    string packageFamilyName, WgsStoreOptions? options = null)
public WgsWritePlan PlanWrite(WgsContainer container, long blobLength)
public void CopyStoreTo(string destination)
```

- `WriteBlob` writes a fresh GUID blob, a new `container.<N+1>` manifest, an updated index entry,
  then removes the superseded generation. The ETag is left alone. Throws
  `WgsWriteRefusedException` when the write gate refuses. It does **not** check for concurrent
  changes.
- `TryWriteBlob` is `WriteBlob` behind the write gate and the concurrent-change check (which runs
  **before** anything is written), returning a [`WgsCommitResult`](/api/results#wgscommitresult).
  A lock conflict becomes `LockConflict`, and other IO failures become `Failed`.
- `AddOrReplaceContainer` adds a container, or replaces the blob of one that exists, preserving the
  index and every other container. `TryAddOrReplaceContainer` is its typed-result form.
- `WriteNewContainer` (static) creates a brand-new single-container store at `destFolder`. It
  refuses a folder that already holds a `containers.index`. `packageFamilyName` is recorded in the
  index to identify the owning title.
- `PlanWrite` previews what `WriteBlob` would do (steps, files removed, new state) and touches
  nothing.
- `CopyStoreTo` copies the whole store folder recursively. It is the rollback for a write.

## Checking and diagnosing

```csharp
public WgsChangeReport DetectExternalChange()
public WgsDiagnosis Diagnose()
public WgsWriteAssessment AssessWrite()
public void EnsureWritable()
public static bool IsLockConflict(Exception ex)
```

- `DetectExternalChange` compares the index on disk with the one this instance last read or wrote.
  Never throws; an unreadable index is reported as changed.
- `Diagnose` gathers everything worth knowing about the store's health. Changes nothing.
- `AssessWrite` returns the write gate's verdict now.
- `EnsureWritable` throws [`WgsWriteRefusedException`](/api/results#wgswriterefusedexception) when the gate refuses.
- `IsLockConflict` is true for `UnauthorizedAccessException`, or an `IOException` whose HResult low
  word is 32 or 33 (sharing violation, lock violation).

## Repair

```csharp
public IReadOnlyList<string> ContainersNeedingRepair()
public IReadOnlyList<string> RepairRecoveredManifests()
```

`ContainersNeedingRepair` lists what a repair would change (an unresolved-conflict marker, states
outside the format, manifests whose blob is missing but recoverable). `RepairRecoveredManifests`
performs the repair and returns the names repaired. It never repairs save data and never runs as a
side effect of a read.

## Orphans

```csharp
public static bool HasOrphanedWorldFolders(string folder, WgsStoreOptions? options = null)
public static IReadOnlyList<WgsOrphanedContainer> FindOrphanedContainers(string folder, WgsStoreOptions? options = null)
public IReadOnlyList<WgsOrphanedContainer> OrphanedContainers()
public WgsContainer ReRegisterOrphan(WgsOrphanedContainer orphan, string? containerName = null)
```

Orphans are GUID sub-folders still holding save data that no index entry points at, which is what
cloud sync leaves behind when it drops a container from the index but not from the disk.
Discovery is best effort and never throws. `ReRegisterOrphan` adds an index entry pointing at the
orphan's data (nothing is copied). The entry has no ETag, so it is `Created`. It throws
`InvalidOperationException` when no name can be worked out or the name is already used, and
`WgsWriteRefusedException` when the write gate refuses.
