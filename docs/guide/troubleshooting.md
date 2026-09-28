# Troubleshooting

Every fallible call returns a status value instead of throwing where it can. This page lists each
one, what it means and what to do.

## WgsOpenStatus

Returned by `WgsStore.TryOpen` in `WgsOpenResult.Status`.

| Status | Meaning | What to do |
| --- | --- | --- |
| `Opened` | The store opened. `Store` is set. | Continue. |
| `NotAContainerFolder` | The folder does not contain `containers.index`. | Check the path. Use `WgsStore.ResolveContainerFolder` to map a parent or GUID sub-folder to the store folder. |
| `UnsupportedLayout` | `containers.index` exists but is not in the layout this package understands. | Do not write to it. Run `wgs diagnose` if it opens, and report the store (sanitized) as an issue. See [Supported titles](/supported-titles#contributing-fixtures). |
| `LockConflict` | Another process holds the index open, or access was denied. | Close the game and the Xbox app, wait a few seconds, and retry. Check folder permissions. |
| `Failed` | Any other IO or permission error. `Message` has the detail. | Read the message, check the path exists and the drive is available. |

## WgsOperationStatus

Returned by `TryReadBlob` (in `WgsReadResult`) and by `TryWriteBlob` and `TryAddOrReplaceContainer`
(in `WgsCommitResult`).

| Status | Read | Write | Meaning and what to do |
| --- | --- | --- | --- |
| `Ok` | yes | yes | It worked. On a read, check `UsedFallback`: true means the blob came from the previous id or a folder scan, so Xbox has not finished syncing that save. |
| `Refused` | no | yes | The write gate said no, or a write would misstate a blob (an untouched blob of a multi-blob container is missing or mid-sync), or the container is a deletion tombstone. Read `Assessment.Concerns` and `Message`. For `unresolved-conflict`, launch the game online and let it resolve the conflict. For `unsafe-state`, the container has an undefined state or is `Deleted` without an ETag: run `RepairRecoveredManifests`. For `process-running`, close the named process (and the Xbox app). For `process-check-failed`, the process list could not be read: fix that or run as a user who can read it. For a custom concern (for example "game running"), fix that. |
| `LockConflict` | yes | yes | A file was held by another process (sharing violation or access denied). Close the game and the Xbox app, then retry. Nothing was written. |
| `ConcurrentChange` | no | yes | The index changed on disk after you opened the store, so the game or a sync client touched it. Nothing was written. Re-open the store, re-read, re-apply your edit, and retry. |
| `UnsupportedLayout` | yes | yes | A manifest or index shape this package does not model, for instance a truncated manifest or one whose blob count its length cannot hold. (A container that simply holds several blobs is supported: read it with `TryReadBlobs`; `TryReadBlob` also reports it here, with a message pointing at `TryReadBlobs`.) The library refuses rather than guess. Report a sanitized fixture. |
| `MissingBlob` | yes | no | The blob a manifest names is missing and no safe alternative exists. Xbox may not have finished downloading it. Close the game, wait for sync, and try again. |
| `SyncInFlight` | yes | no | Two different blobs are on disk: a sync is part-way through and nothing says which should win. Close the game and Xbox app, let sync finish, then open again. Do not write. |
| `Failed` | yes | yes | Another IO error. `Message` has the detail. Nothing is guaranteed about a partial write: restore from your `CopyStoreTo` backup if the store looks wrong. |

## Reading `wgs diagnose`

`wgs diagnose` (and `WgsStore.Diagnose()`) reports:

| Section | Meaning | Fix |
| --- | --- | --- |
| invalid state | State outside the format, or a state that contradicts the ETag | `RepairRecoveredManifests()`, or a write corrects contradictions |
| unsafe to build on | Undefined state, or `Deleted` without an ETag | Repair the state. (A deletion tombstone with its ETag, which `wgs delete` leaves, is normal and is listed as pending deletion, not here.) |
| need repair | An unresolved-conflict marker, undefined states, or manifests whose blob is missing but recoverable | `RepairRecoveredManifests()` (explicit; never runs on a read) |
| multi-blob | The container's manifest names more than one blob. Informational: supported | `wgs blobs`, `TryReadBlobs`, `WriteNamedBlob`. `put` and `WriteBlob` refuse these rather than drop blobs |
| malformed manifest | A `container.N` exists but cannot be parsed (truncated, a blob count its length does not support, a repeated blob name) | Damage. Restore that container from a backup (`wgs restore`); do not write |
| orphaned folders | Save data no index entry points at | `OrphanedContainers()` then `ReRegisterOrphan` |

## Common questions

**My edit vanished after the game started.** The service may have settled a conflict in the
cloud's favour, or the game overwrote the store. Compare snapshots taken before and after with
`wgs compare`: it flags DROPPED, ROLLED BACK, CHANGED and RESOLVED containers.

**`wgs put` says nothing was written.** The message names the status. The backup is kept either
way, so nothing is lost.

**Reading works but writing is refused.** Reading only needs a coherent manifest. Writing also
needs a clear write gate; see `wgs diagnose` for the concerns.

**Exit code 1 from `wgs diagnose`.** Writes are blocked. See the concerns it prints.

## Deleting, restoring, importing

**`wgs delete` left the container listed as `Deleted`.** The cloud knows that container, so the
entry stays as a tombstone with its ETag until the deletion syncs. Launch the game online and let it
sync. A container that was never uploaded is removed outright.

**`wgs restore` says the backup is not a valid store.** It names each problem (a missing blob, an
unreadable manifest, a size the index does not match). Nothing was changed and no safety copy was
made. Use a backup taken with `wgs backup` on a healthy store.

**A restore or import was interrupted.** The index is replaced last, so an interruption leaves the
previous index and data described. `wgs restore` also keeps a safety copy of the store as it was; an
import stops at the first failure with earlier containers fully written and later ones untouched.

**`wgs import` refuses a file.** With an export manifest, every file's size and SHA-256 must match
and no path may leave the folder. Re-export, or fix the file.
