namespace GamePassStorage;

/// <summary>
/// One blob entry of a <c>container.N</c> manifest. Manifest layout (<c>u32</c> constant 4, <c>u32</c>
/// blob count, then per blob a 128-byte UTF-16 name and two 16-byte GUIDs, nothing after the last
/// entry) as documented by Z1ni/XGP-save-extractor (<c>main.py</c>), and consistent with
/// LukeFZ/XblContainerReader, Fr33dan/GPSaveConverter and libNOM.io. The first GUID is the blob id as
/// the cloud last knew it, the second the file on disk; they differ only while a sync is in flight.
/// </summary>
public sealed record WgsBlobEntry(string Name, Guid CloudId, Guid LocalId);

/// <summary>A blob a container holds, with the size of its file when that file is on disk.</summary>
public sealed record WgsBlobInfo(string Name, Guid CloudId, Guid LocalId, long? Size)
{
    /// <summary>The two ids differ: Xbox is part-way through syncing this blob.</summary>
    public bool SyncInFlight => CloudId != LocalId;
}

/// <summary>Outcome of reading every blob of a container.</summary>
/// <param name="Blobs">Blob name to bytes, in manifest order. Null unless the read succeeded.</param>
public sealed record WgsBlobsReadResult(WgsOperationStatus Status, IReadOnlyDictionary<string, byte[]>? Blobs,
    string? Message, bool UsedFallback)
{
    public bool Succeeded => Status == WgsOperationStatus.Ok;
}

/// <summary>Outcome of listing the blobs a container's manifest names.</summary>
public sealed record WgsBlobListResult(WgsOperationStatus Status, IReadOnlyList<WgsBlobInfo>? Blobs, string? Message)
{
    public bool Succeeded => Status == WgsOperationStatus.Ok;
}
