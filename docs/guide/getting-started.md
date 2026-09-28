# Getting started

Requires the .NET 10 SDK.

```console
dotnet add package GamePassStorage
```

## Find the store folder

Game Pass titles keep saves under the package's `SystemAppData\wgs` folder:

```text
%LOCALAPPDATA%\Packages\<PackageFamilyName>\SystemAppData\wgs\<XUID>_<SCID>
```

The folder that directly holds `containers.index` is the store. If a user picks a parent folder,
or one of the GUID sub-folders, `WgsStore.ResolveContainerFolder` maps it to the right place:

```csharp
using GamePassStorage;

var folder = WgsStore.ResolveContainerFolder(pickedFolder);
if (folder is null) { Console.WriteLine("That is not a wgs save folder."); return; }
```

## Open the store

Prefer `TryOpen`, which reports problems as a `WgsOpenStatus` instead of throwing:

```csharp
var opened = WgsStore.TryOpen(folder);
if (!opened.Succeeded)
{
    Console.WriteLine($"{opened.Status}: {opened.Message}");
    return;
}
var store = opened.Store!;
```

`WgsStore.Open` does the same but throws on any failure. See
[Troubleshooting](/guide/troubleshooting#wgsopenstatus) for each status.

## Look around

```csharp
Console.WriteLine($"{store.PackageFamilyName}  sync: {store.SyncState}");

foreach (var container in store.Containers)
    Console.WriteLine($"{container.Name}  {container.State}  {container.BlobSize} bytes");

var diagnosis = store.Diagnose();               // changes nothing
Console.WriteLine(diagnosis.WriteAssessment.CanWrite ? "writable" : "writes blocked");
```

## Read a blob

```csharp
var save = store.Find("MySave")!;
var read = store.TryReadBlob(save);
if (!read.Succeeded)
{
    Console.WriteLine($"{read.Status}: {read.Message}");
    return;
}
byte[] bytes = read.Blob!;
```

`read.UsedFallback` is true when the blob was found through the manifest's previous id or by
scanning the folder, a sign that Xbox has not finished syncing this container.

## Write it back

Take a whole-folder backup first, preview if you like, then commit:

```csharp
store.CopyStoreTo(@"D:\backups\wgs-before-edit");   // the real rollback

var plan = store.PlanWrite(save, edited.Length);      // touches nothing
foreach (var step in plan.Steps) Console.WriteLine(step);

var commit = store.TryWriteBlob(save, edited);
if (!commit.Succeeded)
    Console.WriteLine($"{commit.Status}: {commit.Message}");
```

`TryWriteBlob` asks the write gate, checks whether the store changed on disk since you opened it,
and only then writes. To add a container that does not exist yet, use
`TryAddOrReplaceContainer(name, blob)`. To create a brand-new store, use the static
`WgsStore.WriteNewContainer`.

::: tip Prefer the Try methods
`WriteBlob` and `AddOrReplaceContainer` throw when the gate refuses and do **not** run the
concurrent-change check. The `Try*` forms run both and return a typed `WgsCommitResult`.
:::

## After writing

Close the game before writing and leave it closed. The change reaches the Xbox cloud the next
time the title launches and syncs. See the [safety model](/guide/safety#cloud-sync-limits).
