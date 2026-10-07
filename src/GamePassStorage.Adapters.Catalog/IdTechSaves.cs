using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace GamePassStorage.Adapters.Catalog;

/// <summary>
/// id Tech save slots (DOOM Eternal, DOOM: The Dark Ages), as brodrigz/XgpSaveTools handles them
/// (<c>SaveHandlers/Impl/DoomDarkAges/*.cs</c>, MIT). A slot container holds <c>game.details</c> (text,
/// <c>checksum=</c>, <c>slotId=</c>...), <c>game_duration.dat</c> (a <c>SlotFile</c> header: u32 version, u32 8, the
/// eight bytes <c>SlotFile</c>), a <c>-BACKUP</c> copy of each, and a <c>.checksum</c> sidecar per <c>.dat</c>: the four
/// u32 words of the data's MD5 XORed together, little-endian, in a 4-byte (Eternal) or 8-byte (The Dark Ages) file.
/// The Steam version keeps the same four files per slot, each AES-GCM encrypted with a key from the SteamID64.
/// </summary>
public sealed class IdTechSaves : IWgsDerivedBlobs
{
    public const string ChecksumSuffix = ".checksum";
    private const string GameCode = "MANCUBUS";
    private const int NonceLength = 12;
    private const int TagLength = 16;

    /// <summary>The four files a slot holds besides the checksum sidecars, and all the Steam version keeps per slot.</summary>
    public static IReadOnlyList<string> SlotFiles { get; } = ["game.details", "game.details-BACKUP", "game_duration.dat", "game_duration.dat-BACKUP"];

    private readonly int _sidecarLength;
    private readonly uint? _xboxSlotVersion;
    private readonly uint? _steamSlotVersion;

    /// <param name="sidecarLength">Bytes in a new <c>.checksum</c> file (an existing one keeps its own length).</param>
    /// <param name="xboxSlotVersion">The SlotFile version the Game Pass build expects, when it differs from Steam's.</param>
    /// <param name="steamSlotVersion">The SlotFile version the Steam build expects.</param>
    public IdTechSaves(int sidecarLength, uint? xboxSlotVersion = null, uint? steamSlotVersion = null)
    {
        if (sidecarLength is not (4 or 8)) throw new ArgumentOutOfRangeException(nameof(sidecarLength), "A checksum sidecar is 4 or 8 bytes.");
        _sidecarLength = sidecarLength;
        _xboxSlotVersion = xboxSlotVersion;
        _steamSlotVersion = steamSlotVersion;
    }

    /// <summary>DOOM: The Dark Ages: 8-byte sidecars; SlotFile version 10 on Game Pass, 11 on Steam (per the source, as of its writing).</summary>
    public static IdTechSaves DoomTheDarkAges { get; } = new(8, xboxSlotVersion: 10, steamSlotVersion: 11);

    /// <summary>DOOM Eternal: 4-byte sidecars, no SlotFile version change between stores.</summary>
    public static IdTechSaves DoomEternal { get; } = new(4);

    /// <summary>The checksum id Tech keeps beside a save file: the MD5's four little-endian u32 words XORed.</summary>
    public static uint Checksum(ReadOnlySpan<byte> data)
    {
        Span<byte> md5 = stackalloc byte[16];
#pragma warning disable CA5351 // The game's own checksum format is built on MD5; this is format compatibility, not security.
        MD5.HashData(data, md5);
#pragma warning restore CA5351
        return BinaryPrimitives.ReadUInt32LittleEndian(md5) ^ BinaryPrimitives.ReadUInt32LittleEndian(md5[4..])
            ^ BinaryPrimitives.ReadUInt32LittleEndian(md5[8..]) ^ BinaryPrimitives.ReadUInt32LittleEndian(md5[12..]);
    }

    /// <summary>A sidecar of <paramref name="length"/> bytes (4 or 8) holding <see cref="Checksum"/>, zero padded.</summary>
    public static byte[] Sidecar(ReadOnlySpan<byte> data, int length)
    {
        var output = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(output, Checksum(data));
        return output;
    }

