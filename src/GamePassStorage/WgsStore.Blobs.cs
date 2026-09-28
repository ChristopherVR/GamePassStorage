namespace GamePassStorage;

// Multi-blob containers. A container.N manifest may name several blobs (see WgsBlobEntry for the
// layout and its sources). Every write here replaces only the blobs it names: untouched blobs keep
// their files and ids, so their bytes are preserved exactly.
public sealed partial class WgsStore
{
    /// <summary>Reads every blob of a container by name. Throws when any cannot be read safely.</summary>
    public IReadOnlyDictionary<string, byte[]> ReadBlobs(WgsContainer container)
    {
        var r = TryReadBlobs(container);
        if (!r.Succeeded) throw new InvalidDataException(r.Message);
        return r.Blobs!;
    }

    /// <summary>
    /// Reads all blobs of a container (one or many) as name to bytes. Each blob follows the same rules
    /// as <see cref="TryReadBlob"/>: two differing ids on disk is <see cref="WgsOperationStatus.SyncInFlight"/>,
    /// a missing current id falls back to the id the manifest records for the cloud. The folder-scan
    /// last resort is used only when the manifest names a single blob. A malformed manifest is
    /// <see cref="WgsOperationStatus.UnsupportedLayout"/>.
    /// </summary>
    public WgsBlobsReadResult TryReadBlobs(WgsContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);
        try
        {
            var folder = Path.Combine(_root, container.FolderName);
            var manifest = ReadManifestFile(folder, container.ContainerNumber);
            var blobs = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var fallback = false;
            foreach (var entry in manifest.Entries)
            {
                var r = ResolveEntry(container, folder, entry, manifest.Entries.Count == 1,
                    manifest.Entries.Count == 1 ? container.Name : $"{container.Name}/{entry.Name}");
                if (!r.Succeeded) return new WgsBlobsReadResult(r.Status, null, r.Message, false);
                blobs[entry.Name] = r.Blob!;
                fallback |= r.UsedFallback;
            }
            return new WgsBlobsReadResult(WgsOperationStatus.Ok, blobs, null, fallback);
        }
        catch (Exception ex) when (IsLockConflict(ex))
        {
            return new WgsBlobsReadResult(WgsOperationStatus.LockConflict, null, ex.Message, false);
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or InvalidDataException)
        {
            return new WgsBlobsReadResult(WgsOperationStatus.UnsupportedLayout, null,
                $"The manifest for '{container.Name}' is malformed: {ex.Message}", false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new WgsBlobsReadResult(WgsOperationStatus.Failed, null, ex.Message, false);
        }
    }

    /// <summary>Lists the blobs a container's manifest names, with ids and on-disk sizes. Reads only.</summary>
    public WgsBlobListResult TryListBlobs(WgsContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);
        try
        {
            var folder = Path.Combine(_root, container.FolderName);
            var manifest = ReadManifestFile(folder, container.ContainerNumber);
            var list = new List<WgsBlobInfo>();
            foreach (var e in manifest.Entries)
            {
                var path = Path.Combine(folder, BlobFileName(e.LocalId));
                list.Add(new WgsBlobInfo(e.Name, e.CloudId, e.LocalId, _fs.FileExists(path) ? _fs.GetFileLength(path) : null));
            }
            return new WgsBlobListResult(WgsOperationStatus.Ok, list, null);
        }
        catch (Exception ex) when (IsLockConflict(ex))
        {
            return new WgsBlobListResult(WgsOperationStatus.LockConflict, null, ex.Message);
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or InvalidDataException)
        {
            return new WgsBlobListResult(WgsOperationStatus.UnsupportedLayout, null,
                $"The manifest for '{container.Name}' is malformed: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new WgsBlobListResult(WgsOperationStatus.Failed, null, ex.Message);
        }
    }

    /// <summary>Replaces one named blob (or adds it), keeping every other blob byte-for-byte. Throws when the gate refuses.</summary>
    public void WriteNamedBlob(WgsContainer container, string blobName, byte[] blob)
    {
        ArgumentNullException.ThrowIfNull(blob);
        ArgumentException.ThrowIfNullOrEmpty(blobName);
        WriteBlobs(container, new Dictionary<string, byte[]>(StringComparer.Ordinal) { [blobName] = blob });
    }

    /// <summary>
    /// Replaces or adds the named blobs in one new generation of the container (blobs, then manifest,
    /// then index, then prune). Blobs not named are untouched. Refuses when an untouched blob is
    /// missing or mid-sync, since the new manifest would misstate it.
    /// </summary>
    public void WriteBlobs(WgsContainer container, IReadOnlyDictionary<string, byte[]> changes)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(changes);
        CommitGeneration(container, changes, legacySingle: false);
    }

    /// <summary>Typed-result form of <see cref="WriteNamedBlob"/>.</summary>
    public WgsCommitResult TryWriteNamedBlob(WgsContainer container, string blobName, byte[] blob)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(blob);
        ArgumentException.ThrowIfNullOrEmpty(blobName);
        return Commit(container.Name, () => { WriteNamedBlob(container, blobName, blob); return container; });
    }

    /// <summary>Typed-result form of <see cref="WriteBlobs"/>.</summary>
    public WgsCommitResult TryWriteBlobs(WgsContainer container, IReadOnlyDictionary<string, byte[]> changes)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(changes);
        return Commit(container.Name, () => { WriteBlobs(container, changes); return container; });
    }

    /// <summary>
    /// Creates a new container holding several named blobs. The container is never-uploaded
    /// (<see cref="WgsEntryState.Created"/>, no ETag). Throws when the name is taken or the gate refuses.
    /// The index size recorded is the total of all blob bytes (an assumption: public sources describe
    /// the field for single-blob containers only).
    /// </summary>
    public WgsContainer CreateContainer(string containerName, IReadOnlyDictionary<string, byte[]> blobs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        ArgumentNullException.ThrowIfNull(blobs);
        return CreateContainerCore(containerName, blobs);
    }

    /// <summary>Typed-result form of <see cref="CreateContainer"/>.</summary>
    public WgsCommitResult TryCreateContainer(string containerName, IReadOnlyDictionary<string, byte[]> blobs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        ArgumentNullException.ThrowIfNull(blobs);
        return Commit(containerName, () => CreateContainer(containerName, blobs));
    }
}
