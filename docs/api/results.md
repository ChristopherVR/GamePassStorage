# Results and status codes

The `Try*` methods return these records instead of throwing. What to do about each status is in
[Troubleshooting](/guide/troubleshooting).

## WgsOpenStatus

```csharp
public enum WgsOpenStatus { Opened, NotAContainerFolder, UnsupportedLayout, LockConflict, Failed }
```

| Member | Meaning |
| --- | --- |
| `Opened` | The store opened. |
| `NotAContainerFolder` | The folder does not contain `containers.index`. |
| `UnsupportedLayout` | `containers.index` exists but could not be parsed as the known layout. |
| `LockConflict` | Another process holds the index open exclusively (or access was denied). |
| `Failed` | Any other IO or permission failure. |

## WgsOpenResult

```csharp
public sealed record WgsOpenResult(WgsOpenStatus Status, WgsStore? Store, string? Message)
{
    public bool Succeeded { get; }   // Status == Opened
}
```

## WgsOperationStatus

```csharp
public enum WgsOperationStatus
{
    Ok, Refused, LockConflict, ConcurrentChange, UnsupportedLayout, MissingBlob, SyncInFlight, Failed
}
```

| Member | Meaning |
| --- | --- |
| `Ok` | Succeeded. |
| `Refused` | The write gate refused (unresolved conflict, unsafe state, platform concern). |
| `LockConflict` | A file was held by another process (sharing violation or access denied). |
| `ConcurrentChange` | The store on disk no longer matches what was inspected; re-open and re-evaluate. |
| `UnsupportedLayout` | A manifest or index shape this package does not model (for instance several blobs). |
| `MissingBlob` | The blob a manifest names is missing and no safe alternative exists. |
| `SyncInFlight` | Two different blobs are on disk: a sync is in flight and nothing says which should win. |
| `Failed` | Any other IO failure. |

## WgsReadResult

```csharp
public sealed record WgsReadResult(WgsOperationStatus Status, byte[]? Blob, string? Message, bool UsedFallback)
{
    public bool Succeeded { get; }   // Status == Ok
}
```

`UsedFallback` is true when the blob was read through the manifest's previous id or a size-checked
folder scan, meaning Xbox has not finished syncing that container.

## WgsCommitResult

```csharp
public sealed record WgsCommitResult(WgsOperationStatus Status, WgsContainer? Container, string? Message,
    WgsWriteAssessment? Assessment = null)
{
    public bool Succeeded { get; }   // Status == Ok
}
```

`Container` is set on success. `Assessment` carries the write gate's verdict, so a `Refused` result
lists the blocking [concerns](/api/write-gates#wgswriteconcern).

## WgsChangeReport

```csharp
public sealed record WgsChangeReport(bool Changed, IReadOnlyList<string> Reasons)
{
    public static WgsChangeReport Unchanged { get; }
}
```

Returned by `WgsStore.DetectExternalChange()`.

## WgsWritePlan

```csharp
public sealed record WgsWritePlan(
    string ContainerName, byte CurrentNumber, byte NewNumber, WgsEntryState NewState,
    long NewBlobSize, IReadOnlyList<string> Steps, IReadOnlyList<string> FilesToRemove,
    WgsWriteAssessment Assessment);
```

A preview from `WgsStore.PlanWrite`. Nothing is touched to produce it.

## WgsDiagnosis

```csharp
public sealed record WgsDiagnosis(
    uint IndexVersion, bool IsKnownIndexVersion, string PackageFamilyName, WgsSyncState SyncState,
    int ContainerCount, IReadOnlyList<string> InvalidStateContainers,
    IReadOnlyList<string> UnsafeStateContainers, IReadOnlyList<string> ContainersNeedingRepair,
    IReadOnlyList<string> MultiBlobContainers, IReadOnlyList<WgsOrphanedContainer> Orphans,
    WgsWriteAssessment WriteAssessment);
```

Returned by `WgsStore.Diagnose()`. `MultiBlobContainers` lists containers whose manifest declares
more than one blob.

## WgsWriteRefusedException

```csharp
public sealed class WgsWriteRefusedException : InvalidOperationException
{
    public WgsWriteRefusedException(WgsWriteAssessment assessment);
    public WgsWriteAssessment Assessment { get; }
}
```

Thrown by the throwing write paths (`WriteBlob`, `AddOrReplaceContainer`, `ReRegisterOrphan`,
`EnsureWritable`) when the write gate refuses. Its message is the assessment's blocking message.
