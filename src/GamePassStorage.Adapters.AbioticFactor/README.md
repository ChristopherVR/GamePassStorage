# GamePassStorage.Adapters.AbioticFactor

The Abiotic Factor game adapter for [GamePassStorage](https://github.com/ChristopherVR/GamePassStorage).
It teaches the game-agnostic container layer about one title:

- matches package family `PlayStack.AbioticFactor_3wcqaesafpzfy`;
- classifies containers: `<World>-WC` (world bundle), `<World>-WC-B` (the game's own spare copy),
  `Profile*` (raw GVAS account saves), `Settings` / `GameUserSettings` (ini text, every byte
  incremented by one);
- reads a world bundle's `ABF_SAVE_VERSION` table of contents (member path, size, save class, flag)
  without decompressing the body. The body is Oodle-compressed and Oodle is not bundled, so the
  codec reports itself unavailable for bundles instead of guessing;
- names orphaned world folders from that table of contents (`IWgsBlobInspector`);
- refuses writes while an `AbioticFactor*` process is running (`IWgsWriteGate`).

It ships built into the `wgs` tool, and the same DLL loads as a plugin
(`wgs --adapters <folder> ...`). Use it from code:

```csharp
var registry = new WgsGameAdapterRegistry();
registry.Register(new AbioticFactorAdapter());
var opened = registry.TryOpen(storeFolder);      // store opened with the game's gate and inspector
```

Adapters describe and gate; they never write. Every change still goes through `WgsStore`'s own
write path. Apache-2.0.
