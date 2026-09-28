# Supported titles

| Title | Read | Write | Evidence |
| --- | --- | --- | --- |
| Abiotic Factor | Yes | Yes | Shipped adapter (`GamePassStorage.Adapters.AbioticFactor`, built into `wgs`). Real sanitized stores and in-game use through [Abiotic Editor](https://github.com/ChristopherVR/AbioticEditor) |
| Any other title | Unverified | Unverified | Works through the generic model. Needs real sanitized fixtures before a support claim, and an adapter to add game knowledge |

## Adapters

A title with an adapter gets more than raw container access: the adapter recognises the package,
names container kinds, describes what a blob holds, names orphaned data and adds its own write gate.

| Adapter | Package | Serves | What it adds |
| --- | --- | --- | --- |
| Abiotic Factor | `GamePassStorage.Adapters.AbioticFactor` (built into `wgs`) | `PlayStack.AbioticFactor_3wcqaesafpzfy` | Container classification (`<World>-WC`, `<World>-WC-B`, `Profile*`, `Settings`), the `ABF_SAVE_VERSION` table of contents read without decompressing, settings ini decoding, orphaned world names, and a gate that refuses while `AbioticFactor*` runs. World bodies are Oodle-compressed and Oodle is not bundled, so the adapter reports that instead of decoding. |

Every other title is served by the generic adapter: `list`, `diagnose`, `extract`, `put`, `delete`,
`restore`, `export`, `import` and a generic `inspect` (size, SHA-256, content sniffing) all work. To add a
title see [Game adapters (plugins)](/guide/adapters#adding-a-new-adapter).

## What "verified" means here

Abiotic Factor is the only title verified so far. The format knowledge in this library comes from
real Abiotic Factor stores analysed byte by byte, cross-checked against independent
implementations (see the [format reference](/wgs-format#where-this-knowledge-came-from-and-what-is-still-unverified)).

Other Game Pass titles use the same container layer, so they may well work, but that is not
evidence. The library's own in-memory tests exercise the API boundary with synthetic stores; they
do not prove that a particular game's layout (multi-blob manifests are implemented from public
sources but have no real multi-blob fixture here; different manifest versions are unseen) is supported. `WgsStore` reports layouts it does not understand as `UnsupportedLayout`
rather than guessing.

## Trying it on another title

Safe, read-only steps first:

```console
wgs diagnose <store>
wgs list <store>
wgs backup <store> <backup-folder>
```

If `diagnose` opens the store and reports nothing alarming, reading with `wgs extract` is the next
step. Only try a write on a title you have backed up, and remember that whether Xbox accepts a
rewritten container in-game needs a real sync on a real machine. Use
`wgs snapshot` and `wgs compare` around that sync to record what happened.

## Contributing fixtures

A sanitized real store is the most valuable contribution. To make one:

1. Close the game and the Xbox app, then copy the whole `wgs\<XUID>_<SCID>` folder.
2. **Sanitize it.** Blob contents are the game's own save data and can hold personal information.
   Replace each blob with same-sized filler bytes (or a minimal valid save you created yourself),
   keeping file names and sizes exactly. Check `containers.index` for anything you would rather not
   share; it records the package family name and container names, but no account details beyond the
   folder name, so rename the `<XUID>_<SCID>` folder to something neutral.
3. Confirm `wgs diagnose` still opens the sanitized copy.
4. Open an issue or pull request at
   [ChristopherVR/GamePassStorage](https://github.com/ChristopherVR/GamePassStorage) with the game
   name, the platform (Xbox app or Microsoft Store), whether you saw multiple blobs per container,
   and what you verified (read only, or a round trip in-game).

Tests skip gracefully when a fixture is absent, so fixtures are always optional for contributors.
See [Contributing](/contributing).
