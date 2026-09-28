# Writing a game adapter

The library knows the container layer only. Everything game-specific plugs in through
`WgsStoreOptions`, and every member has a real default:

```csharp
var options = new WgsStoreOptions
{
    FileSystem    = PhysicalWgsFileSystem.Instance,  // default
    Clock         = SystemWgsClock.Instance,         // default
    Log           = NullWgsLog.Instance,             // default
    BlobInspector = new MyGameInspector(),           // optional
    WriteGate     = new MyGameWriteGate(),           // null means WgsWriteGates.Structural
};
var opened = WgsStore.TryOpen(folder, options);
```

`WgsStoreOptions` uses `init` properties, so build it once and pass it to `TryOpen`, `Open`,
`FindOrphanedContainers` and `WriteNewContainer`.

## Recognising payloads: IWgsBlobInspector

```csharp
public interface IWgsBlobInspector
{
    int HeadBytes { get; }
    WgsBlobDescription? Inspect(ReadOnlySpan<byte> head);
}

public sealed record WgsBlobDescription(string? Label, string? SuggestedContainerName);
```

The store reads the first `HeadBytes` bytes of a blob and hands them to `Inspect`, which returns
a description or `null` when the payload is not one you recognise. It is used to label
**orphaned** data (`WgsOrphanedContainer.Label`) and to suggest a container name when re-registering
it. Because it needs only the leading bytes, it works on a machine with no decompressor
installed.

```csharp
using System.Text;
using GamePassStorage;

/// <summary>Recognises "MYSV" saves: 4 magic bytes, then a length-prefixed world name.</summary>
public sealed class MyGameInspector : IWgsBlobInspector
{
    public int HeadBytes => 64;

    public WgsBlobDescription? Inspect(ReadOnlySpan<byte> head)
    {
        if (head.Length < 5 || !head[..4].SequenceEqual("MYSV"u8)) return null;

        int nameLength = head[4];
        if (head.Length < 5 + nameLength) return null;

        var world = Encoding.UTF8.GetString(head.Slice(5, nameLength));
        return new WgsBlobDescription(Label: world, SuggestedContainerName: $"{world}-WC");
    }
}
```

Guidelines:

- Return `null` for anything you do not recognise; do not guess.
- Do not throw. Treat short or truncated input as "not mine".
- Keep `HeadBytes` as small as you can. It is read for every orphaned blob.

Then discover and re-register leftover data:

```csharp
foreach (var orphan in store.OrphanedContainers())
{
    Console.WriteLine($"{orphan.FolderName}  {orphan.Label}  {orphan.BlobSize} bytes");
    // store.ReRegisterOrphan(orphan);   // uses SuggestedContainerName; pass a name to override
}
```

`ReRegisterOrphan` changes only `containers.index`, and the entry comes back as `Created` with no
ETag. It asks the write gate first and throws `InvalidOperationException` when no name can be
worked out or the name is already used.

## Adding checks: IWgsWriteGate

```csharp
public interface IWgsWriteGate
{
    WgsWriteAssessment Assess(WgsStore store);
}

public sealed record WgsWriteConcern(string Code, bool Blocking, string Message);
public sealed record WgsWriteAssessment(IReadOnlyList<WgsWriteConcern> Concerns)
{
    public bool CanWrite { get; }   // false when any concern is Blocking
}
```

A gate returns a list of concerns. Any concern with `Blocking = true` makes the write refuse,
and non-blocking concerns are surfaced as notes. Build on the structural checks so you keep them:

```csharp
using System.Diagnostics;
using GamePassStorage;

/// <summary>Structural checks plus "the game must not be running".</summary>
public sealed class MyGameWriteGate(string processName) : IWgsWriteGate
{
    public const string GameRunning = "game-running";

    public WgsWriteAssessment Assess(WgsStore store)
    {
        var concerns = new List<WgsWriteConcern>(WgsWriteGates.Structural.Assess(store).Concerns);

        if (Process.GetProcessesByName(processName).Length > 0)
        {
            concerns.Add(new WgsWriteConcern(GameRunning, Blocking: true,
                "Close the game before saving. A running game can overwrite the change on exit."));
        }
        return new WgsWriteAssessment(concerns);
    }
}
```

`Code` is a stable machine-readable identifier your UI can switch on. The built-in codes are the
constants `WgsWriteGates.UnresolvedConflict`, `UnsafeState` and `ContradictoryState`.

The gate is consulted by `AssessWrite`, `EnsureWritable`, `WriteBlob`, `AddOrReplaceContainer`,
`ReRegisterOrphan` and the `Try*` methods, and its verdict is echoed back in
`WgsCommitResult.Assessment`.

::: warning Do not weaken the structural gate lightly
`WgsWriteGates.AllowAll` never refuses. Use it only when you run an equivalent gate of your
own before every write.
:::

## Injected filesystem, clock and log

| Interface | Implement it to |
| --- | --- |
| `IWgsFileSystem` | run against memory or inject faults (locked files, interrupted commits) |
| `IWgsClock` | make index and entry FILETIMEs deterministic |
| `IWgsLog` | capture what the store does (`Info` and `Warn`; never throw) |

```csharp
public sealed class ConsoleWgsLog : IWgsLog
{
    public void Info(string message) => Console.WriteLine($"[wgs] {message}");
    public void Warn(string message) => Console.Error.WriteLine($"[wgs warn] {message}");
}

public sealed class FixedClock(DateTimeOffset now) : IWgsClock
{
    public DateTimeOffset UtcNow { get; set; } = now;
}
```

The full member lists are on the [injected services](/api/services) page. A complete in-memory
`IWgsFileSystem` is in [Testing](/guide/testing).

## Putting it together

```csharp
var options = new WgsStoreOptions
{
    Log = new ConsoleWgsLog(),
    BlobInspector = new MyGameInspector(),
    WriteGate = new MyGameWriteGate("MyGame-Win64-Shipping"),
};

var opened = WgsStore.TryOpen(folder, options);
if (!opened.Succeeded) return;

var store = opened.Store!;
store.CopyStoreTo(backupFolder);
var commit = store.TryWriteBlob(store.Find("MySave")!, editedBytes);
if (commit.Status == WgsOperationStatus.Refused)
    foreach (var concern in commit.Assessment!.Concerns.Where(c => c.Blocking))
        Console.WriteLine($"{concern.Code}: {concern.Message}");
```

::: info Coming soon
Process-aware write gate and multi-blob container support are in development. This page will
gain sections for them once the APIs land.
:::
