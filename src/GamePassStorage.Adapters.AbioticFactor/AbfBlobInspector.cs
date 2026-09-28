namespace GamePassStorage.Adapters.AbioticFactor;

/// <summary>
/// Names a leftover world folder from its bundle's table of contents, which sits uncompressed at
/// the front of the blob. Needs no Oodle library, so it works on any machine.
/// </summary>
public sealed class AbfBlobInspector : IWgsBlobInspector
{
    /// <summary>The first member's path is a few hundred bytes in; reading more would make listing orphans feel slow.</summary>
    public int HeadBytes => 8192;

    public WgsBlobDescription? Inspect(ReadOnlySpan<byte> head)
    {
        var pos = 0;
        if (AbfBundleToc.ReadString(head, ref pos) != AbfBundleToc.Marker) return null;
        pos += 16;   // version, two opaque header ints, member count
        if (pos >= head.Length) return null;
        var first = AbfBundleToc.ReadString(head, ref pos);
        var world = AbfBundleToc.WorldNameFromPath(first);
        return world is null ? null : new WgsBlobDescription(world, world + AbioticContainers.WorldSuffix);
    }
}
