# Game adapters (plugins)

The library understands the container layer only. A **game adapter** teaches it about one title:
which package it serves, what the container names mean, what is inside a blob, when writing is
unsafe. Adapters are open-ended: the tool ships with the adapters in this repository, more can load
as plugins, and a store no adapter serves is handled by the generic model, which always works.

## The pieces

| Piece | Where | Role |
| --- | --- | --- |
| `IWgsGameAdapter` | `GamePassStorage` (library) | The contract. See [API](/api/adapters). |
| `GenericWgsAdapter` | `GamePassStorage` (library) | The fallback: always last, describes any blob by size, hash and sniffing. |
| `WgsGameAdapterRegistry` | `GamePassStorage` (library) | Registers adapters and resolves one for a store. |
| `WgsAdapterLoader` | `GamePassStorage` (library) | Loads adapter assemblies from folders. |
| `GamePassStorage.Adapters.<Game>` | own package and project | A shipped adapter. Today: `AbioticFactor`. |
| `BuiltInAdapters` | `GamePassStorage.Tool` | The list of adapters compiled into `wgs`. |

The library holds no game code. Everything game-specific lives in an adapter project that references
only the library.

## The contract

Only `Id`, `DisplayName`, `KnownPackageFamilyNames`, `Matches` and `Describe` are required. The rest
have defaults (interface default members), so a first adapter can be very small:

```csharp
using GamePassStorage;

public sealed class MyGameAdapter : IWgsGameAdapter
{
    public string Id => "my-game";
    public string DisplayName => "My Game (Game Pass)";
    public IReadOnlyList<string> KnownPackageFamilyNames { get; } = ["Studio.MyGame_abc123"];

    public bool Matches(string packageFamilyName)
        => WgsGameAdapterRegistry.FamilyOf(packageFamilyName)
            .Equals("Studio.MyGame_abc123", StringComparison.OrdinalIgnoreCase);

    // Optional hooks: BlobInspector, WriteGate, ContainerNameConventions, Codec.
    public IWgsWriteGate? WriteGate { get; } = WgsWriteGates.RefuseWhileRunning("MyGame*");

    public WgsContentDescription Describe(WgsContainer container, byte[] blob)
        => WgsContentDescription.Generic(container.Name, blob);   // until you recognise something
}
```

Adapters **describe and gate; they never write**. Every change still goes through `WgsStore`, so the
write order, ETag rules, strictly advancing timestamp and concurrent-change check apply no matter
what an adapter does. Do not throw from any member for ordinary input; return the generic
description for a payload you do not recognise.

A codec (`IWgsPayloadCodec`) is optional and honest: it returns `WgsCodecResult.Unavailable(reason)`
when it cannot decode something (for example a compressed body whose native library is not
installed) and never fakes a result.

## Resolution

`registry.Resolve(packageFamilyName)` never returns null:

1. Among registered adapters whose `Matches` is true, an adapter that lists the family in
   `KnownPackageFamilyNames` **beats** one that only matches by its own rule (a substring or
   pattern).
2. Equally specific adapters resolve to the one registered first (built-ins are registered before
   plugins).
3. If nothing matches, `GenericWgsAdapter` answers. The id `generic` is reserved.

`wgs list`, `wgs diagnose` and `wgs inspect` say which adapter matched, so the answer is always
visible. `--no-builtin-adapters` forces the generic model.

