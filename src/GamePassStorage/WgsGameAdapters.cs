namespace GamePassStorage;

/// <summary>One member (file, section, table entry) found inside a blob.</summary>
/// <param name="Name">Member name or path as the payload records it.</param>
/// <param name="Size">Size in bytes when the payload records one.</param>
/// <param name="Type">A short type or class name, when the payload records one.</param>
/// <param name="Note">Anything worth saying about this member.</param>
public sealed record WgsDescribedMember(string Name, long? Size = null, string? Type = null, string? Note = null);

/// <summary>What a game adapter found inside a blob, without changing it.</summary>
/// <param name="Kind">A short classification, e.g. <c>world bundle</c>; <c>opaque</c> when nothing recognised it.</param>
/// <param name="Summary">One line for a person.</param>
/// <param name="Members">What the blob holds, in the order the payload lists it.</param>
/// <param name="Notes">Caveats and observations (for example that a body was not decoded, and why).</param>
/// <param name="Preview">Decoded text worth showing, when the payload is text. Null otherwise.</param>
public sealed record WgsContentDescription(string Kind, string Summary, IReadOnlyList<WgsDescribedMember> Members,
    IReadOnlyList<string> Notes, string? Preview = null)
{
    /// <summary>
    /// The fallback used when no adapter matched or an adapter does not recognise a blob. It looks only at the
    /// blob: size, SHA-256 and the leading bytes, and names what they show (GVAS magic, zlib, gzip, zip, PNG,
    /// or text, which is called ini-like when it holds a <c>[Section]</c> header).
    /// </summary>
    public static WgsContentDescription Generic(string blobName, byte[] blob)
    {
        ArgumentNullException.ThrowIfNull(blob);
        var head = blob.AsSpan(0, Math.Min(blob.Length, 16));
        var type = Sniff(blob);
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(blob));
        return new WgsContentDescription("opaque", $"{blob.Length:N0} bytes of {type}",
            [new WgsDescribedMember(blobName, blob.Length, type, $"sha256 {sha}; starts {Convert.ToHexString(head)}")],
            ["No game-specific adapter recognised this payload."]);
    }

    private static string Sniff(byte[] blob)
    {
        if (blob.Length >= 4 && blob[0] == (byte)'G' && blob[1] == (byte)'V' && blob[2] == (byte)'A' && blob[3] == (byte)'S')
        {
            return "GVAS (Unreal save)";
        }
        if (blob.Length >= 2 && blob[0] == 0x1F && blob[1] == 0x8B) return "gzip";
        if (blob.Length >= 2 && blob[0] == 0x78 && blob[1] is 0x01 or 0x5E or 0x9C or 0xDA
            && ((blob[0] << 8) | blob[1]) % 31 == 0) return "zlib stream";
        if (blob.Length >= 4 && blob[0] == (byte)'P' && blob[1] == (byte)'K' && blob[2] == 3 && blob[3] == 4) return "zip archive";
        if (blob.Length >= 4 && blob[0] == 0x89 && blob[1] == (byte)'P' && blob[2] == (byte)'N' && blob[3] == (byte)'G') return "PNG image";
        if (blob.Length > 0 && blob.Take(2048).All(b => b is 9 or 10 or 13 || (b >= 32 && b < 127)))
        {
            var text = System.Text.Encoding.ASCII.GetString(blob, 0, Math.Min(blob.Length, 2048));
            return text.Split('\n').Any(l => l.Trim() is { Length: > 2 } t && t[0] == '[' && t[^1] == ']') ? "text (ini-like)" : "text";
        }
        return "binary";
    }
}

/// <summary>Outcome of a codec call. A codec that cannot run says so; it never fakes a result.</summary>
public sealed record WgsCodecResult(bool Succeeded, byte[]? Data, string? Message)
{
    public static WgsCodecResult Ok(byte[] data) => new(true, data, null);

    /// <summary>The codec cannot do this here (for example a native library is not installed).</summary>
    public static WgsCodecResult Unavailable(string reason) => new(false, null, reason);
}

/// <summary>
/// Optional game adapter hook: turns a blob into an editable form and back. Availability is decided
/// per call, because a codec may handle some payloads (a text ini) and not others (a compressed bundle).
/// </summary>
public interface IWgsPayloadCodec
{
    string Name { get; }

    /// <summary>Decodes a container's blob, or explains why it cannot.</summary>
    WgsCodecResult TryDecode(WgsContainer container, byte[] blob);

    /// <summary>Encodes edited content back into a blob, or explains why it cannot.</summary>
    WgsCodecResult TryEncode(WgsContainer container, byte[] decoded);
}

