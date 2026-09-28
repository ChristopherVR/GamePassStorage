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
}
```

| Gate | Behaviour |
| --- | --- |
| `Structural` | The default when `WriteGate` is null. Blocks on an unresolved cloud conflict and on containers that are deleted or in an undefined state. Adds a non-blocking note for containers whose state and ETag disagree. |
| `AllowAll` | Never refuses. For callers that run their own gate before every write. |

| Code constant | Blocking | Raised when |
| --- | --- | --- |
| `UnresolvedConflict` | yes | The index has `WgsSyncState.HasUnresolvedConflicts`. |
| `UnsafeState` | yes | A container is `Deleted` or has an undefined state. |
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

::: info Coming soon
A built-in process-aware write gate is in development and will be documented here.
:::
