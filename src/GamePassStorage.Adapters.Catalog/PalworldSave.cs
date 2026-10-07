using System.IO.Compression;

namespace GamePassStorage.Adapters.Catalog;

/// <summary>
/// Palworld's <c>.sav</c> wrapper around an Unreal GVAS save, as cheahjs/palworld-save-tools reads it
/// (<c>palworld_save_tools/palsav.py</c>, MIT): u32 uncompressed length, u32 compressed length, the magic <c>PlZ</c>, a
/// save-type byte, then the body, optionally behind a 12-byte <c>CNK</c> prefix that repeats the same fields. Type 0x31 is
/// zlib once, 0x32 zlib twice (the compressed length is then that of the once-decompressed data), 0x30 is not handled
/// by the source and is not decoded here either.
/// </summary>
public sealed class PalworldSave : IWgsPayloadCodec
{
    public static PalworldSave Instance { get; } = new();

    private const int MaxUncompressed = 1 << 30;

    public string Name => "palworld-sav";

    /// <summary>The wrapper's fields, or null when <paramref name="blob"/> does not start with one.</summary>
    public static (int Uncompressed, int Compressed, byte SaveType, int BodyOffset, bool Chunked)? TryReadHeader(ReadOnlySpan<byte> blob)
    {
        if (blob.Length < 12) return null;
        var offset = 0;
        var chunked = blob.Slice(8, 3).SequenceEqual("CNK"u8);
        if (chunked)
        {
            if (blob.Length < 24) return null;
            offset = 12;
        }
        if (!blob.Slice(offset + 8, 3).SequenceEqual("PlZ"u8)) return null;
        return (BitConverter.ToInt32(blob[offset..]), BitConverter.ToInt32(blob[(offset + 4)..]), blob[offset + 11], offset + 12, chunked);
    }

    /// <summary>Decompresses the wrapper to the GVAS save inside, checking both recorded lengths, or explains why not.</summary>
    public WgsCodecResult TryDecode(WgsContainer container, byte[] blob)
    {
        ArgumentNullException.ThrowIfNull(blob);
        if (TryReadHeader(blob) is not { } h) return WgsCodecResult.Unavailable("Not a Palworld .sav (no PlZ header).");
        if (h.SaveType is not (0x31 or 0x32))
        {
            return WgsCodecResult.Unavailable($"Save type 0x{h.SaveType:X2} is not one this codec decodes (0x31, 0x32).");
        }
        if (h.Uncompressed is < 0 or > MaxUncompressed) return WgsCodecResult.Unavailable($"Implausible uncompressed length {h.Uncompressed}.");
        try
        {
            var body = blob.AsSpan(h.BodyOffset).ToArray();
            if (h.SaveType == 0x31 && h.Compressed != body.Length)
            {
                return WgsCodecResult.Unavailable($"The compressed length is {body.Length}, the header says {h.Compressed}.");
            }
            var once = Inflate(body, h.SaveType == 0x31 ? h.Uncompressed : h.Compressed);
            if (h.SaveType == 0x32 && once.Length != h.Compressed)
            {
                return WgsCodecResult.Unavailable($"The once-decompressed length is {once.Length}, the header says {h.Compressed}.");
            }
            var gvas = h.SaveType == 0x32 ? Inflate(once, h.Uncompressed) : once;
            return gvas.Length == h.Uncompressed
                ? WgsCodecResult.Ok(gvas)
                : WgsCodecResult.Unavailable($"The decompressed length is {gvas.Length}, the header says {h.Uncompressed}.");
        }
        catch (InvalidDataException ex)
        {
            return WgsCodecResult.Unavailable($"The body is not valid zlib: {ex.Message}");
        }
    }

    /// <summary>Re-wrapping is not offered: which save type a given file must use is not established.</summary>
    public WgsCodecResult TryEncode(WgsContainer container, byte[] decoded)
        => WgsCodecResult.Unavailable("Re-wrapping a Palworld save is not supported yet: which save type (0x31 or 0x32) each file must use is not established.");

    /// <summary>Describes a Palworld .sav: its wrapper, and the GVAS class and engine version inside when it decodes.
    /// Null when the blob is not one, so the caller falls back to the generic description.</summary>
    public static WgsContentDescription? Describe(string name, byte[] blob)
    {
        if (TryReadHeader(blob) is not { } h) return null;
        var notes = new List<string>
        {
            $"Palworld .sav wrapper{(h.Chunked ? " (CNK prefix)" : "")}: save type 0x{h.SaveType:X2} ({TypeName(h.SaveType)}), " +
            $"{h.Uncompressed:N0} bytes uncompressed, {h.Compressed:N0} recorded as compressed.",
        };
        var type = "Palworld .sav";
        var decoded = Instance.TryDecode(new WgsContainer { Name = name }, blob);
        if (decoded.Succeeded && WgsGvas.TryReadHeader(decoded.Data) is { } gvas)
        {
            type = $"Palworld .sav ({gvas.SaveGameClass})";
            notes.Add($"Inside: Unreal Engine {gvas.EngineVersion} GVAS save of class {gvas.SaveGameClass}.");
        }
        else if (!decoded.Succeeded)
        {
            notes.Add($"Not decoded: {decoded.Message}");
        }
        return new WgsContentDescription("palworld save", $"{blob.Length:N0} bytes, {type}",
            [new WgsDescribedMember(name, blob.Length, type)], notes);
    }

    private static string TypeName(byte saveType) => saveType switch
    {
        0x30 => "not handled",
        0x31 => "zlib",
        0x32 => "zlib twice",
        _ => "unknown",
    };

    private static byte[] Inflate(byte[] data, int expected)
    {
        using var z = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
        using var output = new MemoryStream(Math.Clamp(expected, 0, MaxUncompressed));
        var buffer = new byte[81920];
        int read;
        while ((read = z.Read(buffer)) > 0)
        {
            if (output.Length + read > MaxUncompressed) throw new InvalidDataException("decompresses beyond the size limit");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
}
