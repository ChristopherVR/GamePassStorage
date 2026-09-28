# Xbox Connected Storage (wgs) format

This is the on-disk layout that Game Pass (Microsoft Store / Xbox app) PC titles use for their
saves, as read and written by this library. Findings come from real stores and independent
implementations; unverified points are called out at the end.

A store is a folder (usually `%LOCALAPPDATA%\Packages\<PackageFamilyName>\SystemAppData\wgs\<XUID>_<SCID>`)
holding a `containers.index` that maps logical container names to GUID-named folders. Each folder
holds a `container.N` manifest naming a GUID-named blob file. What is inside a blob is up to the
game; this library treats it as opaque bytes and lets a game adapter describe it through
`IWgsBlobInspector`.

## `containers.index` is a sync protocol, not a file listing

The index is one half of a conversation with the Xbox cloud service. It records what the service
should believe about each container. A local tool can inspect and update this metadata, while
the service remains authoritative for cloud versions and ETags.

Layout (little-endian):

| Field | Type | Notes |
| --- | --- | --- |
| version | `u32` | 14 in every observed store |
| container count | `u32` | rewritten from the live list on every save |
| reserved | `u32` | 0. Really an empty length-prefixed display name |
| package family name | wstring | `u32` char count + UTF-16. e.g. `Publisher.Game_xxxxxxxxxxxxx!AppGameShipping` |
| index FILETIME | `i64` | the recency token sync compares. Full precision |
| sync flags | `u32` | see below. Previously misread as a constant `3` |
| root GUID | wstring | |
| reserved | 8 bytes | `00 00 00 10 00 00 00 00` as the game writes it |

Then one entry per container:

| Field | Type | Notes |
| --- | --- | --- |
| name | wstring | e.g. `ForScience-WC` |
| name2 | wstring | same as name in practice |
| ETag | wstring | a version token **issued by the service**, e.g. `"0x8DEBCCC41BE9635"` |
| container number | `u8` | the `N` in `container.N` |
| state | `u32` | see below |
| folder GUID | 16 bytes | mixed-endian, matches the GUID folder name |
| FILETIME | `i64` | millisecond granular (every observed value divides by 10,000 ticks) |
| reserved | `i64` | really `Type` + `UnkInt3`, both 0 |
| blob size | `i64` | must equal the real blob byte count |

### Container state

| Value | Meaning |
| --- | --- |
| 0 | undefined |
| 1 | Synced. Local and cloud agree; the resting state |
| 2 | Modified. Changed locally, still based on a known cloud version. Keeps its ETag |
| 3 | Deleted. A tombstone, so the deletion can reach the cloud |
| 4 | undefined |
| 5 | Created. Made locally, never uploaded, so no ETag |

