# Supported titles

| Title | Read | Write | Evidence |
| --- | --- | --- | --- |
| Abiotic Factor | Yes | Yes | Real sanitized stores and in-game use through [Abiotic Editor](https://github.com/ChristopherVR/AbioticEditor) |
| Any other title | Unverified | Unverified | Needs real sanitized fixtures before a support claim |

## What "verified" means here

Abiotic Factor is the only title verified so far. The format knowledge in this library comes from
real Abiotic Factor stores analysed byte by byte, cross-checked against independent
implementations (see the [format reference](/wgs-format#where-this-knowledge-came-from-and-what-is-still-unverified)).

Other Game Pass titles use the same container layer, so they may well work, but that is not
evidence. The library's own in-memory tests exercise the API boundary with synthetic stores; they
do not prove that a particular game's layout (multiple blobs per container, different manifest
versions) is supported. `WgsStore` reports layouts it does not understand as `UnsupportedLayout`
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
