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
and how many blob entries it declares. This package models exactly one.
