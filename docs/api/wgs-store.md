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
| `UnsafeStateContainers` | `IReadOnlyList<string>` | Containers a write must not build on: an undefined state, or a `Deleted` entry that is not a well-formed tombstone (no ETag). |
| `PendingDeletionContainers` | `IReadOnlyList<string>` | Containers deleted locally whose tombstone (state `Deleted`, ETag kept) is waiting for the cloud. These do not block writes to other containers. |
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
typed result and reports a container holding more than one blob (use [`TryReadBlobs`](#multi-blob-containers))
or a malformed manifest as `UnsupportedLayout`. The
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

- `WriteBlob` on a container that holds several blobs throws `InvalidDataException` (and `TryWriteBlob`
  returns `Failed`) rather than drop the other blobs; use the named-blob writes below.
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

All write paths, including the new ones below, share one commit routine: fresh blobs, then the
manifest naming them, then the index (each through an atomic replace), then removal of the superseded
generation. If anything before the prune fails, the files that attempt created are removed and the
in-memory entry is restored, so the previous generation stays the only one described.

## Multi-blob containers

Many titles keep several named blobs in one container. The manifest layout is in the
[format reference](/wgs-format#multi-blob-containers).

```csharp
public IReadOnlyDictionary<string, byte[]> ReadBlobs(WgsContainer container)
public WgsBlobsReadResult TryReadBlobs(WgsContainer container)
public WgsBlobListResult TryListBlobs(WgsContainer container)
public void WriteNamedBlob(WgsContainer container, string blobName, byte[] blob)
public void WriteBlobs(WgsContainer container, IReadOnlyDictionary<string, byte[]> changes)
public WgsCommitResult TryWriteNamedBlob(WgsContainer container, string blobName, byte[] blob)
public WgsCommitResult TryWriteBlobs(WgsContainer container, IReadOnlyDictionary<string, byte[]> changes)
public WgsContainer CreateContainer(string containerName, IReadOnlyDictionary<string, byte[]> blobs)
public WgsCommitResult TryCreateContainer(string containerName, IReadOnlyDictionary<string, byte[]> blobs)
public static void WriteNewContainer(string destFolder, string containerName,
    IReadOnlyDictionary<string, byte[]> blobs, string packageFamilyName, WgsStoreOptions? options = null)
```

- `TryReadBlobs` returns name to bytes in manifest order. A single-blob container reads as one blob
  named `Data`. Each blob follows the same rules as `TryReadBlob` (in-flight sync, previous-id
  fallback); the folder-scan last resort is used only when the manifest names one blob. A malformed
  manifest is `UnsupportedLayout`.
- `TryListBlobs` lists names, ids and on-disk sizes without reading the data.
- `WriteNamedBlob` / `WriteBlobs` replace or add the named blobs in one new generation. **Blobs not
  named keep their files and ids untouched**, so their bytes are preserved exactly. The write is
  refused (`Refused`) if an untouched blob is missing or mid-sync, since the new manifest would
  misstate it. Bytes after the last manifest entry, and the manifest's leading constant, are
  written back verbatim.
- `CreateContainer` makes a never-uploaded container (`Created`, no ETag) with several blobs. It
  throws `InvalidOperationException` when the name is taken. The index records the total of all blob
  sizes (an assumption; see the format reference).
- Blob names must be 1 to 63 characters, no NUL, and must not differ from each other only by case.

## Deleting a container

```csharp
public WgsDeletePlan PlanDelete(WgsContainer container)
public WgsContainer? DeleteContainer(WgsContainer container)
public WgsCommitResult TryDeleteContainer(WgsContainer container)
```

One rule ([why](/wgs-format#deleting-a-container)): a container that was never uploaded (no ETag) is
removed from the index and its files cleared; one the cloud knows becomes a `Deleted` tombstone
(state 3) that keeps its ETag, with its files left in place. `DeleteContainer` returns the
tombstone, or `null` when the entry was removed. `WgsDeletePlan.Action` is a `WgsDeleteAction`:
`RemoveFromIndex`, `MarkDeleted` or `AlreadyDeleted`. A tombstone can no longer be written to
(`Refused`), is not touched by `RepairRecoveredManifests`, and does not block writes to other
containers.

## Restoring a backup

```csharp
public static IReadOnlyList<string> ValidateStore(string folder, WgsStoreOptions? options = null)
public static WgsRestoreResult TryRestore(string storeFolder, string backupFolder,
    string safetyCopyFolder, WgsStoreOptions? options = null)
```

`ValidateStore` returns the problems that make a folder unusable as a store (empty means valid).
`TryRestore` puts a folder made by `CopyStoreTo` back over a store: the backup must validate and
belong to the same package family, the write gate must allow it, a safety copy of the current store
is taken first (into an empty or absent `safetyCopyFolder`), and the change-detection check runs
before anything is written. Files go in blob, then manifest, then the index last. The restored index
is stamped strictly newer than both indexes, and `Synced` containers are recorded as `Modified`
(they keep their ETag). The result is a [`WgsRestoreResult`](/api/results#wgsrestoreresult).

## Exporting and importing

```csharp
public const string ExportManifestFileName = "wgs-export.json";
public WgsExportResult TryExportTo(string folder)
public WgsImportPlan PlanImport(string folder)
public WgsImportResult TryImport(string folder)
```

`TryExportTo` writes each non-deleted container's blobs to `<folder>/<container>/<blob>` and a
manifest. `PlanImport` reads and verifies a folder (size and SHA-256 against the manifest, no path
leaving the folder) and says what would be added or replaced; `TryImport` applies it one container
at a time through the gate. ETags and states in the manifest are informational and never applied.

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
  `MultiBlobContainers` lists containers with several blobs (informational) and
  `MalformedManifestContainers` lists manifests that cannot be parsed.
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
