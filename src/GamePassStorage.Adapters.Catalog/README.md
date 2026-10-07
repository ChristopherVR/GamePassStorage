# GamePassStorage.Adapters.Catalog

Native save layouts for Game Pass titles, for [GamePassStorage](https://github.com/ChristopherVR/GamePassStorage).
For each title it knows how Xbox containers map to the files the Steam or Epic version keeps, so a save can
be unwrapped into those files and wrapped back:

```console
wgs unwrap <store> <out-folder>
wgs wrap   <store> <folder> --backup <dir> --dry-run
```

43 titles, among them Palworld, Starfield, Forza Horizon 5, Hades, Remnant 2, Oblivion Remastered, Persona
5 Royal, Control and DOOM Eternal. Package family names and mappings come from
[XGP-save-extractor](https://github.com/Z1ni/XGP-save-extractor) (MIT). They are **not verified against
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

The adapters describe payloads generically and add no write gate of their own; every write still goes
through the library's structural gate, concurrent-change check and generation write.