Opening a store through `registry.TryOpen(folder)` (or the tool's commands) reopens it with the
matching adapter's blob inspector and write gate added to the structural gate.

## Loading plugins

The `wgs` tool loads extra adapters from folders you name:

```console
wgs adapters --adapters D:\wgs-plugins
set WGS_ADAPTERS_DIR=D:\wgs-plugins;D:\more
wgs inspect <store>
```

`--adapters <dir>` can repeat; `WGS_ADAPTERS_DIR` takes several folders separated by `;` on Windows
and `:` elsewhere. The loader reads every `*.dll` directly in each folder and in its immediate
sub-folders, so a plugin can ship in its own folder with its dependencies. Each assembly loads in its
own `AssemblyLoadContext`: its private dependencies stay private, while the `GamePassStorage`
contract is always the host's copy so the types line up. From code:

```csharp
var registry = new WgsGameAdapterRegistry();
var loaded = WgsAdapterLoader.LoadFromDirectories(["D:/wgs-plugins"]);
foreach (var problem in loaded.Errors.Concat(loaded.RegisterInto(registry)))
    Console.Error.WriteLine(problem);
```

A plugin that fails to load is reported as a warning and skipped; it never stops a command. A plugin
with the same id as a built-in is reported as a duplicate and the built-in stays.

::: danger Trust model: plugins run with full trust
An adapter is ordinary .NET code loaded into the tool's process. It has your permissions: it can read
and write any file you can and start processes. There is no sandbox, and none is possible for
in-process .NET code. Load only assemblies you built yourself or trust, from folders only you can
write to. The loader never downloads anything; it only reads the folders you give it. Built-in
adapters are part of the tool's own package and carry the same trust as the tool.
:::

## Native layouts: the save without the Xbox wrapper

Xbox stores a save as containers and GUID-named blobs. The game itself, on Steam or Epic, keeps plain
files and folders. A **native layout** (`IWgsNativeLayout`) is the invertible mapping between the two, so a
save can be taken out of the wrapper to edit, back up or move, and put back.

```console
wgs unwrap <store> <out-folder> --layout container-folders
wgs wrap   <store> <folder> --layout one-file:.sav --backup <dir> [--dry-run]
```

Layouts that need no code: `container-folders[:<suffix>]` (`<container>/<blob>`), `one-file[:<suffix>]`
(one file per single-blob container) and `blobs[:<container>]` (one container's blobs side by side, the
first container by default). An adapter supplies its own through `IWgsGameAdapter.NativeLayout`, which
becomes the default for that title; with none, the tool uses `container-folders`.

A custom layout is usually two functions:

```csharp
public IWgsNativeLayout? NativeLayout { get; } = WgsNativeLayouts.Map("my-game",
    (ctx, container, blob, blobCount) => blobCount == 1 ? $"Saves/{container}.sav" : null,   // to a file
    (ctx, path) => path.StartsWith("Saves/") && WgsNativeLayouts.WithoutSuffix(path[6..], ".sav") is { } c
        ? new WgsNativeTarget(c) : null);                                                   // and back
```

`WgsNativeContext` gives both directions the store's live container names (in index order) and each
container's blob names, so a mapping that drops part of a name can find the container it came from.
Implement `IWgsNativeLayout` directly when a file is built from a whole container (`UnwrapContainer`,
for example several blobs joined into one file), when content needs transforming (`ToNativeContent` and
`ToBlobContent`), or when the mapping cannot be inverted (`CanWrap => false`, or pass a null inverse to
`Map`).

Two more optional hooks: `DerivedBlobs` (`IWgsDerivedBlobs`) recomputes blobs the game keeps in step
with others on every write through the store, for example id Tech's `.checksum` files, so no write path
can leave one stale; and `CreateNativeLayout(spec)` offers named alternative layouts, such as DOOM's
`steam:<SteamID64>`, which `wgs --layout` tries before the generic specs.

`GamePassStorage.Adapters.Catalog` holds 76 worked examples, from one-liners to Starfield's part
joining.

Unwrap reads only. Wrap uses the same checks as `import`: every file is read and planned before the first
write, the write gate and concurrent-change check apply, existing containers have only the named blobs
replaced, new ones are created with no ETag, and files the layout does not recognise are ignored and listed.
Wrapping changes the local store only; Xbox still has to accept it on the next sync.

## Walkthrough: the Abiotic Factor adapter

`GamePassStorage.Adapters.AbioticFactor` is a real, shipped adapter (its own NuGet package, built
into `wgs`). Its layout is the template for new ones:

| File | What it does |
| --- | --- |
| `AbioticFactorAdapter.cs` | The `IWgsGameAdapter`: matching, conventions, `Describe`, and wiring of the hooks below. |
| `AbioticContainers.cs` | Container classification and the ini transform. |
| `AbfBundleToc.cs` | Reads a world bundle's table of contents. |
| `AbfBlobInspector.cs` | `IWgsBlobInspector` for orphaned worlds. |
| `AbioticPayloadCodec.cs` | `IWgsPayloadCodec`: decodes the settings ini, is honestly unavailable for bundles. |

What it knows, and where each piece comes from:

- **Matching.** It serves package family `PlayStack.AbioticFactor_3wcqaesafpzfy`, given as the index
  writes it (`...!AppAbioticFactorShipping`) or without the suffix. The family is listed in
  `KnownPackageFamilyNames`, so it beats any looser adapter.
- **Container names.** `<World>-WC` is a world bundle, `<World>-WC-B` is the game's own spare copy of
  that world, `Profile*` (`ProfileUnlocks`, `ProfilePlayerStatsSave`, `ProfileUserSettings`,
  `ProfileScientistCustomization_<n>`) are account containers holding a raw GVAS save, and `Settings`
  / `GameUserSettings` are ini text. The suffixes are checked before the `Profile` prefix, because a
  world may itself be called `ProfileSomething`.
- **World bundles.** The blob starts with an `ABF_SAVE_VERSION` header and a table of contents (member
  path, uncompressed size, save class, and a flag that is 1 for the text `SandboxSettings.ini`
  member), followed by one Oodle-compressed body. The adapter reads the table of contents **without
  decompressing anything**. Oodle is not bundled, so the codec reports itself unavailable for bundles
  rather than decode; `wgs inspect` says so in a note.
- **Settings ini.** `Settings` and `GameUserSettings` are stored with every byte incremented by one, so
  the adapter decrements each byte to decode and increments to encode (the world bundle's own
  `SandboxSettings.ini` member is the reverse, and lives inside the compressed body).
- **Orphaned worlds.** `AbfBlobInspector` reads the first member path (`Profile/Worlds/<World>/...`)
  from the first 8 KB of a blob to name a leftover world folder and suggest `<World>-WC`, on a machine
  with no Oodle at all.
- **Write gate.** `WgsWriteGates.RefuseWhileRunning("AbioticFactor*")`, because the shipped game runs
  as `AbioticFactor-Win64-Shipping`.

```console
$ wgs inspect <store> Settings
adapter: Abiotic Factor (Game Pass) (abiotic-factor)
Settings  [settings ini]
  Settings: 19 characters, 1 section(s)
    [Audio]  ini section  (1 setting(s))
  note: Decoded by decrementing every byte.
  ---
  [Audio]
  Master=0.5
  ---
  codec Abiotic Factor payload codec (settings ini only): decodes to 19 bytes
```

The byte layouts were derived from the Abiotic Editor project's reference notes and checked against a
small sanitized real store (see [fixtures policy](#fixtures-policy)).

## Adding a new adapter

A new title is a new project that follows the same pattern:

1. **Create the project** `src/GamePassStorage.Adapters.<Game>/` with a csproj copied from
   `GamePassStorage.Adapters.AbioticFactor`: `net10.0`, a `ProjectReference` to `GamePassStorage`
   and nothing else, `PackageId` `GamePassStorage.Adapters.<Game>`, `EnableDynamicLoading`, a
   package `README.md` packed at the root. Add it to `GamePassStorage.slnx` under `/src/`.
2. **Write the adapter** as a public class with a public parameterless constructor implementing
   `IWgsGameAdapter`, plus a static `Instance` for hosts that register built-ins. Keep game logic in
   small classes beside it (classification, header parsing, transforms) so each can be tested alone.
3. **Register it** in `src/GamePassStorage.Tool/BuiltInAdapters.cs` and add a `ProjectReference` in
   `GamePassStorage.Tool.csproj`. That is how it gets into the built-in set, so `wgs` serves the
   title with no plugin folder. Adapters not accepted as built-ins still work as plugins.
4. **Add it to the pipelines:** a `dotnet pack src/GamePassStorage.Adapters.<Game>` line in
   `.github/workflows/ci.yml` and `publish.yml`.
5. **Test it** in `tests/GamePassStorage.Tests`: matching (the full family string, with and without
   `!App`, and a near miss), container classification, every parser against real or synthetic
   payloads including truncated input (it must never throw), the inspector, the gate (with an
   injected process lister), resolution against another adapter, loading the built DLL as a plugin
   from a temporary folder, and `wgs inspect` output. Keep tests for the generic fallback: a store the
   adapter does not serve must still work.
6. **Document it:** a row in [Supported titles](/supported-titles) with the evidence, and a note in
   the adapter package README.

The library must stay game-agnostic: no game names, layouts or constants in `src/GamePassStorage`.

### Fixtures policy

Prefer synthetic payloads built inside the test. When a real payload is needed to prove a layout,
add one small (under 2 MB) sanitized store under `tests/fixtures/`, with synthetic ids and no
personal data, and say where it came from in `tests/fixtures/README.md`. A fixture proves a layout for
its own title only. Do not claim support for a title without one.

## Generic behaviour without an adapter

Everything works when no adapter matches: `list`, `diagnose`, `extract`, `put`, `delete`, `restore`,
`export`, `import`, and a generic `inspect` that reports blob sizes, SHA-256 and what the leading
bytes show (GVAS magic, zlib, gzip, zip, PNG, text or ini). Adapters add knowledge and safety; they
are never required.
