# Supported titles

| Title | Read | Write | Evidence |
| --- | --- | --- | --- |
| Abiotic Factor | Yes | Yes | Shipped adapter (`GamePassStorage.Adapters.AbioticFactor`, built into `wgs`). Real sanitized stores and in-game use through [Abiotic Editor](https://github.com/ChristopherVR/AbioticEditor) |
| 76 catalog titles (below) | Native layout known | Native layout known | Community-sourced mappings in `GamePassStorage.Adapters.Catalog` (built into `wgs`). Synthetic tests only; no real store checked here |
| `BethesdaSoftworks.ProjectTitan_3275kfvn8vcwc` (title not publicly confirmed) | Yes (container layer) | Multi-blob write tested on a sanitized copy | Real store, sanitized as `tests/fixtures/RealMultiBlob`. Container layer only: no adapter, payloads not interpreted, and no in-game round trip |
| Any other title | Unverified | Unverified | Works through the generic model. Needs real sanitized fixtures before a support claim, and an adapter to add game knowledge |

## Adapters

A title with an adapter gets more than raw container access: the adapter recognises the package,
names container kinds, describes what a blob holds, names orphaned data and adds its own write gate.

| Adapter | Package | Serves | What it adds |
| --- | --- | --- | --- |
| Abiotic Factor | `GamePassStorage.Adapters.AbioticFactor` (built into `wgs`) | `PlayStack.AbioticFactor_3wcqaesafpzfy` | Container classification (`<World>-WC`, `<World>-WC-B`, `Profile*`, `Settings`), the `ABF_SAVE_VERSION` table of contents read without decompressing, settings ini decoding, orphaned world names, and a gate that refuses while `AbioticFactor*` runs. World bodies are Oodle-compressed and Oodle is not bundled, so the adapter reports that instead of decoding. |
| Catalog | `GamePassStorage.Adapters.Catalog` (built into `wgs`) | The 76 families below | The native layout for `wgs unwrap` and `wgs wrap`. Payload knowledge for Palworld (below); the rest are described generically, which names the class of any Unreal (GVAS) save. `wgs` refuses writes while the title's own package runs (the package gate, which every title gets). |

Every other title is served by the generic adapter: `list`, `diagnose`, `extract`, `put`, `delete`,
`restore`, `export`, `import` and a generic `inspect` (size, SHA-256, content sniffing) all work. To add a
title see [Game adapters (plugins)](/guide/adapters#adding-a-new-adapter).

## The catalog

`GamePassStorage.Adapters.Catalog` knows, for each title below, how its containers map to the files the
Steam or Epic version keeps, so `wgs unwrap` gives you the save as the game itself lays it out and
`wgs wrap` puts edited files back. Package family names and mappings come from two independent
community tools, [XGP-save-extractor](https://github.com/Z1ni/XGP-save-extractor) (E) and
[XgpSaveTools](https://github.com/brodrigz/XgpSaveTools) (T), both MIT; they agree on every title they share.
XGP-save-extractor's README supplies the evidence column where it reports one. Titles marked H were
also checked against a real store on a contributor's machine (structure, and for DOOM: The Dark Ages the
SlotFile version and both checksum sidecars). **No converted save has been loaded in-game by this
repository.** Every layout is tested against synthetic stores shaped as the sources describe.

| Title | Package family | Layout | Direction | Evidence | Source |
| --- | --- | --- | --- | --- | --- |
| Atomic Heart | `FocusHomeInteractiveSA.579645D26CFD_4hny5m903y3g0` | `one-file:.sav` | unwrap and wrap | reported working on Steam | E T |
| The Callisto Protocol | `PUBGCorp.TheCallistoProtocolXB1_gsxfe54jwf950` | `one-file:.sav` | unwrap and wrap | reported working on Steam | E T |
| Celeste | `MattMakesGamesInc.Celeste_79daxvg0dq3v6` | `one-file` | unwrap and wrap | unconfirmed | E T |
| Final Fantasy XV | `39EA002F.FINALFANTASYXVforPC_n746a19ndrrjg` | `one-file` | unwrap and wrap | reported working on Steam | E T |
| Fuga: Melodies of Steel 2 | `CyberConnect2Co.Ltd.FugaMelodiesofSteel2_zpv0gf6t8hz5r` | `one-file:.sav` | unwrap and wrap | unconfirmed | E T |
| High on Life | `2637SquanchGamesInc.HighonLife_mh7dg3tfmz2cj` | `one-file:.sav` | unwrap and wrap | reported working on Steam | E T |
| Hi-Fi RUSH | `BethesdaSoftworks.Hibiki_3275kfvn8vcwc` | `one-file:.sav` | unwrap and wrap | reported working on Steam | E T |
| Manor Lords | `HoodedHorse.ManorLords_znaey1dw2bdpr` | `one-file:.sav` | unwrap and wrap | reported working on Steam | E T |
| MechWarrior 5: Clans | `PiranhaGamesInc.MechWarrior5Clans_skpx0jhaqqap2` | `one-file:.sav` | unwrap and wrap | reported working on Steam | E T |
| Remnant 2 | `PerfectWorldEntertainment.GFREMP2_jrajkyc4tsa6w` | `one-file:.sav` | unwrap and wrap | reported working on Steam | E T |
| Solar Ash | `AnnapurnaInteractive.SolarAsh_c96c51jf6wkvm` | `one-file:.sav` | unwrap and wrap | reported working on Steam | E T |
| Yakuza 0 | `SEGAofAmericaInc.Yakuza0PC_s751p9cej88mt` | `one-file` | unwrap and wrap | reported working on Steam | E T |
| Trials of Mana | `39EA002F.TOMDF_n746a19ndrrjg` | `one-file:.sav` | unwrap and wrap | reported working on Steam | E |
| The Alters | `4063811bitstudios.TheAlters_gwy9gn5q9j1y6` | `one-file:.sav` | unwrap and wrap | reported working on Steam | E T |
| The Elder Scrolls IV: Oblivion Remastered | `BethesdaSoftworks.ProjectAltar_3275kfvn8vcwc` | `one-file:.sav` | unwrap and wrap | reported working on Steam | E T |
| Indiana Jones and the Great Circle | `BethesdaSoftworks.ProjectRelic_3275kfvn8vcwc` | `one-file:.sav` | unwrap and wrap | unconfirmed | T |
| Lords of the Fallen | `CIGamesS.A.LordsoftheFallen-PC_9609msxhzdsvj` | `one-file:.sav` | unwrap and wrap | unconfirmed | T |
| Railroads Online | `astragonSoftwareGmbH.RailroadsOnline_gq6wh0enzmg8j` | `one-file` | unwrap and wrap | unconfirmed | T |
| Yakuza Kiwami | `SEGAofAmericaInc.YakuzaKiwamiPC_s751p9cej88mt` | `one-file` | unwrap and wrap | unconfirmed | T |
| Solarpunk | `rokapublish.Solarpunk_6q4vfhsywtz4j` | `one-file:.sav` | unwrap and wrap | unconfirmed | T |
| TCG Card Shop Simulator | `OPNEONGAMES.TCGCardShopSimulator_19j6by82ahhzr` | `one-file:.json` | unwrap and wrap | unconfirmed | T |
| Rematch | `SLOCLAP.ProjectRuntime_cse8z5zpmcvkt` | `one-file:.sav` | unwrap and wrap | unconfirmed | T |
| Chained Echoes | `DECK13.ChainedEchoesRelease_rn1dn9jh54zft` | `blobs` | unwrap and wrap | unconfirmed | E T |
| Chorus | `DeepSilver.UnleashedGoF_hmv7qcest37me` | `blobs` + `.sav` | unwrap and wrap | reported working on Steam | E T |
| Hades | `SupergiantGamesLLC.Hades_q53c1yqmx7pha` | `blobs` | unwrap and wrap | reported working on Steam | E T |
| Hypnospace Outlaw | `NoMoreRobots.HypnospaceOutlaw_671zbmwb2bw9p` | `blobs` | unwrap and wrap | reported working on Steam | E T |
| Just Cause 4 | `39C668CD.JustCause4-BaseGame_r7bfsmp40f67j` | `blobs` | unwrap and wrap | unconfirmed | E T |
| Octopath Traveler | `39EA002F.FrigateMS_n746a19ndrrjg` | `blobs` | unwrap and wrap | unconfirmed | E T |
| Remnant: From the Ashes | `PerfectWorldEntertainment.RemnantFromtheAshes_jrajkyc4tsa6w` | `blobs` + `.sav` | unwrap and wrap | unconfirmed | E T |
| Sea of Stars | `SabotageStudio.SeaofStars_p3aneehax6csy` | `blobs` + `.sos` | unwrap and wrap | reported working on Steam | E T |
| Totally Accurate Battle Simulator | `LandfallGames.TotallyAccurateBattleSimulator_r2vq7k2y0v9ct` | `blobs` | unwrap and wrap | reported working on Steam | E T |
| Chants of Sennaar | `FocusHomeInteractiveSA.ChantsofSennaar-Windows_4hny5m903y3g0` | `blobs` | unwrap and wrap | unconfirmed | T |
| Avowed | `Microsoft.Avowed_8wekyb3d8bbwe` | `blobs` + `.sav` | unwrap and wrap | unconfirmed | T |
| Citizen Sleeper 2: Starward Vector | `SurpriseAttackPtyLtd.CitizenSleeper2StarwardVector_8k24hnfn3vvj0` | `blobs` | unwrap and wrap | unconfirmed | T |
| Crime Scene Cleaner | `5901F20F.CrimeSceneCleaner_t06nbjdc8fw86` | `blobs` | unwrap and wrap | unconfirmed | T |
| SnowRunner | `FocusHomeInteractiveSA.SnowRunnerWindows10_4hny5m903y3g0` | `blobs` + `.cfg` | unwrap and wrap | unconfirmed | T |
| Monster Train | `69C22BB6.MonsterTrain_8ekbzbj4dakee` | `container-folders` | unwrap and wrap | reported working on Steam | E T |
| Ninja Gaiden Sigma | `946B6A6E.NINJAGAIDENSIGMA_dkffhzhmh6pmy` | `container-folders` | unwrap and wrap | reported working on Steam | E T |
| Persona 5 Royal | `SEGAofAmericaInc.F0cb6b3aer_s751p9cej88mt` | `container-folders` | unwrap and wrap | reported working on Steam | E T |
| Persona 5 Tactica | `SEGAofAmericaInc.s0cb6b3ael_s751p9cej88mt` | `container-folders` | unwrap and wrap | reported working on Steam | E T |
| SpiderHeck | `tinyBuildGames.SpiderHeck_3sz1pp2ynv2xe` | `container-folders` | unwrap and wrap | reported working on Steam | E T |
| Wo Long: Fallen Dynasty | `946B6A6E.WoLongFallenDynasty_dkffhzhmh6pmy` | `container-folders` | unwrap and wrap | unconfirmed | E T |
| Dungeons of Hinterberg | `CurveDigital.DungeonsOfHinterberg_1ezqdnbhnc70m` | `container-folders` | unwrap and wrap | unconfirmed | T |
| Kingdom Come: Deliverance II | `DeepSilver.77536C3FE941_hmv7qcest37me` | `container-folders` | unwrap and wrap | unconfirmed | T |
| Phoenix Point | `SnapshotGames.PhoenixPoint_xxvrk32m0sthm` | `container-folders` | unwrap and wrap | unconfirmed | T |
| The Outer Worlds 2 | `Microsoft.OE-Arkansas_8wekyb3d8bbwe` | `container-folders` | unwrap and wrap | unconfirmed | T |
| The Lamplighters League | `ParadoxInteractive.ProjectWiseguy-PC_zfnrdv2de78ny` | `container-folders` | unwrap and wrap | unconfirmed | T |
| Quantum Break | `Microsoft.QuantumBreak_8wekyb3d8bbwe` | `container-folders` | unwrap and wrap | unconfirmed | T |
| DOOM Eternal | `BethesdaSoftworks.DOOMEternal-PC_3275kfvn8vcwc` | `container-folders`, `steam:<SteamID64>` | unwrap and wrap | reported working on Steam | E T |
| DOOM: The Dark Ages | `BethesdaSoftworks.ProjectTitan_3275kfvn8vcwc` | `container-folders`, `steam:<SteamID64>` | unwrap and wrap | **real store checked here** | T H |
| Palworld | `PocketpairInc.Palworld_ad4psfrxyesvt` | `palworld` | unwrap and wrap | reported working on Steam | E T |
| Forza Horizon 5 | `Microsoft.624F8B84B80_8wekyb3d8bbwe` | `forza` | unwrap and wrap | reported working on Steam | E T |
| Lies of P | `Neowiz.3616725F496B_r4z3116tdh636` | `lies-of-p` | unwrap and wrap | reported working on Steam | E T |
| Coral Island | `HumbleBundle.CoralIsland_q2mcdwmzx4qja` | `backup-folder` | unwrap and wrap | reported working on Steam | E T |
| Clair Obscur: Expedition 33 | `KeplerInteractive.Expedition33_ymj30pw7xe604` | `backup-folder` | unwrap and wrap | reported working on Steam | E T |
| Blue Prince | `RawFury.BluePrince_9s0pnehqffj7t` | `backup-folder` | unwrap and wrap | unconfirmed | T |
| Arcade Paradise | `WiredProductions.ArcadeParadise_hxzk6evwjr6sy` | `arcade-paradise` | unwrap and wrap | reported working on Steam | E T |
| Railway Empire 2 | `KalypsoMediaGroup.RailwayEmpire2Win_e60j8nnj33ga6` | `railway-empire-2` | unwrap and wrap | unconfirmed | E T |
| State of Decay 2 | `Microsoft.Dayton_8wekyb3d8bbwe` | `state-of-decay-2` | unwrap only | reported working on Steam | E T |
| Cricket 24 | `BigbenInteractiveSA.Cricket24Win10_tqjv3vrxr8ppw` | `cricket-24` | unwrap and wrap | reported working on Steam | E T |
| Control | `505GAMESS.P.A.ControlPCGP_tefn33qh9azfc` | `control` | unwrap and wrap | reported working on Epic | E T |
| Starfield | `BethesdaSoftworks.ProjectGold_3275kfvn8vcwc` | `starfield` | unwrap only | reported working on Steam | E T |
| Fallout 4 | `BethesdaSoftworks.Fallout4-CoreGame_3275kfvn8vcwc` | `fallout-4` | unwrap only | unconfirmed | T |
| One Lonely Outpost | `FreedomGames.OneLonelyOutpostGame_0c9x75n11d8wg` | `one-lonely-outpost` | unwrap only | reported working on Steam | E T |
| Galacticare | `TheCultGamesLtd.Galacticare_vnassb3anythc` | `galacticare` | unwrap and wrap | unconfirmed | T |
| Balatro | `PlayStack.Balatro_3wcqaesafpzfy` | `balatro` | unwrap and wrap | unconfirmed | T |
| Hollow Knight: Silksong | `TeamCherry.HollowKnightSilksong_y4jvztpgccj42` | `silksong` | unwrap and wrap | unconfirmed | T |
| Metaphor: ReFantazio | `SEGAofAmericaInc.Pae22b02y_s751p9cej88mt` | `metaphor-refantazio` | unwrap and wrap | unconfirmed | T |
| Ninja Gaiden 2 Black | `946B6A6E.NINJAGAIDEN2Black_dkffhzhmh6pmy` | `ninja-gaiden-2-black` | unwrap and wrap | unconfirmed | T |
| Scorn | `KeplerInteractive.1439274AB3A46_ymj30pw7xe604` | `scorn` | unwrap and wrap | unconfirmed | T |
| Roadside Research | `OroInteractive.RoadsideResearch_z7bgc74zqm87r` | `all-blobs-flat` | unwrap and wrap | unconfirmed | T |
| Persona 3 Reload | `SEGAofAmericaInc.L0cb6b3aea_s751p9cej88mt` | `persona-3-reload` | unwrap and wrap | unconfirmed | T |
| Like a Dragon: Ishin! | `SEGAofAmericaInc.ProjectMacan_s751p9cej88mt` | `like-a-dragon:png` | unwrap and wrap | unconfirmed | E T |
| Like a Dragon Gaiden: The Man Who Erased His Name | `SEGAofAmericaInc.l1b05f489e_s751p9cej88mt` | `like-a-dragon:dds` | unwrap and wrap | unconfirmed | E T |
| Yakuza: Like a Dragon | `SEGAofAmericaInc.Yazawa_s751p9cej88mt` | `like-a-dragon:dds` | unwrap and wrap | unconfirmed | E T |
| Call of Duty (HQ) | `38985CA0.COREBase_5bkah9njm3e9g` | `one-file` | unwrap and wrap | **real store checked here** | H |

DOOM: The Dark Ages and DOOM Eternal keep a `.checksum` beside each `game_duration.dat` (the data's MD5
with its four u32 words XORed, 8 and 4 bytes). The adapter recomputes it on every write, whether by
`put --blob`, `import`, `wrap` or the library, so an edit never leaves a stale checksum. `--layout
steam:<SteamID64>` unwraps each slot's four files AES-GCM encrypted for that Steam account (SlotFile
version 11 for The Dark Ages), and wraps a Steam slot back into an existing Game Pass slot (version 10),
as XgpSaveTools does.

Palworld also has payload knowledge: `wgs inspect` reads its `.sav` wrapper (`PlZ`, save type `0x31`
zlib or `0x32` zlib twice, optionally behind a `CNK` prefix, as
[palworld-save-tools](https://github.com/cheahjs/palworld-save-tools) reads it, MIT) and names the
GVAS class inside, and its codec decodes a save to the GVAS bytes for an Unreal save editor.
Re-wrapping is not offered yet: which save type each file must use is not established.

Layout notes:

- `one-file[:suffix]` takes single-blob containers only. The source tool takes the first blob of any
  container; a multi-blob container here is left out and reported instead.
- `blobs` takes the store's first container, as the source does; other containers are left out.
- Lies of P drops a numeric prefix, so wrapping only replaces existing saves. Railway Empire 2 leaves the
  `description` blob out; an existing container keeps its own, a new one has none.
- Starfield, State of Decay 2 and One Lonely Outpost lose what wrapping would need (part sizes, blob
  path prefixes, the JSON envelope), so they only unwrap.

Reported **not** to move file for file (per XGP-save-extractor): A Plague Tale: Requiem, ARK: Survival
Ascended, Chivalry 2, Death's Door, Forza Horizon 4, Neon White and Tinykin. It also lists the Like a Dragon
titles and Persona 3 Reload as incompatible, while XgpSaveTools maps them; the catalog includes them, marked
unconfirmed, with that note. Not yet in the catalog: Brotato (its saves are a bundle that needs a parser) and
Dragon Ball Xenoverse 2 (XgpSaveTools only exports it raw). Newer GDK titles keep file-oriented saves under
`XboxGames\GameSave\pgs`, a different layer from wgs. `wgs pgs` reads it (find, list, extract,
back up; no writes); the only game id known so far is Forza Horizon 6's (`16D460`).

A title not in the catalog still unwraps with `container-folders` (lossless for any store) or a layout
named with `--layout`. To promote a catalog title to a full adapter (payload knowledge, a process gate),
contribute a sanitized store; see below.

## What "verified" means here

Abiotic Factor is the only title verified so far. The format knowledge in this library comes from
real Abiotic Factor stores analysed byte by byte, cross-checked against independent
implementations (see the [format reference](/wgs-format#where-this-knowledge-came-from-and-what-is-still-unverified)).

Other Game Pass titles use the same container layer, so they may well work, but that is not
evidence. The library's own in-memory tests exercise the API boundary with synthetic stores; they
do not prove that a particular game's layout is supported. Multi-blob manifests are verified against
one real store (see the [format reference](/wgs-format#multi-blob-containers)); different manifest
versions are unseen. `WgsStore` reports layouts it does not understand as `UnsupportedLayout`
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

1. Close the game and the Xbox app.
2. **Sanitize it** with `wgs sanitize <store> <out-folder>`. It copies `containers.index` and every
   `container.N` byte for byte, zeroes the index's root GUID (its meaning is undocumented), and
   replaces every blob file with zero bytes of the same length. Name the output folder something
   neutral such as `0009000000000002_00000000000000000000000000000001`: the store's own folder name is
   your account id (XUID). The index still records the package family name, container names and
   ETags; check that you are happy to share those.
3. Confirm `wgs diagnose` still opens the sanitized copy.
4. Open an issue or pull request at
   [ChristopherVR/GamePassStorage](https://github.com/ChristopherVR/GamePassStorage) with the game
   name, the platform (Xbox app or Microsoft Store), whether you saw multiple blobs per container,
   and what you verified (read only, or a round trip in-game).

Tests skip gracefully when a fixture is absent, so fixtures are always optional for contributors.
See [Contributing](/contributing).
