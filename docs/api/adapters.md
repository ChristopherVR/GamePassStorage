# Game adapters

The types behind [game adapters (plugins)](/guide/adapters). All live in the `GamePassStorage`
namespace of the library, which stays game-agnostic: game code lives in adapter packages such as
`GamePassStorage.Adapters.AbioticFactor`.

## IWgsGameAdapter

```csharp
public interface IWgsGameAdapter
{
    string Id { get; }
    string DisplayName { get; }
    IReadOnlyList<string> KnownPackageFamilyNames { get; }
    bool Matches(string packageFamilyName);

    IWgsBlobInspector? BlobInspector => null;
    IWgsWriteGate? WriteGate => null;
    IReadOnlyList<string> ContainerNameConventions => [];
    IWgsPayloadCodec? Codec => null;

    WgsContentDescription Describe(WgsContainer container, byte[] blob);
    WgsContentDescription Describe(WgsContainer container, string blobName, byte[] blob) => Describe(container, blob);
}
```

| Member | Required | Meaning |
| --- | --- | --- |
| `Id` | yes | Stable machine id, unique in a registry. `generic` is reserved. |
| `DisplayName` | yes | Shown by `wgs adapters` and `wgs list`. |
| `KnownPackageFamilyNames` | yes | Families the adapter serves, without the `!App` suffix. An exact match here makes the adapter more specific than one that only matches by its own rule. |
| `Matches` | yes | True when the adapter serves the store whose index records this package family name (with or without `!AppId`). |
| `BlobInspector` | no | Names orphaned data from leading bytes. |
| `WriteGate` | no | The game's own gate, added to the structural gate. |
| `ContainerNameConventions` | no | Human-readable list of the game's container names. |
| `Codec` | no | Decode and encode hook. |
| `Describe` | yes | What a blob holds. Return `WgsContentDescription.Generic(...)` for a payload the adapter does not recognise. |

Adapters describe and gate; they never write. Every change still goes through `WgsStore`.
Do not throw from any member for ordinary input.

## WgsContentDescription

```csharp
public sealed record WgsContentDescription(string Kind, string Summary,
    IReadOnlyList<WgsDescribedMember> Members, IReadOnlyList<string> Notes, string? Preview = null)
{
    public static WgsContentDescription Generic(string blobName, byte[] blob);
}

public sealed record WgsDescribedMember(string Name, long? Size = null, string? Type = null, string? Note = null);
```

`Generic` reports size, SHA-256 and what the leading bytes show: GVAS magic, gzip, zlib, zip, PNG,
or text (called `text (ini-like)` when it holds a `[Section]` header), else `binary`. `Preview`
carries decoded text worth showing.

## IWgsPayloadCodec

```csharp
public interface IWgsPayloadCodec
{
    string Name { get; }
    WgsCodecResult TryDecode(WgsContainer container, byte[] blob);
    WgsCodecResult TryEncode(WgsContainer container, byte[] decoded);
}

public sealed record WgsCodecResult(bool Succeeded, byte[]? Data, string? Message)
{
    public static WgsCodecResult Ok(byte[] data);
    public static WgsCodecResult Unavailable(string reason);
}
```

Availability is decided per call, because a codec may handle some payloads and not others. A codec
that cannot run returns `Unavailable(reason)`; it never fakes a result.

## GenericWgsAdapter

```csharp
public sealed class GenericWgsAdapter : IWgsGameAdapter
{
    public static GenericWgsAdapter Instance { get; }
}
```

The fallback: it matches every store, is always last in resolution, has no inspector, gate or codec,
and describes blobs with `WgsContentDescription.Generic`. It exists so "which adapter served this
store" always has an answer and the generic model keeps working for every title.

## WgsGameAdapterRegistry

```csharp
public sealed class WgsGameAdapterRegistry
{
    public IReadOnlyList<IWgsGameAdapter> Adapters { get; }
    public void Register(IWgsGameAdapter adapter);
    public bool Unregister(string id);

    public IWgsGameAdapter Resolve(string packageFamilyName);          // never null
    public IWgsGameAdapter Resolve(WgsStore store);
    public IWgsGameAdapter? ResolveSpecific(string packageFamilyName); // null when only generic fits

    public static string FamilyOf(string packageFamilyName);
    public static WgsStoreOptions CreateOptions(IWgsGameAdapter? adapter, WgsStoreOptions? baseOptions = null);
    public WgsStoreOptions CreateOptions(string packageFamilyName, WgsStoreOptions? baseOptions = null);
    public WgsAdapterOpenResult TryOpen(string folder, WgsStoreOptions? baseOptions = null);
}

public sealed record WgsAdapterOpenResult(WgsOpenResult Open, IWgsGameAdapter? Adapter);
```

**Resolution is deterministic.** Among registered adapters whose `Matches` returns true, one whose
`KnownPackageFamilyNames` names the family exactly beats one that only matches by its own rule (a
substring, a pattern). Equally specific adapters resolve to the one registered first. If none
matches, `GenericWgsAdapter.Instance` is returned. An adapter that throws from `Matches` is skipped.
`Register` rejects a duplicate id and the reserved id `generic`.

`CreateOptions` builds `WgsStoreOptions` for an adapter: its inspector (unless the base options
already have one) and its gate added to the base gate (or to `WgsWriteGates.Structural`). With a
null or generic adapter the base options come back unchanged. `TryOpen` opens the store, resolves
its adapter from the package family name in the index and, when a game-specific one matches,
reopens it with those options; use the returned store for writes.

## WgsAdapterLoader

```csharp
public static class WgsAdapterLoader
{
    public const string DirectoriesEnvironmentVariable = "WGS_ADAPTERS_DIR";
    public static IReadOnlyList<string> DirectoriesFromEnvironment();
    public static WgsAdapterLoadResult LoadFromDirectories(IEnumerable<string> directories);
    public static WgsAdapterLoadResult LoadFromAssembly(string path);
}

public sealed record WgsAdapterLoadResult(IReadOnlyList<IWgsGameAdapter> Adapters,
    IReadOnlyList<string> Assemblies, IReadOnlyList<string> Errors)
{
    public IReadOnlyList<string> RegisterInto(WgsGameAdapterRegistry registry);
}
```

Loads every public, non-abstract class implementing `IWgsGameAdapter` with a public parameterless
constructor from each `*.dll` directly in a folder and in its immediate sub-folders. Each assembly
gets its **own `AssemblyLoadContext`**, so its private dependencies cannot clash with the host's;
the `GamePassStorage` contract assembly is always the host's own copy, so the adapter's
`IWgsGameAdapter` is the same type the host uses. The contract assembly itself and non-managed DLLs
are skipped. Problems (a missing folder, a broken assembly, a type that cannot be created) are
listed in `Errors`, never thrown.

::: danger Plugins run with full trust
An adapter is ordinary .NET code that runs in your process with your permissions. There is no
sandbox: loading an adapter is executing it. Load only assemblies you built or trust, from folders
only you can write to.
:::
