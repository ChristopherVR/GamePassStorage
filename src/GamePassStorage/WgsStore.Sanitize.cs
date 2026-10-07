namespace GamePassStorage;

/// <summary>Outcome of <see cref="WgsStore.TrySanitizedCopyTo"/>.</summary>
/// <param name="StructureFiles">Files copied byte for byte (<c>containers.index</c> and every <c>container.N</c>).</param>
/// <param name="BlobsReplaced">Blob files written as same-sized filler.</param>
/// <param name="Skipped">Files left out (anything that is not part of the store's layout), with the reason.</param>
public sealed record WgsSanitizeResult(WgsOperationStatus Status, int StructureFiles, int BlobsReplaced,
    IReadOnlyList<string> Skipped, string? Message)
{
    public bool Succeeded => Status == WgsOperationStatus.Ok;
}

// A sanitized copy keeps everything that describes the store (the index, every manifest, every file name and
// size) and nothing that is the game's data: each blob file, current, previous or orphaned, becomes filler of
// the same length. That is the shape a fixture needs and all a format bug report needs. The index and
// manifests hold container and blob names, ETags and timestamps but no account details; the account id is
// the store's folder name, which the caller chooses for the copy.
public sealed partial class WgsStore
{
    /// <summary>The byte every blob is replaced with in a sanitized copy.</summary>
    public const byte SanitizeFiller = 0;

    /// <summary>
    /// Writes a shareable copy of the store to <paramref name="destination"/> (empty or absent): the index and
    /// manifests verbatim, every other file in a container folder replaced by <see cref="SanitizeFiller"/> bytes
    /// of the same length. Reads the store only. Files outside container folders are left out and listed.
    /// </summary>
    public WgsSanitizeResult TrySanitizedCopyTo(string destination)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        if (_fs.DirectoryExists(destination) && (_fs.EnumerateFiles(destination).Any() || _fs.EnumerateDirectories(destination).Any()))
        {
            return new WgsSanitizeResult(WgsOperationStatus.Refused, 0, 0, [], $"'{destination}' is not empty.");
        }
        var written = new List<string>();
        var skipped = new List<string>();
        int structure = 0, blobs = 0;
        try
        {
            _fs.CreateDirectory(destination);
            foreach (var file in _fs.EnumerateFiles(_root))
            {
                var name = Path.GetFileName(file);
                if (!string.Equals(name, IndexFileName, StringComparison.OrdinalIgnoreCase))
                {
                    skipped.Add($"{name}: not part of the store layout");
                    continue;
                }
                var index = _fs.ReadAllBytes(file);
                NeutraliseRootGuid(index);
                var to = Path.Combine(destination, name);
                _fs.WriteAllBytes(to, index);
                written.Add(to);
                structure++;
            }
            foreach (var dir in _fs.EnumerateDirectories(_root))
            {
                var dirName = Path.GetFileName(dir);
                if (!Guid.TryParseExact(dirName, "N", out _))
                {
                    skipped.Add($"{dirName}/: not a container folder");
                    continue;
                }
                var target = Path.Combine(destination, dirName);
                _fs.CreateDirectory(target);
                foreach (var sub in _fs.EnumerateDirectories(dir)) skipped.Add($"{dirName}/{Path.GetFileName(sub)}/: unexpected folder");
                foreach (var file in _fs.EnumerateFiles(dir))
                {
                    var name = Path.GetFileName(file);
                    var to = Path.Combine(target, name);
                    if (name.StartsWith("container.", StringComparison.OrdinalIgnoreCase))
                    {
                        Copy(file, to);
                        structure++;
                    }
                    else
                    {
                        var filler = new byte[_fs.GetFileLength(file)];
                        Array.Fill(filler, SanitizeFiller);
                        _fs.WriteAllBytes(to, filler);
                        written.Add(to);
                        blobs++;
                    }
                }
            }
            return new WgsSanitizeResult(WgsOperationStatus.Ok, structure, blobs, skipped, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            RemoveBestEffort(written);
            return new WgsSanitizeResult(IsLockConflict(ex) ? WgsOperationStatus.LockConflict : WgsOperationStatus.Failed,
                structure, blobs, skipped, ex.Message);
        }

        void Copy(string from, string to)
        {
            _fs.WriteAllBytes(to, _fs.ReadAllBytes(from));
            written.Add(to);
        }
    }

    /// <summary>
    /// Overwrites the index header's root GUID string in place with zeros of the same length (dashes kept), so the
    /// layout and every offset stay identical. Its meaning is undocumented and it may identify a device or install.
    /// </summary>
    private static void NeutraliseRootGuid(byte[] index)
    {
        var pos = 12;                                       // version, count, reserved
        pos += 4 + 2 * (int)BitConverter.ToUInt32(index, pos);   // package family name
        pos += 8 + 4;                                       // index FILETIME, sync flags
        var chars = (int)BitConverter.ToUInt32(index, pos);
        pos += 4;
        if (chars < 0 || pos + chars * 2 > index.Length) throw new InvalidDataException("The index header is truncated.");
        for (var i = 0; i < chars; i++)
        {
            if (index[pos + i * 2] != (byte)'-' || index[pos + i * 2 + 1] != 0)
            {
                index[pos + i * 2] = (byte)'0';
                index[pos + i * 2 + 1] = 0;
            }
        }
    }
}