    /// <summary>Recomputes <c>X.checksum</c> for every changed blob <c>X</c> that has one, keeping the sidecar's length.</summary>
    public IReadOnlyDictionary<string, byte[]> Derive(string containerName, IReadOnlyDictionary<string, byte[]> changes,
        IReadOnlyDictionary<string, byte[]> after)
    {
        var derived = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var name in changes.Keys)
        {
            if (name.EndsWith(ChecksumSuffix, StringComparison.Ordinal)) continue;
            var sidecar = name + ChecksumSuffix;
            if (!after.TryGetValue(sidecar, out var existing) && !name.StartsWith("game_duration.dat", StringComparison.Ordinal)) continue;
            var length = existing?.Length is 4 or 8 ? existing.Length : _sidecarLength;
            derived[sidecar] = Sidecar(after[name], length);
        }
        return derived;
    }

    /// <summary>The SlotFile version in a <c>game_duration.dat</c>, or null when it has no SlotFile header.</summary>
    public static uint? SlotFileVersion(ReadOnlySpan<byte> data)
        => data.Length >= 16 && BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) == 8 && data.Slice(8, 8).SequenceEqual("SlotFile"u8)
            ? BinaryPrimitives.ReadUInt32LittleEndian(data)
            : null;

    private static byte[] WithSlotVersion(string fileName, byte[] data, uint? version)
    {
        if (version is not { } v || !fileName.StartsWith("game_duration.dat", StringComparison.Ordinal)) return data;
        if (SlotFileVersion(data) is not { } current) throw new InvalidDataException($"'{fileName}' has no SlotFile header.");
        if (current == v) return data;
        var patched = (byte[])data.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(patched, v);
        return patched;
    }

    /// <summary>Describes one blob of a slot container, or null for a blob this does not recognise.</summary>
    public static WgsContentDescription? Describe(string name, byte[] blob)
    {
        var leaf = name[(name.LastIndexOf('/') + 1)..];
        if (leaf.StartsWith("game_duration.dat", StringComparison.Ordinal) && !leaf.EndsWith(ChecksumSuffix, StringComparison.Ordinal))
        {
            var version = SlotFileVersion(blob);
            return new WgsContentDescription("id Tech save slot", $"{blob.Length:N0} bytes, SlotFile {(version is { } v ? $"version {v}" : "header missing")}",
                [new WgsDescribedMember(name, blob.Length, "SlotFile")],
                version is null ? ["Expected a SlotFile header (u32 version, u32 8, 'SlotFile')."] : []);
        }
        if (leaf.EndsWith(ChecksumSuffix, StringComparison.Ordinal))
        {
            return new WgsContentDescription("id Tech checksum", $"{blob.Length}-byte checksum of {leaf[..^ChecksumSuffix.Length]}",
                [new WgsDescribedMember(name, blob.Length, "checksum", blob.Length >= 4 ? $"0x{BinaryPrimitives.ReadUInt32LittleEndian(blob):X8}" : null)],
                ["MD5 of the data file with its four u32 words XORed. Recomputed automatically when the data file is written."]);
        }
        if (leaf.StartsWith("game.details", StringComparison.Ordinal))
        {
            var keys = Encoding.UTF8.GetString(blob).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(l => l.Split('=', 2)[0]).Where(k => k.Length > 0).Distinct().ToList();
            return new WgsContentDescription("id Tech slot details", $"{blob.Length:N0} bytes of key=value text",
                [new WgsDescribedMember(name, blob.Length, "text", string.Join(", ", keys))], []);
        }
        return null;
    }

    /// <summary>
    /// The Steam form of each slot: <c>&lt;slot&gt;/&lt;file&gt;</c> for the four slot files, encrypted for
    /// <paramref name="steamId64"/>, with the SlotFile version set to Steam's. Profile and checksum files stay out (Steam
    /// keeps its own profile and no sidecars). Wrapping decrypts with the same id, restores the Game Pass SlotFile
    /// version, and only replaces slots that already exist; their checksums are then recomputed by <see cref="Derive"/>.
    /// </summary>
    public IWgsNativeLayout SteamLayout(string steamId64)
    {
        if (steamId64.Length != 17 || !steamId64.All(char.IsAsciiDigit))
        {
            throw new ArgumentException("A SteamID64 is 17 digits.", nameof(steamId64));
        }
        return new SteamSlots(this, steamId64);
    }

    private static bool IsSlot(WgsNativeContext context, string container)
    {
        var blobs = context.BlobNamesOf(container);
        return SlotFiles.All(f => blobs.Contains(f, StringComparer.Ordinal));
    }

    private sealed class SteamSlots(IdTechSaves game, string steamId) : IWgsNativeLayout
    {
        public string Name => $"steam:{steamId}";

        public string? ToNativePath(WgsNativeContext context, string container, string blobName, int blobCount)
            => SlotFiles.Contains(blobName, StringComparer.Ordinal) && IsSlot(context, container) ? $"{container}/{blobName}" : null;

        public WgsNativeTarget? FromNativePath(WgsNativeContext context, string relativePath)
            => WgsNativeLayouts.SplitLast(relativePath) is { } p && SlotFiles.Contains(p.File, StringComparer.Ordinal) && IsSlot(context, p.Folder)
                ? new WgsNativeTarget(p.Folder, p.File)
                : null;

        public byte[] ToNativeContent(string container, string blobName, byte[] blob)
            => Encrypt(WithSlotVersion(blobName, blob, game._steamSlotVersion), blobName, steamId);

        public byte[] ToBlobContent(string? container, string? blobName, byte[] native)
        {
            ArgumentNullException.ThrowIfNull(blobName);
            return WithSlotVersion(blobName, Decrypt(native, blobName, steamId), game._xboxSlotVersion);
        }
    }

    private static byte[] Key(string fileName, string steamId, out byte[] material)
    {
        material = Encoding.ASCII.GetBytes(steamId + GameCode + fileName);
        return SHA256.HashData(material)[..16];
    }

    public static byte[] Encrypt(ReadOnlySpan<byte> plaintext, string fileName, string steamId, ReadOnlySpan<byte> nonce = default)
    {
        var key = Key(fileName, steamId, out var material);
        var output = new byte[NonceLength + plaintext.Length + TagLength];
        var n = output.AsSpan(0, NonceLength);
        if (nonce.IsEmpty) RandomNumberGenerator.Fill(n); else nonce.CopyTo(n);
        using var aes = new AesGcm(key, TagLength);
        aes.Encrypt(n, plaintext, output.AsSpan(NonceLength, plaintext.Length), output.AsSpan(NonceLength + plaintext.Length), material);
        return output;
    }

    public static byte[] Decrypt(ReadOnlySpan<byte> encrypted, string fileName, string steamId)
    {
        if (encrypted.Length < NonceLength + TagLength) throw new InvalidDataException($"'{fileName}' is too short to be an encrypted save.");
        var key = Key(fileName, steamId, out var material);
        var plaintext = new byte[encrypted.Length - NonceLength - TagLength];
        try
        {
            using var aes = new AesGcm(key, TagLength);
            aes.Decrypt(encrypted[..NonceLength], encrypted[NonceLength..^TagLength], encrypted[^TagLength..], plaintext, material);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidDataException($"'{fileName}' does not decrypt with that SteamID64 (wrong account, or not a Steam save).", ex);
        }
        return plaintext;
    }

    /// <summary>Parses <c>steam:&lt;SteamID64&gt;</c>; null for any other spec.</summary>
    public IWgsNativeLayout? ParseLayout(string spec)
        => spec.StartsWith("steam:", StringComparison.OrdinalIgnoreCase) ? SteamLayout(spec["steam:".Length..]) : null;
}
