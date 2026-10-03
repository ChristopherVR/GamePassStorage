namespace GamePassStorage;

/// <summary>Outcome of <see cref="WgsStore.TryRestore"/>.</summary>
/// <param name="Store">The restored store, reopened, when the restore succeeded.</param>
/// <param name="SafetyCopyPath">Where the pre-restore copy of the store was taken (null when refused before that).</param>
/// <param name="Problems">Why a backup was judged not to be a valid store.</param>
public sealed record WgsRestoreResult(WgsOperationStatus Status, WgsStore? Store, string? Message,
    string? SafetyCopyPath, IReadOnlyList<string> Problems)
{
    public bool Succeeded => Status == WgsOperationStatus.Ok;
}

// Restoring a whole-folder backup made by CopyStoreTo.
//
// Rules, following the library's write rules rather than a byte copy:
//   * the backup must be a valid store (see ValidateStore) or nothing is touched;
//   * the current store is copied to a safety folder first;
//   * the write gate and the concurrent-change check apply to the current store;
//   * files go in blob, then manifest, per container, then the index last through an atomic replace,
//     so an interruption before the index leaves the current store's index and data described;
//   * the restored index is NOT the backup's bytes: its FILETIME is set strictly above both the
//     backup's and the current store's, and FullyUploaded is cleared, so cloud sync sees the restore as
//     the newest local change. An older FILETIME could lose the restore to the cloud copy;
//   * containers the backup recorded as Synced are recorded as Modified (they keep their ETag): the
//     restored content is a local change against whatever version the cloud holds now. This is an
//     assumption from "state is set to what actually happened" in docs/wgs-format.md, not something
//     verified against a live sync;
//   * files of containers that are no longer in the index are removed only after the index commits.
public sealed partial class WgsStore
{
    /// <summary>
    /// Checks that a folder is a usable store: an index that parses, and for every container that is
    /// not a deletion tombstone a readable manifest whose current blobs are all on disk (and, for a
    /// single-blob container, whose size matches the index). Returns the problems found; empty means valid.
    /// Reads only.
    /// </summary>
    public static IReadOnlyList<string> ValidateStore(string folder, WgsStoreOptions? options = null)
    {
        var opts = options ?? WgsStoreOptions.Default;
        var problems = new List<string>();
        var opened = TryOpen(folder, opts);
        if (!opened.Succeeded)
        {
            problems.Add(opened.Message ?? $"'{folder}' is not a store.");
            return problems;
        }
        var store = opened.Store!;
        foreach (var c in store.Containers)
        {
            if (c.RawState == (uint)WgsEntryState.Deleted) continue;
            var dir = Path.Combine(folder, c.FolderName);
            try
            {
                var manifest = store.ReadManifestFile(dir, c.ContainerNumber);
                long total = 0;
                var missing = false;
                foreach (var e in manifest.Entries)
                {
                    var path = Path.Combine(dir, BlobFileName(e.LocalId));
                    if (!opts.FileSystem.FileExists(path))
                    {
                        problems.Add($"'{c.Name}': blob '{e.Name}' ({e.LocalId:N}) is missing.");
                        missing = true;
                    }
                    else
                    {
                        total += opts.FileSystem.GetFileLength(path);
                    }
                }
                if (manifest.Entries.Count == 1 && total != c.BlobSize && !missing)
                {
                    problems.Add($"'{c.Name}': blob is {total} bytes but the index records {c.BlobSize}.");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                or ArgumentException)
            {
                problems.Add($"'{c.Name}': manifest container.{c.ContainerNumber} is unusable ({ex.Message}).");
            }
        }
        return problems;
    }

    /// <summary>
    /// Restores <paramref name="backupFolder"/> (a copy made by <see cref="CopyStoreTo"/>) over the store
    /// at <paramref name="storeFolder"/>, after copying the current store to <paramref name="safetyCopyFolder"/>
    /// (which must not exist or be empty). Refuses an invalid backup, a backup of a different title
    /// (package family name), an unreadable current store, and anything the write gate refuses.
    /// </summary>
    public static WgsRestoreResult TryRestore(string storeFolder, string backupFolder, string safetyCopyFolder,
        WgsStoreOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(safetyCopyFolder);
        var opts = options ?? WgsStoreOptions.Default;
        var fs = opts.FileSystem;
        static WgsRestoreResult Refuse(string message, string? safety = null, IReadOnlyList<string>? problems = null)
            => new(WgsOperationStatus.Refused, null, message, safety, problems ?? []);

        var storeFull = Path.GetFullPath(storeFolder);
        var backupFull = Path.GetFullPath(backupFolder);
        var safetyFull = Path.GetFullPath(safetyCopyFolder);
        if (SameOrInside(storeFull, backupFull) || SameOrInside(backupFull, storeFull))
        {
            return Refuse("The backup and the store are the same folder, or one is inside the other.");
        }
        if (SameOrInside(storeFull, safetyFull) || SameOrInside(backupFull, safetyFull) || SameOrInside(safetyFull, storeFull))
        {
            return Refuse("The safety copy folder must be separate from the store and the backup.");
        }
        if (fs.DirectoryExists(safetyCopyFolder)
            && (fs.EnumerateFiles(safetyCopyFolder).Any() || fs.EnumerateDirectories(safetyCopyFolder).Any()))
        {
            return Refuse($"The safety copy folder '{safetyCopyFolder}' is not empty.");
        }

        var problems = ValidateStore(backupFolder, opts);
        if (problems.Count > 0)
        {
            return Refuse($"'{backupFolder}' is not a valid store, so nothing was changed.", null, problems);
        }

        var opened = TryOpen(storeFolder, opts);
        if (!opened.Succeeded)
        {
            return Refuse($"The current store could not be opened ({opened.Message}); it cannot be safely replaced. "
                + "Move it aside by hand first.");
        }
        var current = opened.Store!;
        var backupResult = TryOpen(backupFolder, opts);
        if (!backupResult.Succeeded) return Refuse($"The backup changed or could not be reopened: {backupResult.Message}");
        var backupOpened = backupResult.Store!;
        if (current.PackageFamilyName.Length > 0 && backupOpened.PackageFamilyName.Length > 0
            && !string.Equals(current.PackageFamilyName, backupOpened.PackageFamilyName, StringComparison.OrdinalIgnoreCase))
        {
            return Refuse($"The backup belongs to '{backupOpened.PackageFamilyName}', the store to '{current.PackageFamilyName}'.");
        }
        var assessment = current.AssessWrite();
        if (!assessment.CanWrite) return Refuse(assessment.BlockingMessage());

        try
        {
            current.CopyStoreTo(safetyCopyFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new WgsRestoreResult(IsLockConflict(ex) ? WgsOperationStatus.LockConflict : WgsOperationStatus.Failed,
                null, $"The safety copy could not be made, so nothing was changed: {ex.Message}", null, []);
        }
        var change = current.DetectExternalChange();
        if (change.Changed)
        {
            return new WgsRestoreResult(WgsOperationStatus.ConcurrentChange, null,
                string.Join(" ", change.Reasons) + " Re-open the store and try again.", safetyCopyFolder, []);
        }

        var created = new List<string>();
        var committed = false;
        try
        {
            var restored = new WgsStore(storeFolder, opts);
            restored.ParseIndex(fs.ReadAllBytes(Path.Combine(backupFolder, IndexFileName)));

            foreach (var c in restored._containers)
            {
                var from = Path.Combine(backupFolder, c.FolderName);
                var to = Path.Combine(storeFolder, c.FolderName);
                if (!fs.DirectoryExists(from))
                {
                    if (c.RawState == (uint)WgsEntryState.Deleted) continue;
                    throw new IOException($"The backup container '{c.Name}' disappeared while restoring.");
                }
                fs.CreateDirectory(to);
                var manifestName = $"container.{c.ContainerNumber}";
                var manifestSource = Path.Combine(from, manifestName);
                if (!fs.FileExists(manifestSource))
                {
                    if (c.RawState == (uint)WgsEntryState.Deleted) continue;
                    throw new IOException($"The backup manifest for '{c.Name}' disappeared while restoring.");
                }
                var manifest = backupOpened.ReadManifestFile(from, c.ContainerNumber);
                // Stage under fresh blob ids and an unused manifest number. Never overwrite any
                // existing generation before the new index commits, even when backup ids collide.
                var number = FindRestoreManifestNumber(fs, to, c.ContainerNumber, current);
                var ids = new Dictionary<Guid, Guid>();
                foreach (var id in manifest.Entries.SelectMany(e => new[] { e.LocalId, e.CloudId }).Distinct())
                {
                    var source = Path.Combine(from, BlobFileName(id));
                    if (!fs.FileExists(source))
                    {
                        if (manifest.Entries.Any(e => e.LocalId == id))
                            throw new IOException($"A current backup blob for '{c.Name}' disappeared while restoring.");
                        continue;
                    }
                    Guid fresh;
                    do { fresh = Guid.NewGuid(); } while (fs.FileExists(Path.Combine(to, BlobFileName(fresh))));
                    ids.Add(id, fresh);
                    var target = Path.Combine(to, BlobFileName(fresh));
                    created.Add(target);
                    WriteFileAtomic(fs, target, fs.ReadAllBytes(source));
                }
                var entries = manifest.Entries.Select(e => new WgsBlobEntry(e.Name,
                    ids.GetValueOrDefault(e.CloudId, e.CloudId), ids[e.LocalId])).ToList();
                created.Add(Path.Combine(to, $"container.{number}"));
                WriteManifestFile(fs, to, number, manifest.Header, entries, manifest.Tail);
                c.ContainerNumber = number;
            }

            foreach (var c in restored._containers.Where(c => c.RawState == (uint)WgsEntryState.Synced))
            {
                c.State = WgsEntryState.Modified;
                c.RawState = (uint)WgsEntryState.Modified;
            }
            // WriteIndex advances the stamp strictly beyond whatever the header holds; seed it with the newer of the two.
            var newest = Math.Max(current.IndexFileTime, restored.IndexFileTime);
            BitConverter.GetBytes(newest).CopyTo(restored._header, restored._indexFileTimeOffset);
            change = current.DetectExternalChange();
            var backupChange = backupOpened.DetectExternalChange();
            if (backupChange.Changed) change = backupChange;
            if (change.Changed)
            {
                current.RemoveBestEffort(created);
                return new WgsRestoreResult(WgsOperationStatus.ConcurrentChange, null,
                    string.Join(" ", change.Reasons), safetyCopyFolder, []);
            }
            restored.WriteIndex();
            committed = true;

            // After the index commits, cleanup failures must never remove the committed files.
            created.Clear();
            restored.PruneUnreferenced(storeFolder);
            opts.Log.Info($"wgs: restored {storeFolder} from {backupFolder}; previous store kept at {safetyCopyFolder}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
            or ArgumentException)
        {
            if (!committed) current.RemoveBestEffort(created);
            return new WgsRestoreResult(IsLockConflict(ex) ? WgsOperationStatus.LockConflict : WgsOperationStatus.Failed, null,
                committed
                    ? $"The restored index committed, but cleanup failed ({ex.Message}). The previous store is kept at {safetyCopyFolder}."
                    : $"The restore was interrupted ({ex.Message}). The index was not replaced; the previous store is also kept at {safetyCopyFolder}.",
                safetyCopyFolder, []);
        }

        var reopened = TryOpen(storeFolder, opts);
        return new WgsRestoreResult(reopened.Succeeded ? WgsOperationStatus.Ok : WgsOperationStatus.Failed,
            reopened.Store, reopened.Message, safetyCopyFolder, []);

    }

    private static byte FindRestoreManifestNumber(IWgsFileSystem fs, string folder, byte previous, WgsStore current)
    {
        for (var offset = 1; offset <= 256; offset++)
        {
            var number = unchecked((byte)(previous + offset));
            if (!fs.FileExists(Path.Combine(folder, $"container.{number}"))
                && !current._containers.Any(c => Path.Combine(current._root, c.FolderName) == folder && c.ContainerNumber == number))
                return number;
        }
        throw new IOException($"'{folder}' has no unused manifest generation; the restore cannot be staged safely.");
    }

    private static bool SameOrInside(string parent, string candidate)
    {
        var p = parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var c = candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(p, c, StringComparison.OrdinalIgnoreCase)
            || c.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || c.StartsWith(p + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>After a restore's index has committed: drop superseded generations in the folders it names,
    /// and the files of folders it no longer names. Best effort; nothing here can invalidate the index.</summary>
    private void PruneUnreferenced(string storeFolder)
    {
        var byFolder = _containers.ToDictionary(c => c.FolderName, StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var sub in _fs.EnumerateDirectories(storeFolder).ToList())
            {
                var name = Path.GetFileName(sub);
                if (name.Length != 32 || !IsHex(name)) continue;
                if (!byFolder.TryGetValue(name, out var c))
                {
                    RemoveBestEffort(_fs.EnumerateFiles(sub).ToList());
                    continue;
                }
                try
                {
                    var manifest = ReadManifestFile(sub, c.ContainerNumber);
                    var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var e in manifest.Entries)
                    {
                        keep.Add(BlobFileName(e.LocalId));
                        keep.Add(BlobFileName(e.CloudId));
                    }
                    PruneSupersededGenerations(sub, c.ContainerNumber, keep);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                    or ArgumentException)
                {
                    // No readable manifest (a tombstone): leave the folder alone.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _options.Log.Warn($"Could not tidy '{storeFolder}' after the restore: {ex.Message}");
        }
    }
}