/// <summary>
/// The contract a game adapter (plugin) implements to teach the container layer about one title.
/// Only <see cref="Id"/>, <see cref="DisplayName"/>, <see cref="KnownPackageFamilyNames"/>,
/// <see cref="Matches"/> and <see cref="Describe(WgsContainer, byte[])"/> are required; the rest have
/// defaults. Adapters must not throw from any member for ordinary input, and must not write anything:
/// they describe, and the store's own gate and write path stay in charge of every change.
/// </summary>
public interface IWgsGameAdapter
{
    /// <summary>A stable machine-readable id, e.g. <c>abiotic-factor</c>. Unique within a registry.</summary>
    string Id { get; }

    string DisplayName { get; }

    /// <summary>Package family names this adapter is known to serve (without the <c>!App</c> suffix).</summary>
    IReadOnlyList<string> KnownPackageFamilyNames { get; }

    /// <summary>True when this adapter serves the store whose index records <paramref name="packageFamilyName"/>
    /// (given as the index writes it, possibly with a <c>!AppId</c> suffix; see
    /// <see cref="WgsGameAdapterRegistry.FamilyOf"/>).</summary>
    bool Matches(string packageFamilyName);

    /// <summary>Recognises the game's payloads from leading bytes, to name orphaned data. Optional.</summary>
    IWgsBlobInspector? BlobInspector => null;

    /// <summary>The game's own write gate (for instance refuse while the game runs, built with
    /// <see cref="WgsWriteGates.RefuseWhileRunning(string[])"/>). It is added to the structural gate. Optional.</summary>
    IWgsWriteGate? WriteGate => null;

    /// <summary>Human-readable container-name conventions, e.g. <c>&lt;World&gt;-WC: the world bundle</c>. Optional.</summary>
    IReadOnlyList<string> ContainerNameConventions => [];

    /// <summary>Decode/encode hook for the game's payloads. Null when the adapter offers none.</summary>
    IWgsPayloadCodec? Codec => null;

    /// <summary>How this game's containers map to the plain save files it keeps outside Xbox (the Steam or Epic layout),
    /// so a save can be taken out of the wrapper and put back. Null when the adapter offers none; the
    /// declarative layouts in <see cref="WgsNativeLayouts"/> can still be chosen by hand.</summary>
    IWgsNativeLayout? NativeLayout => null;

    /// <summary>Describes what a container's blob holds. Return <see cref="WgsContentDescription.Generic"/>
    /// output for a payload this adapter does not recognise.</summary>
    WgsContentDescription Describe(WgsContainer container, byte[] blob);

    /// <summary>Describes one named blob of a multi-blob container. Defaults to
    /// <see cref="Describe(WgsContainer, byte[])"/>.</summary>
    WgsContentDescription Describe(WgsContainer container, string blobName, byte[] blob) => Describe(container, blob);
}

/// <summary>A store opened with the adapter that served it. <c>Adapter</c> is never null when the store opened:
/// <see cref="GenericWgsAdapter"/> answers when nothing more specific matched.</summary>
public sealed record WgsAdapterOpenResult(WgsOpenResult Open, IWgsGameAdapter? Adapter);

/// <summary>
/// The adapter used when no game-specific one matches. It matches every store, is always last in
/// resolution, offers no inspector, gate or codec, and describes a blob by size, hash and content sniffing only.
/// It exists so "which adapter served this store" always has an answer and the generic model keeps working
/// for every title.
/// </summary>
public sealed class GenericWgsAdapter : IWgsGameAdapter
{
    public const string GenericId = "generic";

    public static GenericWgsAdapter Instance { get; } = new();

    public string Id => GenericId;
    public string DisplayName => "Generic (no game-specific adapter)";
    public IReadOnlyList<string> KnownPackageFamilyNames => [];
    public bool Matches(string packageFamilyName) => true;
    public WgsContentDescription Describe(WgsContainer container, byte[] blob)
    {
        ArgumentNullException.ThrowIfNull(container);
        return WgsContentDescription.Generic(container.Name, blob);
    }

    public WgsContentDescription Describe(WgsContainer container, string blobName, byte[] blob)
        => WgsContentDescription.Generic($"{container.Name}/{blobName}", blob);
}

/// <summary>
/// Holds the game adapters a host knows about and resolves one for a store by its package family
/// name. Resolution is by specificity (see <see cref="Resolve(string)"/>); registration order breaks ties. The
/// generic fallback is not registered: it is always last.
/// </summary>
public sealed class WgsGameAdapterRegistry
{
    private readonly List<IWgsGameAdapter> _adapters = [];

    public IReadOnlyList<IWgsGameAdapter> Adapters => _adapters;

