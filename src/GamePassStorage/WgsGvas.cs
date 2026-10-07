using System.Text;

namespace GamePassStorage;

/// <summary>What an Unreal Engine save (<c>GVAS</c>) records before its properties.</summary>
/// <param name="SaveGameVersion">The GVAS header version (3 and later also record a UE5 package version).</param>
/// <param name="EngineVersion">e.g. <c>5.4.2-0+++UE5+Release-5.4</c>: major.minor.patch-changelist+branch.</param>
/// <param name="SaveGameClass">The save-game class path, e.g. <c>/Script/Pal.PalWorldSaveGame</c>.</param>
/// <param name="HeaderLength">Bytes up to and including the class name; the property stream starts here.</param>
public sealed record WgsGvasHeader(int SaveGameVersion, int PackageVersionUe4, int? PackageVersionUe5, string EngineVersion,
    string SaveGameClass, int HeaderLength);

/// <summary>Reads the header of an Unreal Engine GVAS save. Describe only: the property stream after it is not parsed.</summary>
public static class WgsGvas
{
    /// <summary>
    /// The GVAS header, or null when <paramref name="data"/> is not one this reader understands. Bounded and never throws.
    /// Layout (UE4 and UE5 <c>FSaveGameHeader</c>): magic <c>GVAS</c>, i32 save-game version, i32 UE4 package version, i32 UE5
    /// package version when the save-game version is 3 or more, engine version (u16 major, minor, patch, u32 changelist,
    /// FString branch), i32 custom-version format, i32 count and per entry a 16-byte GUID and an i32, then the class FString.
    /// </summary>
    public static WgsGvasHeader? TryReadHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4 || !data[..4].SequenceEqual("GVAS"u8)) return null;
        var pos = 4;
        if (!TryI32(data, ref pos, out var saveVersion) || !TryI32(data, ref pos, out var ue4)) return null;
        int? ue5 = null;
        if (saveVersion >= 3)
        {
            if (!TryI32(data, ref pos, out var v)) return null;
            ue5 = v;
        }
        if (pos + 10 > data.Length) return null;
        var major = BitConverter.ToUInt16(data[pos..]);
        var minor = BitConverter.ToUInt16(data[(pos + 2)..]);
        var patch = BitConverter.ToUInt16(data[(pos + 4)..]);
        var changelist = BitConverter.ToUInt32(data[(pos + 6)..]);
        pos += 10;
        if (TryFString(data, ref pos) is not { } branch) return null;
        if (!TryI32(data, ref pos, out _) || !TryI32(data, ref pos, out var count) || count is < 0 or > 4096) return null;
        if ((long)pos + count * 20L > data.Length) return null;
        pos += count * 20;
        if (TryFString(data, ref pos) is not { Length: > 0 } cls) return null;
        var engine = $"{major}.{minor}.{patch}-{changelist}" + (branch.Length > 0 ? $"+{branch}" : "");
        return new WgsGvasHeader(saveVersion, ue4, ue5, engine, cls, pos);
    }

    private static bool TryI32(ReadOnlySpan<byte> d, ref int pos, out int value)
    {
        value = 0;
        if (pos + 4 > d.Length) return false;
        value = BitConverter.ToInt32(d[pos..]);
        pos += 4;
        return true;
    }

    /// <summary>An Unreal FString: i32 length counting the terminator, negative for UTF-16, zero for empty.</summary>
    private static string? TryFString(ReadOnlySpan<byte> d, ref int pos)
    {
        if (!TryI32(d, ref pos, out var n)) return null;
        if (n == 0) return string.Empty;
        if (n is > 65_536 or < -65_536) return null;
        var unicode = n < 0;
        var bytes = unicode ? -n * 2 : n;
        if (pos + bytes > d.Length) return null;
        var s = unicode ? Encoding.Unicode.GetString(d.Slice(pos, bytes - 2)) : Encoding.Latin1.GetString(d.Slice(pos, bytes - 1));
        pos += bytes;
        return s;
    }
}
