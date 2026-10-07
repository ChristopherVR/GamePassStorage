# GamePassStorage.Adapters.Catalog

Native save layouts for Game Pass titles, for [GamePassStorage](https://github.com/ChristopherVR/GamePassStorage).
For each title it knows how Xbox containers map to the files the Steam or Epic version keeps, so a save can
be unwrapped into those files and wrapped back:

```console
wgs unwrap <store> <out-folder>
wgs wrap   <store> <folder> --backup <dir> --dry-run
```

76 titles, among them Palworld, Starfield, Forza Horizon 5, Hades, Remnant 2, Oblivion Remastered, Avowed,
Kingdom Come: Deliverance II, Hollow Knight: Silksong, DOOM Eternal and DOOM: The Dark Ages. Package family
names and mappings come from [XGP-save-extractor](https://github.com/Z1ni/XGP-save-extractor) and
[XgpSaveTools](https://github.com/brodrigz/XgpSaveTools) (both MIT). The DOOM titles also keep their checksum
files in step on every write and offer the encrypted Steam form (`--layout steam:<SteamID64>`). They are **not verified against
real stores by this project**; see the
[supported titles page](https://github.com/ChristopherVR/GamePassStorage/blob/main/docs/supported-titles.md#the-catalog)
for each title's layout and evidence.

```csharp
var registry = new WgsGameAdapterRegistry();
foreach (var adapter in GameCatalog.CreateAdapters()) registry.Register(adapter);
var opened = registry.TryOpen(storeFolder);
var layout = opened.Adapter!.NativeLayout ?? WgsNativeLayouts.ContainerFolders;
var result = opened.Open.Store!.TryUnwrapTo(outFolder, layout);
```

Palworld's adapter also reads its `.sav` wrapper and decodes it to the GVAS save inside (format from
[palworld-save-tools](https://github.com/cheahjs/palworld-save-tools), MIT). The other adapters describe payloads generically and add no write gate of their own: process names are not
known, but `WgsWriteGates.RefuseWhilePackageRuns()` (on by default in `wgs`) finds a running game by its
package. Every write still goes through the library's structural gate, concurrent-change check and
generation write.