**Observation:** public reverse-engineering documentation does not agree on the names for states
2, 4 and 5. The mapping above follows [libNOM.io](https://github.com/zencq/libNOM.io).
[LukeFZ/XblContainerReader](https://github.com/LukeFZ/XblContainerReader) uses different names
for states 2 and 5. This is a documentation discrepancy; observed stores and their
ETag pairing provide context:

- Containers written by the game use state 1 or 2 and carry an ETag.
- Other parsers associate the Created state with an empty ETag, consistent with the local-only
  interpretation of states 4 and 5 in the mapping above.

**The write rule follows from that invariant.** A container with an ETag becomes `Modified`; one
without stays `Created`. Never break the pairing, and never mint an ETag: only the service issues
them, and it uses the one you echo back to recognise which cloud version your copy was based on.

### Sync flags

The header `u32` after the index FILETIME is a flags field:

| Bit | Meaning |
| --- | --- |
| 1 | FullyUploaded |
| 2 | FullyDownloaded |
| 16 | HasUnresolvedConflicts |

A healthy fully-synced store reads 3. A live store found mid-problem read 18
(`FullyDownloaded | HasUnresolvedConflicts`). On write the library clears `FullyUploaded`, because the store holds something the cloud does not.
It deliberately does **not** clear the conflict bit: only Xbox can decide a conflict is resolved,
and clearing it locally would hide a real problem rather than fix one. `WgsStore.HasUnresolvedConflicts` and `Diagnose()` report it
instead, and the default write gate refuses to write while it is set.

The index FILETIME is refreshed on every write and **strictly advances** (`max(now, previous + 1)`).
Cloud sync compares it to decide which copy is newer, so a same-or-older stamp is enough to lose an
edit to the cloud copy. Confirmed by diffing two game-written indexes: the game rewrites this value
on every save.

## `container.N` holds two blob ids, and they are not a duplicate

```
u32 constant (4)
u32 blob count (1)
128-byte fixed UTF-16 name field ("Data", zero padded)
16 bytes  blob id as the cloud last knew it
16 bytes  blob id of the file on disk
```

Confirmed across four independent implementations (Z1ni/XGP-save-extractor, LukeFZ,
Fr33dan/GPSaveConverter, libNOM.io). In a settled container the two are identical, which is what this library writes. They differ while a sync is in flight, which gives the read rules:

- **Both present and different:** a sync is genuinely in flight and nothing on disk says which side
  wins. `TryReadBlob` returns `SyncInFlight` rather than hand back the wrong save as though it were the right one.
- **Current missing, previous present:** use the previous one. It is a recorded alternative, not a
  guess.
- **Neither present:** last resort, scan the folder for a GUID-named blob, and accept it only when
  its size matches the size the index records.

The game keeps exactly **one** `container.N` and one blob per folder, so the library prunes the
superseded generation after committing the index. Keeping old generations is what made the
folder-scan fallback ambiguous in the first place. Use `CopyStoreTo` to take a whole-folder backup before writing; that is the real rollback.

`containers.index` and the manifests are written through a temp file plus an atomic replace: a
truncated index loses every container in the store at once, which no per-save backup can undo.

## What a write does

`WgsStore.TryWriteBlob` and `TryAddOrReplaceContainer` follow this order:

1. Ask the write gate (`IWgsWriteGate`) whether writing is allowed. The default refuses a store
   with an unresolved conflict or a deleted/undefined container state; a platform or game adapter can add its own
   concerns (for example "the game is running").
2. Refuse if the store changed on disk since it was opened (`DetectExternalChange`).
3. Write a fresh GUID blob file.
4. Write `container.<N+1>` naming it (both ids identical).
5. Update the index entry: number, size, entry FILETIME, and state set to `Modified` or `Created`
   per the ETag rule. The ETag itself is echoed untouched.
6. Rewrite the index header: container count, strictly advanced FILETIME, `FullyUploaded` cleared.
7. Prune the superseded manifest and blob.

Order matters: blob, then the manifest naming it, then the index naming the manifest. A crash at
any point leaves the previous generation still fully described, never a manifest pointing at a blob
that does not exist. Index and manifests are written through a temp file plus an atomic replace.
Several atomic replacements are still not a transaction across the whole store, which is why the
change detection above exists.

## Where this knowledge came from, and what is still unverified

Sources:

- Real Game Pass stores from Abiotic Factor analysed byte by byte (2026-06), including a live store
  whose saves kept vanishing and a series of backups showing the state field climbing over
  successive edits by an older tool.
- [libNOM.io](https://github.com/zencq/libNOM.io) for the container state mapping and the
  prune-on-write behaviour.
- [LukeFZ/XblContainerReader](https://github.com/LukeFZ/XblContainerReader) (LibXblContainer) for
  the index layout, the sync flags, and the fact that its `SetModified()` never touches the ETag.
  Its state mapping is the one **not** to follow.
- `palworld-xgp-import`, `palworld-save-pal`, Z1ni/XGP-save-extractor and Fr33dan/GPSaveConverter as
  independent cross-checks on the state/ETag invariant and the two blob ids.
- Microsoft's Connected Storage documentation for sync behaviour: conflict resolution is
  user-driven, and data written straight to disk uploads only the next time the title launches and
  acquires the Game Saves provider.

Still unverified from here:

- **Whether Xbox accepts a rewritten container in-game.** That needs a real sync cycle on a real
  machine with a signed-in account. Connected Storage sync cannot be invoked from outside the title
  (it needs the title's service configuration id and a signed-in Xbox Live account), so observing
  the before/after of a real sync is the only proof available. `WgsSnapshot.Capture` and
  `WgsSnapshot.Compare` exist for exactly that.
- The meaning of the reserved header bytes. They are round-tripped verbatim.
- Whether any state value above 5 has a meaning at all. The library treats them as damage
  (`WgsContainer.HasInvalidState`), on the evidence that only older third-party tools are known to
  have produced them.
