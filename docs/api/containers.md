# Containers and state

## WgsContainer

```csharp
public sealed class WgsContainer
```

One logical container in a wgs folder. All members are settable, but treat the collection from
`WgsStore.Containers` as owned by the store and change containers only through store methods.

| Property | Type | Description |
| --- | --- | --- |
| `Name` | `string` | The container name, for example `ForScience-WC`. |
| `Name2` | `string` | The second name field; the same as `Name` in practice. |
| `Etag` | `string` | Service-issued version token. Echoed back untouched on every local write. Empty when never uploaded. |
| `ContainerNumber` | `byte` | The `N` in `container.N`. Advances on each write and wraps at 255. |
| `State` | `WgsEntryState` | How the container stands against its cloud copy. An undefined raw value reads as `Modified` here. |
| `RawState` | `uint` | The state as read, so an undefined value can be reported and repaired. |
| `HasInvalidState` | `bool` | `RawState` is greater than `Created` (5). |
| `IsPendingDeletion` | `bool` | A well-formed tombstone: state `Deleted` (3) with the ETag the cloud issued. |
| `StateContradictsEtag` | `bool` | Bit 2 of the state ("never uploaded") must be set if and only if the ETag is empty; true when it is not. |
| `FolderGuid` | `Guid` | The GUID folder holding the manifest and blob. |
| `FileTime` | `long` | Entry FILETIME, millisecond granular. |
| `Reserved` | `long` | Round-tripped verbatim. |
| `BlobSize` | `long` | Blob size in bytes. Must equal the real blob byte count. |
| `FolderName` | `string` | `FolderGuid` as 32 upper-case hex digits (the folder name). |

## WgsEntryState

```csharp
public enum WgsEntryState : uint
```

| Member | Value | Meaning |
| --- | --- | --- |
| `UnknownZero` | 0 | Undefined. |
| `Synced` | 1 | Local and cloud agree; the resting state. |
| `Modified` | 2 | Changed locally since the last sync; keeps its ETag. |
| `Deleted` | 3 | A tombstone kept so the deletion can reach the cloud. |
| `UnknownFour` | 4 | Undefined. |
| `Created` | 5 | Made locally and never uploaded; the ETag is empty. |

The mapping follows libNOM.io. See the [format reference](/wgs-format#container-state) for the
discussion of where public documentation disagrees.

## WgsSyncState

```csharp
[Flags] public enum WgsSyncState : uint
```

| Member | Value | Meaning |
| --- | --- | --- |
| `None` | 0 | |
| `FullyUploaded` | 1 | Cleared by every write. |
| `FullyDownloaded` | 2 | |
| `HasUnresolvedConflicts` | 16 | Never cleared locally. Blocks writes under the default gate. |

## WgsOrphanedContainer

```csharp
public sealed record WgsOrphanedContainer(
    string FolderName, string FolderPath, byte ContainerNumber, Guid BlobId,
    long BlobSize, DateTime LastWrittenUtc, string? Label, string? SuggestedContainerName);
```

Save data in a wgs folder that no index entry points at. `Label` and `SuggestedContainerName` come
from the [`IWgsBlobInspector`](/api/services#iwgsblobinspector), if one was supplied.

## WgsManifestInfo

```csharp
public sealed record WgsManifestInfo(Guid Current, Guid Previous, uint BlobCount);
```

Both blob ids a `container.N` manifest records (the file on disk and the one the cloud last knew),
and how many blob entries it declares. It describes the first blob of a manifest; the full list is
[`WgsBlobEntry`](#wgsblobentry).

## WgsBlobEntry

```csharp
public sealed record WgsBlobEntry(string Name, Guid CloudId, Guid LocalId);
```

One blob entry of a `container.N` manifest: a name (a 128-byte UTF-16 field) and two GUIDs. `CloudId`
is the blob id as the cloud last knew it, `LocalId` the file on disk; they differ only while a sync
is in flight.

## WgsBlobInfo

```csharp
public sealed record WgsBlobInfo(string Name, Guid CloudId, Guid LocalId, long? Size)
{
    public bool SyncInFlight { get; }   // CloudId != LocalId
}
```

What `TryListBlobs` reports. `Size` is the on-disk size, or `null` when the file is missing.

## WgsDeleteAction and WgsDeletePlan

```csharp
public enum WgsDeleteAction { RemoveFromIndex, MarkDeleted, AlreadyDeleted }
public sealed record WgsDeletePlan(string ContainerName, WgsDeleteAction Action,
    IReadOnlyList<string> Steps, WgsWriteAssessment Assessment);
```

## Export and import types

```csharp
public sealed record WgsExportBlob(string Name, string File, long Size, string Sha256);
public sealed record WgsExportContainer(string Name, WgsEntryState State, string Etag,
    byte ContainerNumber, IReadOnlyList<WgsExportBlob> Blobs);
public sealed record WgsExportManifest(string Format, int Version, string PackageFamilyName,
    IReadOnlyList<WgsExportContainer> Containers);
public sealed record WgsImportItem(string ContainerName, bool IsNew, IReadOnlyList<string> BlobNames, long TotalBytes);
public sealed record WgsImportPlan(IReadOnlyList<WgsImportItem> Items, IReadOnlyList<string> Problems,
    WgsWriteAssessment Assessment)
{
    public bool CanApply { get; }   // no problems, something to import, and the gate allows it
}
```
