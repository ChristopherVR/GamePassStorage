# Safety model

A wgs store is one half of a conversation with the Xbox cloud service, so a careless write can
look like a conflict, or simply be discarded. The library enforces the rules below. The last
section covers what no local library can enforce.

## ETags are echoed, never minted

The service issues each container's ETag. It uses the ETag you echo back to recognise which
cloud version your copy was based on. The library never invents one, and it keeps state and ETag
in agreement:

| Container has an ETag | State after a write |
| --- | --- |
| Yes | `Modified` |
| No | `Created` |

`WgsContainer.StateContradictsEtag` reports a pair that disagrees, and a write corrects it.

## Sync flags and the index timestamp

The index header carries `WgsSyncState` flags. On every write the library:

- clears `FullyUploaded`, because the store now holds something the cloud does not,
- strictly advances the index FILETIME (`max(now, previous + 1)`), since a same-or-older stamp can
  lose an edit to the cloud copy,
- **never** clears `HasUnresolvedConflicts`. Only Xbox can decide a conflict is resolved.

## Write order

A write goes **blob, then manifest, then index**, each through an atomic replace (temp file plus
move):

1. Ask the write gate.
2. Refuse if the store changed on disk since it was opened.
3. Write a fresh GUID blob.
4. Write `container.<N+1>` naming it.
5. Update the index entry and header.
6. Prune the superseded manifest and blob.

A crash at any point leaves the previous generation fully described. Several atomic replacements
are still not a transaction, which is why change detection and backups exist. The full sequence is
in the [format reference](/wgs-format#what-a-write-does).

## Concurrent-change detection

The store remembers a fingerprint of the index it last read or wrote. `TryWriteBlob` and
`TryAddOrReplaceContainer` compare it with the file on disk **before** writing anything and
return `ConcurrentChange` if the game or a sync client touched the store. Re-open the store and
re-evaluate. You can also call `store.DetectExternalChange()` yourself.

## Write gates

Every write asks an `IWgsWriteGate`. The default, `WgsWriteGates.Structural`, blocks when:

| Code | Blocking | Cause |
| --- | --- | --- |
| `unresolved-conflict` | yes | the index carries the unresolved-conflict flag |
| `unsafe-state` | yes | a container is deleted or in a state the format does not define |
| `contradictory-state` | no | a container's state and ETag disagree (a write corrects it) |

Add your own checks, such as "the game is running", with a custom gate: see
[Writing a game adapter](/guide/adapter#adding-checks-iwgswritegate). `WgsWriteGates.AllowAll`
never refuses, for callers that run their own gate first. Use `store.AssessWrite()` to preview the
verdict, and `store.EnsureWritable()` to throw `WgsWriteRefusedException` when it says no.

## Backups

Restore stages fresh blob ids and an unused manifest generation before committing the replacement
index. Existing files stay intact if staging or the index write fails. A store with all 256 manifest
generations occupied is refused safely; the safety copy remains available. After a successful
index commit, superseded files are pruned.

Import accepts version 1 export manifests for the same package family, checks blob names, sizes
and SHA-256 hashes, and reads all source bytes before the first write. The verified bytes are used
directly, so changing the import folder afterward cannot substitute unverified content. Imports
still commit one container at a time; an I/O failure reports the containers already applied.

The library keeps only one generation per container (it prunes the superseded manifest and blob),
so **the backup is your rollback**. Call `store.CopyStoreTo(destination)` before every write. The
`wgs put`, `delete`, `restore` and `import` commands refuse to run without `--backup` (or `--dry-run`).

Repair is explicit: `ContainersNeedingRepair()` previews it and `RepairRecoveredManifests()`
performs it. Reading never repairs silently, and repair never touches save data.

## Cloud sync limits

::: warning
Local file access does not give control over Xbox cloud sync. A sync cannot be triggered, paused
or observed from outside the title.
:::

- **Close the game** (and the Xbox app) before writing. A running game holds the store open and
  may overwrite your change on exit.
- **Go offline** if you can. If the service settles a conflict against your copy, your edit can be
  discarded.
- **Let the title upload the change.** Data written straight to disk uploads only the next time
  the title launches and acquires the Game Saves provider.
- **Verify with a snapshot.** Capture before and after a real sync and compare them: see the
  [`wgs snapshot`](/cli/#wgs-snapshot) and [`wgs compare`](/cli/#wgs-compare) commands, or
  [`WgsSnapshot`](/api/snapshots).
- Whether Xbox accepts a rewritten container in-game is still unverified in general: see the
  [format reference](/wgs-format#where-this-knowledge-came-from-and-what-is-still-unverified).