    /// <summary>Adds an adapter. Throws when another adapter already has the same <see cref="IWgsGameAdapter.Id"/> (ignoring case).</summary>
    public void Register(IWgsGameAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        if (string.IsNullOrWhiteSpace(adapter.Id)) throw new ArgumentException("An adapter needs an Id.", nameof(adapter));
        if (string.Equals(adapter.Id, GenericWgsAdapter.GenericId, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"The id '{GenericWgsAdapter.GenericId}' is reserved for the built-in fallback.", nameof(adapter));
        }
        if (_adapters.Any(a => string.Equals(a.Id, adapter.Id, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException($"An adapter with the id '{adapter.Id}' is already registered.", nameof(adapter));
        }
        _adapters.Add(adapter);
    }

    public bool Unregister(string id)
        => _adapters.RemoveAll(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase)) > 0;

    /// <summary>The package family without the <c>!AppId</c> suffix the index adds
    /// (<c>Publisher.Game_abc!AppGameShipping</c> becomes <c>Publisher.Game_abc</c>).</summary>
    public static string FamilyOf(string packageFamilyName)
    {
        if (string.IsNullOrEmpty(packageFamilyName)) return string.Empty;
        var bang = packageFamilyName.IndexOf('!', StringComparison.Ordinal);
        return (bang < 0 ? packageFamilyName : packageFamilyName[..bang]).Trim();
    }

    /// <summary>
    /// The adapter serving a package family name: the most specific registered match, else
    /// <see cref="GenericWgsAdapter"/>. Never null. An adapter whose <see cref="IWgsGameAdapter.KnownPackageFamilyNames"/>
    /// names the family exactly beats one that only matches by its own rule (a substring, a pattern); among equals
    /// the one registered first wins, so the outcome is deterministic. An adapter that throws from
    /// <c>Matches</c> is skipped.
    /// </summary>
    public IWgsGameAdapter Resolve(string packageFamilyName) => ResolveSpecific(packageFamilyName) ?? GenericWgsAdapter.Instance;

    /// <summary>Like <see cref="Resolve(string)"/> but null instead of the generic fallback when nothing specific matches.</summary>
    public IWgsGameAdapter? ResolveSpecific(string packageFamilyName)
    {
        if (string.IsNullOrWhiteSpace(packageFamilyName)) return null;
        var family = FamilyOf(packageFamilyName);
        IWgsGameAdapter? best = null;
        var bestScore = 0;
        foreach (var adapter in _adapters)
        {
            try
            {
                if (!adapter.Matches(packageFamilyName)) continue;
                var exact = adapter.KnownPackageFamilyNames.Any(k => string.Equals(FamilyOf(k), family, StringComparison.OrdinalIgnoreCase));
                var score = exact ? 2 : 1;
                if (score > bestScore) { best = adapter; bestScore = score; }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A faulty plugin must not take resolution down with it; it simply does not match.
            }
        }
        return best;
    }

    /// <summary>The adapter serving an open store (see <see cref="Resolve(string)"/>).</summary>
    public IWgsGameAdapter Resolve(WgsStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        return Resolve(store.PackageFamilyName);
    }

    /// <summary>
    /// Builds options for a store served by <paramref name="adapter"/>: the adapter's blob inspector (unless
    /// <paramref name="baseOptions"/> already has one) and its write gate added to
    /// <paramref name="baseOptions"/>' gate (or to <see cref="WgsWriteGates.Structural"/> when it has none).
    /// With a null adapter, <paramref name="baseOptions"/> is returned unchanged.
    /// </summary>
    public static WgsStoreOptions CreateOptions(IWgsGameAdapter? adapter, WgsStoreOptions? baseOptions = null)
    {
        var baseline = baseOptions ?? WgsStoreOptions.Default;
        if (adapter is null) return baseline;
        var gate = adapter.WriteGate;
        return new WgsStoreOptions
        {
            FileSystem = baseline.FileSystem,
            Clock = baseline.Clock,
            Log = baseline.Log,
            BlobInspector = baseline.BlobInspector ?? adapter.BlobInspector,
            WriteGate = gate is null
                ? baseline.WriteGate
                : WgsWriteGates.Combine(baseline.WriteGate ?? WgsWriteGates.Structural, gate),
        };
    }

    /// <summary>Options for whichever adapter serves <paramref name="packageFamilyName"/> (see <see cref="CreateOptions(IWgsGameAdapter?, WgsStoreOptions?)"/>).</summary>
    public WgsStoreOptions CreateOptions(string packageFamilyName, WgsStoreOptions? baseOptions = null)
        => CreateOptions(Resolve(packageFamilyName), baseOptions);

    /// <summary>
    /// Opens a store, works out its adapter from the package family name in its index, and (when a
    /// game-specific one matches) reopens it with that adapter's inspector and gate applied. The
    /// returned store is the one to use for writes.
    /// </summary>
    public WgsAdapterOpenResult TryOpen(string folder, WgsStoreOptions? baseOptions = null)
    {
        var first = WgsStore.TryOpen(folder, baseOptions);
        if (!first.Succeeded) return new WgsAdapterOpenResult(first, null);
        var adapter = Resolve(first.Store!);
        if (adapter is GenericWgsAdapter) return new WgsAdapterOpenResult(first, adapter);
        var second = WgsStore.TryOpen(folder, CreateOptions(adapter, baseOptions));
        return new WgsAdapterOpenResult(second, adapter);
    }
}
