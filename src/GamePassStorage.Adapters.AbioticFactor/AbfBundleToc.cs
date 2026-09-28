using System.Text;

namespace GamePassStorage.Adapters.AbioticFactor;

/// <summary>One table-of-contents entry of an <c>ABF_SAVE_VERSION</c> world bundle.</summary>
/// <param name="Path">The member's in-game path, e.g. <c>Profile/Worlds/MyWorld/WorldSave_Facility</c>.
/// The game records it as written on the machine that made the save, so treat it as untrusted text.</param>
/// <param name="Size">Uncompressed size of the member body.</param>
/// <param name="SaveClass">The Unreal save class, e.g. <c>/Game/Blueprints/Saves/X.X_C</c>.</param>
/// <param name="Flag">1 for the text <c>SandboxSettings.ini</c> member, 0 for a GVAS save.</param>
public sealed record AbfTocMember(string Path, int Size, string SaveClass, int Flag)
{
    public const int IniFlag = 1;

    public bool IsIni => Flag == IniFlag;

    /// <summary>The class name without its package path or trailing <c>_C</c>.</summary>
    public string ShortClass
    {
        get
        {
            var name = SaveClass[(SaveClass.LastIndexOf('.') + 1)..];
            return name.EndsWith("_C", StringComparison.Ordinal) ? name[..^2] : name;
        }
    }
}

/// <summary>
/// The uncompressed front of an Abiotic Factor world bundle: marker, header ints, member count and
/// the table of contents. The member bodies follow as one Oodle-compressed stream; nothing here
/// touches it, so this works with no Oodle library present.
///
/// <para>Layout (from the Abiotic Editor reference notes): an Unreal FString <c>ABF_SAVE_VERSION</c>;
/// <c>int</c> version; two opaque <c>int</c> header fields; <c>int</c> member count; then per member an FString path,
/// an <c>int</c> uncompressed size, an FString save class and an <c>int</c> flag; then an <c>int</c> payload method (1 =
/// Oodle) and an <c>int</c> compressed size. An FString is an <c>int</c> length including the NUL: positive counts
/// ASCII bytes, negative counts UTF-16 characters.</para>
/// </summary>
public sealed record AbfBundleToc(int Version, uint Field1, uint Field2, IReadOnlyList<AbfTocMember> Members,
    int PayloadMethod, int CompressedSize)
{
    public const string Marker = "ABF_SAVE_VERSION";

    public long UncompressedTotal => Members.Sum(m => (long)m.Size);

    /// <summary>The world named in the member paths (<c>.../Worlds/&lt;World&gt;/...</c>), or null.</summary>
    public string? WorldName => WorldNameFromPath(Members.Count > 0 ? Members[0].Path : null);

    public static bool LooksLikeBundle(ReadOnlySpan<byte> data)
    {
        var pos = 0;
        return ReadString(data, ref pos) == Marker;
    }

    /// <summary>Reads the table of contents. Returns null (with <paramref name="error"/>) when the bytes are
    /// not a bundle or the table is truncated or implausible. Never throws.</summary>
    public static AbfBundleToc? TryRead(ReadOnlySpan<byte> data, out string? error)
    {
        error = null;
        var pos = 0;
        if (ReadString(data, ref pos) != Marker)
        {
            error = "does not start with the ABF_SAVE_VERSION marker";
            return null;
        }
        if (!TryInt(data, ref pos, out var version) || !TryInt(data, ref pos, out var f1)
            || !TryInt(data, ref pos, out var f2) || !TryInt(data, ref pos, out var count))
        {
            error = "the header is truncated";
            return null;
        }
        if (count is < 0 or > 100_000)
        {
            error = $"implausible member count {count}";
            return null;
        }
        var members = new List<AbfTocMember>(count);
        for (var i = 0; i < count; i++)
        {
            var path = ReadString(data, ref pos);
            if (path is null || !TryInt(data, ref pos, out var size)) { error = $"member {i} is truncated"; return null; }
            var cls = ReadString(data, ref pos);
            if (cls is null || !TryInt(data, ref pos, out var flag)) { error = $"member {i} is truncated"; return null; }
            if (size < 0) { error = $"member {i} has a negative size"; return null; }
            members.Add(new AbfTocMember(path, size, cls, flag));
        }
        if (!TryInt(data, ref pos, out var method) || !TryInt(data, ref pos, out var compressed))
        {
            error = "the payload header is truncated";
            return null;
        }
        return new AbfBundleToc(version, (uint)f1, (uint)f2, members, method, compressed);
    }

    /// <summary>Extracts <c>&lt;World&gt;</c> from a path containing <c>Worlds/&lt;World&gt;/</c>.</summary>
    public static string? WorldNameFromPath(string? path)
    {
        if (path is null) return null;
        var parts = path.Replace('\\', '/').Split('/');
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (parts[i].Equals("Worlds", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(parts[i + 1]))
            {
                return parts[i + 1];
            }
        }
        return null;
    }

    private static bool TryInt(ReadOnlySpan<byte> d, ref int pos, out int value)
    {
        if (pos + 4 > d.Length) { value = 0; return false; }
        value = BitConverter.ToInt32(d[pos..]);
        pos += 4;
        return true;
    }

    /// <summary>One FString, or null when the bytes do not describe one.</summary>
    internal static string? ReadString(ReadOnlySpan<byte> d, ref int pos)
    {
        if (!TryInt(d, ref pos, out var length)) return null;
        if (length == 0) return string.Empty;
        if (length > 0)
        {
            if (length > 4096 || pos + length > d.Length) return null;
            var s = Encoding.ASCII.GetString(d.Slice(pos, length)).TrimEnd('\0');
            pos += length;
            return s;
        }
        if (length == int.MinValue) return null;
        var bytes = -length * 2;
        if (bytes > 8192 || pos + bytes > d.Length) return null;
        var wide = Encoding.Unicode.GetString(d.Slice(pos, bytes)).TrimEnd('\0');
        pos += bytes;
        return wide;
    }
}
