# Store discovery

```csharp
public static class WgsStoreDiscovery
{
    public static IReadOnlyList<WgsStoreLocation> Find(string? packageFamilyName = null, bool exact = false,
        WgsStoreDiscoveryOptions? options = null);
}
```

Finds wgs stores on the machine without knowing the title. It looks in:

- `%LOCALAPPDATA%\Packages\<PackageFamilyName>\SystemAppData\wgs\<XUID>_<SCID>`, the path in the
  [format reference](/wgs-format) and the one XGP-save-extractor and libNOM.io read;
- `<drive>:\XboxGames\GameSave\wgs\...`. **Assumption:** the structure below `wgs` on that path is not
  documented in the sources consulted, so a child folder holding a `containers.index` is taken as a
  store, and a child folder holding such stores is taken as a package folder.

A store folder name is split at its **first** underscore into XUID and SCID. Only folders that hold
a `containers.index` are returned. Roots that do not exist yield nothing; nothing throws for an
unreadable folder. Results are ordered by package family name, then path.

## Filtering

`packageFamilyName` filters the results: a case-insensitive substring, or the whole name when
`exact` is true. For the `LocalAppData` layout the family is the package folder name
(`PlayStack.AbioticFactor_3wcqaesafpzfy`, no `!App` suffix). For the drive layout it is taken from
the index, up to the `!`.

## WgsStoreLocation

```csharp
public sealed record WgsStoreLocation(string PackageFamilyName, string Xuid, string Scid,
    string StorePath, WgsStoreLocationSource Source);

public enum WgsStoreLocationSource { LocalAppData, XboxGamesDrive }
```

`StorePath` is the folder holding `containers.index`; pass it to `WgsStore.TryOpen`.

## WgsStoreDiscoveryOptions

```csharp
public sealed class WgsStoreDiscoveryOptions
{
    public IWgsFileSystem FileSystem { get; init; }        // PhysicalWgsFileSystem.Instance
    public string? LocalAppData { get; init; }             // null: the current user's; empty: skip
    public IReadOnlyList<string>? DriveRoots { get; init; } // null: fixed and removable drives on Windows, none elsewhere
}
```

Every root and the filesystem are injectable, so discovery is testable on Linux against an in-memory
filesystem (see [Testing](/guide/testing)).

```csharp
foreach (var store in WgsStoreDiscovery.Find("abiotic"))
    Console.WriteLine($"{store.PackageFamilyName}  {store.Xuid}  {store.StorePath}");
```
