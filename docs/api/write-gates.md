# Write gates and assessments

See [Writing a game adapter](/guide/adapter#adding-checks-iwgswritegate) for a worked example.

## IWgsWriteGate

```csharp
public interface IWgsWriteGate
{
    WgsWriteAssessment Assess(WgsStore store);
}
```

Decides whether a write may proceed. Set it through `WgsStoreOptions.WriteGate`.

## WgsWriteGates

```csharp
public static class WgsWriteGates
{
    public static IWgsWriteGate Structural { get; }
    public static IWgsWriteGate AllowAll { get; }

    public const string UnresolvedConflict = "unresolved-conflict";
    public const string UnsafeState = "unsafe-state";
    public const string ContradictoryState = "contradictory-state";
    public const string ProcessRunning = "process-running";
    public const string ProcessCheckFailed = "process-check-failed";

    public static IWgsWriteGate RefuseWhileRunning(params string[] processNames);
    public static IWgsWriteGate RefuseWhileRunning(IWgsProcessLister lister, params string[] processNames);
    public static IWgsWriteGate Combine(params IWgsWriteGate[] gates);
}
```

| Gate | Behaviour |
| --- | --- |
| `Structural` | The default when `WriteGate` is null. Blocks on an unresolved cloud conflict and on containers in an undefined state or with a `Deleted` state that is not a well-formed tombstone (a tombstone with its ETag, which `DeleteContainer` leaves, does not block). Adds a non-blocking note for containers whose state and ETag disagree. |
| `AllowAll` | Never refuses. For callers that run their own gate before every write. |

| Code constant | Blocking | Raised when |
| --- | --- | --- |
| `UnresolvedConflict` | yes | The index has `WgsSyncState.HasUnresolvedConflicts`. |
| `UnsafeState` | yes | A container has an undefined state, or is `Deleted` without an ETag. |
| `ContradictoryState` | no | A container's state and ETag disagree; a write corrects it. |

## WgsWriteAssessment

```csharp
public sealed record WgsWriteAssessment(IReadOnlyList<WgsWriteConcern> Concerns)
{
    public static WgsWriteAssessment Clear { get; }
    public bool CanWrite { get; }          // no concern is Blocking
    public string BlockingMessage();       // the blocking messages joined with spaces
}
```

## WgsWriteConcern

```csharp
public sealed record WgsWriteConcern(string Code, bool Blocking, string Message);
```

| Parameter | Description |
| --- | --- |
| `Code` | Stable machine-readable identifier (adapter-defined). |
| `Blocking` | True when the write must be refused. |
| `Message` | Explanation for the user. |



## Process-aware gate

```csharp
IWgsWriteGate gate = WgsWriteGates.Combine(
    WgsWriteGates.Structural,
    WgsWriteGates.RefuseWhileRunning("MyGame*", "XboxPcApp"));
var options = new WgsStoreOptions { WriteGate = gate };
```

`RefuseWhileRunning` refuses while any named process runs. Names match case-insensitively with or
without `.exe`; a trailing `*` matches any process whose name starts with the text before it
(`AbioticFactor*` matches `AbioticFactor-Win64-Shipping`). A running process is a blocking
`process-running` concern. If the process list cannot be read, the gate refuses with a blocking
`process-check-failed` concern: it will not write blind. `Combine` runs every gate and merges their
concerns, so one refusal never hides another.

```csharp
public interface IWgsProcessLister
{
    IReadOnlyCollection<string> GetRunningProcessNames();
}
public sealed class SystemWgsProcessLister : IWgsProcessLister { public static SystemWgsProcessLister Instance { get; } }
```

Inject an `IWgsProcessLister` to test a gate without real processes; the default lists real ones
with `Process.GetProcesses()`.
