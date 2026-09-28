# Snapshots

A snapshot is a point-in-time fingerprint of a whole wgs folder. Comparing a snapshot taken before
an Xbox cloud sync with one taken after is the only way to observe, end to end on a real machine,
whether an edit survived the sync. The sync itself is driven by the game or the Xbox app and cannot
be invoked from outside. The CLI equivalents are [`wgs snapshot`](/cli/#wgs-snapshot) and
[`wgs compare`](/cli/#wgs-compare).

## WgsSnapshot

```csharp
public sealed record WgsSnapshot(long IndexFileTime, IReadOnlyList<WgsContainerState> Containers)
{
    public static WgsSnapshot Capture(WgsStore store);
    public static IReadOnlyList<string> Compare(WgsSnapshot before, WgsSnapshot after);
}
```

- `Capture` fingerprints every container in an open store. It is best effort: an unreadable blob is
  recorded with its `Error` rather than aborting the snapshot.
- `Compare` describes what changed. The lines are prefixed `DROPPED`, `ROLLED BACK`, `CHANGED`,
  `RESOLVED` or `ADDED`, plus an `index timestamp advanced` or `WENT BACKWARDS` line. An empty result
  means identical. See the [`wgs compare` table](/cli/#wgs-compare).

## WgsContainerState

```csharp
public sealed record WgsContainerState(
    string Name, byte Number, WgsEntryState State, long BlobSize, string? BlobSha256, string? Error);
```

| Field | Description |
| --- | --- |
| `Name` | The container name. |
| `Number` | The `container.N` number, which advances on each write. |
| `State` | How the container stands against its cloud copy. Modified going back to Synced without the content changing means Xbox resolved a conflict in the cloud's favour. |
| `BlobSize` | Blob size in bytes. |
| `BlobSha256` | Upper-case hex SHA-256 of the blob, or null if it could not be read. |
| `Error` | The read error, if any. |

## Example

```csharp
var before = WgsSnapshot.Capture(store);
// ... write, launch the game, let it sync, close it, re-open the store ...
var after = WgsSnapshot.Capture(reopenedStore);

foreach (var line in WgsSnapshot.Compare(before, after))
    Console.WriteLine(line);
```
