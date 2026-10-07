using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace GamePassStorage.Adapters.Catalog;

/// <summary>
/// Native layouts for titles the declarative layouts do not cover. Each mirrors the matching handler of
/// Z1ni/XGP-save-extractor (<c>main.py</c>, <c>get_save_paths</c>, MIT licensed) and adds the inverse where the
/// mapping keeps enough to go back. A layout that loses information is unwrap-only and says so.
/// </summary>
public static class CatalogLayouts
{
    private const string Sav = ".sav";

    /// <summary>Palworld: a single-blob container per file, each <c>-</c> in the name a folder separator, plus
    /// <c>.sav</c> (<c>Players-&lt;id&gt;</c> becomes <c>Players/&lt;id&gt;.sav</c>). A native path holding a <c>-</c>
    /// cannot be told apart from a separator, so it is not wrapped.</summary>
    public static IWgsNativeLayout Palworld { get; } = WgsNativeLayouts.Map("palworld",
        (_, c, _, count) => count == 1 ? c.Replace('-', '/') + Sav : null,
        (_, path) => WgsNativeLayouts.WithoutSuffix(path, Sav) is { } stem && !stem.Contains('-', StringComparison.Ordinal)
            ? new WgsNativeTarget(stem.Replace('/', '-'))
            : null);

    /// <summary>Forza Horizon 5: <c>&lt;container&gt;.&lt;blob&gt;</c>. Names may hold dots, so the split is ambiguous;
    /// wrapping prefers an existing container that already holds that exact blob, then the longest existing
    /// container name, and only then the first dot.</summary>
    public static IWgsNativeLayout Forza { get; } = WgsNativeLayouts.Map("forza",
        (_, c, b, _) => $"{c}.{b}",
        (ctx, path) =>
        {
            if (path.Contains('/', StringComparison.Ordinal)) return null;
            var candidates = ctx.ContainerNames.Where(c => path.Length > c.Length + 1 && path.StartsWith(c + ".", StringComparison.Ordinal)).ToList();
            var exact = candidates.Where(c => ctx.BlobNamesOf(c).Contains(path[(c.Length + 1)..], StringComparer.Ordinal)).ToList();
            if (exact.Count == 1) return new WgsNativeTarget(exact[0], path[(exact[0].Length + 1)..]);
            var known = candidates.OrderByDescending(c => c.Length).FirstOrDefault();
            if (known is not null) return new WgsNativeTarget(known, path[(known.Length + 1)..]);
            var dot = path.IndexOf('.', StringComparison.Ordinal);
            return dot > 0 && dot < path.Length - 1 ? new WgsNativeTarget(path[..dot], path[(dot + 1)..]) : null;
        });

    /// <summary>Lies of P: the container name without its leading digits, plus <c>.sav</c>. The digits are lost, so a file
    /// wraps only onto an existing container whose name strips to the same thing.</summary>
    public static IWgsNativeLayout LiesOfP { get; } = WgsNativeLayouts.Map("lies-of-p",
        (_, c, _, count) => count == 1 && c.TrimStart("0123456789".ToCharArray()) is { Length: > 0 } s ? s + Sav : null,
        (ctx, path) =>
        {
            if (WgsNativeLayouts.WithoutSuffix(path, Sav) is not { } stem) return null;
            var matches = ctx.ContainerNames.Where(c => c.TrimStart("0123456789".ToCharArray()) == stem).ToList();
            return matches.Count == 1 ? new WgsNativeTarget(matches[0]) : null;
        });

    /// <summary>Coral Island and Clair Obscur: Expedition 33: <c>&lt;container&gt;.sav</c>, except a <c>Backup</c> prefix
    /// becomes a <c>Backup/</c> folder.</summary>
    public static IWgsNativeLayout BackupFolder { get; } = WgsNativeLayouts.Map("backup-folder",
        (_, c, _, count) => count != 1 ? null
            : c.StartsWith("Backup", StringComparison.Ordinal) && c.Length > "Backup".Length ? $"Backup/{c["Backup".Length..]}{Sav}"
            : c + Sav,
        (_, path) => WgsNativeLayouts.WithoutSuffix(path, Sav) switch
        {
            null => null,
            var s when s.StartsWith("Backup/", StringComparison.Ordinal) && !s["Backup/".Length..].Contains('/', StringComparison.Ordinal)
                => new WgsNativeTarget("Backup" + s["Backup/".Length..]),
            var s when !s.Contains('/', StringComparison.Ordinal) => new WgsNativeTarget(s),
            _ => null,
        });

    /// <summary>Arcade Paradise: the first container's only blob is <c>RATSaveData.dat</c>.</summary>
    public static IWgsNativeLayout ArcadeParadise { get; } = WgsNativeLayouts.Map("arcade-paradise",
        (ctx, c, _, count) => count == 1 && ctx.ContainerNames.Count > 0 && ctx.ContainerNames[0] == c ? "RATSaveData.dat" : null,
        (_, path) => path == "RATSaveData.dat" ? new WgsNativeTarget(null) : null);

    /// <summary>Railway Empire 2: each container's <c>savegame</c> blob is a file named after the container. The
    /// <c>description</c> blob is not part of the native save; an existing container keeps its own on wrap.</summary>
    public static IWgsNativeLayout RailwayEmpire2 { get; } = WgsNativeLayouts.Map("railway-empire-2",
        (_, c, b, _) => b == "savegame" ? c : null,
        (_, path) => new WgsNativeTarget(path, "savegame"));

    /// <summary>State of Decay 2: the first container's blobs, each named by the last segment of its blob name plus
    /// <c>.sav</c>. The rest of the blob name is lost, so this layout only unwraps.</summary>
    public static IWgsNativeLayout StateOfDecay2 { get; } = WgsNativeLayouts.Map("state-of-decay-2",
        (ctx, c, b, _) => ctx.ContainerNames.Count > 0 && ctx.ContainerNames[0] == c ? b.Split('/')[^1] + Sav : null,
        null);

    /// <summary>Cricket 24: a folder per container; each <c>&lt;name&gt;.CHUNK0</c> blob is <c>&lt;name&gt;.SAV</c>.
    /// Any other chunk is left out and reported, as the source tool has never seen one.</summary>
    public static IWgsNativeLayout Cricket24 { get; } = WgsNativeLayouts.Map("cricket-24",
        (_, c, b, _) => b.EndsWith(".CHUNK0", StringComparison.Ordinal) && !b[..^".CHUNK0".Length].Contains("CHUNK", StringComparison.Ordinal)
            ? $"{c}/{b[..^".CHUNK0".Length]}.SAV"
            : null,
        (_, path) => WgsNativeLayouts.SplitLast(path) is { } p && WgsNativeLayouts.WithoutSuffix(p.File, ".SAV") is { } stem
            ? new WgsNativeTarget(p.Folder, stem + ".CHUNK0")
            : null);

    /// <summary>Control: a folder per container, each blob <c>&lt;blob&gt;.chunk</c>, plus the
    /// <c>--containerDisplayName.chunk</c> file the Epic version keeps, holding the container name.</summary>
    public static IWgsNativeLayout Control { get; } = new ControlLayout();

    /// <summary>Starfield: each <c>Saves/&lt;name&gt;</c> container is one SFS file, its parts joined in order and each
    /// padded to 16 bytes with <c>padding\0</c>. Two part schemes exist (<c>BETHESDAPFH</c> then <c>P0P</c>, <c>P1P</c>...;
    /// or a <c>toc</c> and <c>BlobData0</c>...). Splitting an SFS file again needs the part sizes, so this only unwraps.</summary>
    public static IWgsNativeLayout Starfield { get; } = new JoinedPartsLayout("starfield", "BETHESDAPFH", "BlobData");

    /// <summary>Fallout 4: the Starfield scheme with its own part names (<c>FO4_SAVEGAME</c> then <c>P0P</c>...; or a
    /// <c>toc</c> and <c>ChunkData0</c>...), per XgpSaveTools' <c>Fallout4Handler</c>. Unwrap only.</summary>
    public static IWgsNativeLayout Fallout4 { get; } = new JoinedPartsLayout("fallout-4", "FO4_SAVEGAME", "ChunkData");

    /// <summary>Galacticare: each container's <c>PlayerData</c> blob is a file named after the container.</summary>
    public static IWgsNativeLayout Galacticare { get; } = WgsNativeLayouts.Map("galacticare",
        (_, c, b, _) => b.Equals("PlayerData", StringComparison.OrdinalIgnoreCase) ? c : null,
        (ctx, path) => path.Contains('/', StringComparison.Ordinal) ? null
            : new WgsNativeTarget(path, ctx.BlobNamesOf(path).FirstOrDefault(b => b.Equals("PlayerData", StringComparison.OrdinalIgnoreCase)) ?? "PlayerData"));

    /// <summary>Balatro: the <c>common</c> container's blobs at the top level, every other container as a folder.</summary>
    public static IWgsNativeLayout Balatro { get; } = WgsNativeLayouts.Map("balatro",
        (_, c, b, _) => c.Equals("common", StringComparison.OrdinalIgnoreCase) ? b : $"{c}/{b}",
        (ctx, path) => WgsNativeLayouts.SplitLast(path) is { } p ? new WgsNativeTarget(p.Folder, p.File)
            : new WgsNativeTarget(ctx.ContainerNames.FirstOrDefault(c => c.Equals("common", StringComparison.OrdinalIgnoreCase)) ?? "common", path));

    /// <summary>Hollow Knight: Silksong: blobs of a container named like <c>shared</c> or <c>save</c> at the top level; a
    /// <c>restore</c> container's blobs under <c>Restore_Points&lt;n&gt;/</c>; any other container as a folder. Top-level
    /// files and restore points wrap only onto an existing container that holds them.</summary>
    public static IWgsNativeLayout Silksong { get; } = WgsNativeLayouts.Map("silksong",
        (_, c, b, _) => SilksongKind(c) switch
        {
            "top" => b,
            "restore" when FirstNumber(c) is { } n => $"Restore_Points{n}/{b}",
            "restore" => null,
            _ => $"{c}/{b}",
        },
        (ctx, path) =>
        {
            if (WgsNativeLayouts.SplitLast(path) is not { } p)
            {
                var owners = ctx.ContainerNames.Where(c => SilksongKind(c) == "top" && ctx.BlobNamesOf(c).Contains(path, StringComparer.Ordinal)).ToList();
                return owners.Count == 1 ? new WgsNativeTarget(owners[0], path) : null;
            }
            if (p.Folder.StartsWith("Restore_Points", StringComparison.Ordinal) && !p.Folder.Contains('/', StringComparison.Ordinal))
            {
                var n = p.Folder["Restore_Points".Length..];
                var owners = ctx.ContainerNames.Where(c => SilksongKind(c) == "restore" && FirstNumber(c) == n).ToList();
                return owners.Count == 1 ? new WgsNativeTarget(owners[0], p.File) : null;
            }
            return new WgsNativeTarget(p.Folder, p.File);
        });

    private static string SilksongKind(string container)
        => container.Contains("shared", StringComparison.OrdinalIgnoreCase) || container.Contains("save", StringComparison.OrdinalIgnoreCase) ? "top"
            : container.Contains("restore", StringComparison.OrdinalIgnoreCase) ? "restore"
            : "folder";

    private static string? FirstNumber(string s)
    {
        var start = s.AsSpan().IndexOfAnyInRange('0', '9');
        if (start < 0) return null;
        var end = start;
        while (end < s.Length && char.IsAsciiDigit(s[end])) end++;
        return s[start..end];
    }

    /// <summary>Metaphor: ReFantazio: a <c>System*</c> container is <c>system.sav</c>, <c>SaveData&lt;rest&gt;</c> is
    /// <c>save&lt;rest&gt;.sav</c>. Other containers are left out. <c>system.sav</c> wraps onto the one existing System container.</summary>
    public static IWgsNativeLayout MetaphorReFantazio { get; } = WgsNativeLayouts.Map("metaphor-refantazio",
        (_, c, _, count) => count != 1 ? null
            : c.StartsWith("System", StringComparison.OrdinalIgnoreCase) ? "system.sav"
            : c.StartsWith("SaveData", StringComparison.OrdinalIgnoreCase) ? $"save{c["SaveData".Length..]}.sav"
            : null,
        (ctx, path) =>
        {
            if (path == "system.sav")
            {
                var systems = ctx.ContainerNames.Where(c => c.StartsWith("System", StringComparison.OrdinalIgnoreCase)).ToList();
                return systems.Count == 1 ? new WgsNativeTarget(systems[0]) : null;
            }
            return path.StartsWith("save", StringComparison.Ordinal) && WgsNativeLayouts.WithoutSuffix(path, Sav) is { Length: > 4 } stem
                && !stem.Contains('/', StringComparison.Ordinal)
                ? new WgsNativeTarget("SaveData" + stem["save".Length..])
                : null;
        });

    /// <summary>Ninja Gaiden 2 Black: a container <c>&lt;cat&gt;&lt;cat&gt;&lt;digits&gt;DAT</c> (the category written twice) is
    /// <c>&lt;cat&gt;/&lt;cat&gt;.sav</c>. The digits are lost, so a file wraps only onto the one existing container of that category.</summary>
    public static IWgsNativeLayout NinjaGaiden2Black { get; } = WgsNativeLayouts.Map("ninja-gaiden-2-black",
        (_, c, _, count) => count == 1 && NgCategory(c) is { } cat ? $"{cat}/{cat}{Sav}" : null,
        (ctx, path) =>
        {
            if (WgsNativeLayouts.SplitLast(path) is not { } p || p.File != p.Folder + Sav) return null;
            var owners = ctx.ContainerNames.Where(c => string.Equals(NgCategory(c), p.Folder, StringComparison.OrdinalIgnoreCase)).ToList();
            return owners.Count == 1 ? new WgsNativeTarget(owners[0]) : null;
        });

    private static string? NgCategory(string container)
    {
        if (!container.EndsWith("DAT", StringComparison.OrdinalIgnoreCase)) return null;
        var before = container[..^3];
        var digits = 0;
        while (digits < before.Length && char.IsAsciiDigit(before[^(digits + 1)])) digits++;
        for (var take = 1; take <= digits; take++)
        {
            var core = before[..^take];
            if (core.Length == 0 || core.Length % 2 != 0) continue;
            var half = core.Length / 2;
            if (core[..half].Equals(core[half..], StringComparison.OrdinalIgnoreCase)) return core[..half];
        }
        return null;
    }

    /// <summary>Scorn: one file per container, with a dot put back before a trailing <c>dat</c>, <c>sav</c> or <c>info</c>
    /// when the container name lost it (<c>save1dat</c> becomes <c>save1.dat</c>). Wraps onto the existing container that maps to the file.</summary>
    public static IWgsNativeLayout Scorn { get; } = WgsNativeLayouts.Map("scorn",
        (_, c, _, count) => count == 1 ? ScornName(c) : null,
        (ctx, path) => ctx.ContainerNames.Where(c => ScornName(c) == path).ToList() is [var only] ? new WgsNativeTarget(only) : null);

    private static string ScornName(string container)
    {
        foreach (var ext in new[] { "dat", "sav", "info" })
        {
            if (container.Length > ext.Length && container.EndsWith(ext, StringComparison.Ordinal) && container[^(ext.Length + 1)] != '.')
            {
                return container[..^ext.Length] + "." + ext;
            }
        }
        return container;
    }

    /// <summary>Every blob of every container, flat, by blob name (XgpSaveTools' <c>generic</c> handler). A file wraps onto the
    /// one existing container that holds a blob of that name.</summary>
    public static IWgsNativeLayout AllBlobsFlat { get; } = WgsNativeLayouts.Map("all-blobs-flat",
        (_, _, b, _) => b,
        (ctx, path) => ctx.ContainerNames.Where(c => ctx.BlobNamesOf(c).Contains(path, StringComparer.Ordinal)).ToList() is [var only]
            ? new WgsNativeTarget(only, path)
            : null);

    /// <summary>The Like a Dragon titles: the container path with a <c>datasav</c> / <c>datasys</c> leaf renamed <c>data.sav</c> /
    /// <c>data.sys</c> holds the <c>data</c> blob; the <c>icon</c> blob becomes <c>&lt;folder&gt;_icon.&lt;format&gt;</c> beside it.
    /// Wrapping replaces existing containers only, matching the names they already have.</summary>
    public static IWgsNativeLayout LikeADragon(string iconFormat) => new LikeADragonLayout(iconFormat);

    /// <summary>Persona 3 Reload: one <c>.sav</c> per container, each byte's bit pairs 0-1 and 4-5 swapped and XORed with a
    /// fixed key (XgpSaveTools' <c>Persona3ReloadHandler</c>). The transform is its own inverse apart from the XOR order,
    /// so wrapping undoes it exactly; the source tool itself only exports.</summary>
    public static IWgsNativeLayout Persona3Reload { get; } = new Persona3ReloadLayout();

    private sealed class LikeADragonLayout(string iconFormat) : IWgsNativeLayout
    {
        public string Name => $"like-a-dragon:{iconFormat}";

        private static string DataPath(string container)
        {
            var (folder, leaf) = WgsNativeLayouts.SplitLast(container) is { } p ? (p.Folder + "/", p.File) : ("", container);
            return leaf switch { "datasav" => folder + "data.sav", "datasys" => folder + "data.sys", _ => container };
        }

        public string? ToNativePath(WgsNativeContext context, string container, string blobName, int blobCount)
        {
            if (blobName.Equals("data", StringComparison.OrdinalIgnoreCase)) return DataPath(container);
            if (!blobName.Equals("icon", StringComparison.OrdinalIgnoreCase) || WgsNativeLayouts.SplitLast(container) is not { } p) return null;
            var parent = p.Folder[(p.Folder.LastIndexOf('/') + 1)..];
            return $"{p.Folder}/{parent}_icon.{iconFormat}";
        }

        public WgsNativeTarget? FromNativePath(WgsNativeContext context, string relativePath)
        {
            foreach (var c in context.ContainerNames)
            {
                foreach (var b in context.BlobNamesOf(c))
                {
                    if (ToNativePath(context, c, b, 0) == relativePath) return new WgsNativeTarget(c, b);
                }
            }
            return null;
        }
    }

    private sealed class Persona3ReloadLayout : IWgsNativeLayout
    {
        private static readonly byte[] Key = Encoding.ASCII.GetBytes("ae5zeitaix1joowooNgie3fahP5Ohph");
        public string Name => "persona-3-reload";

        public string? ToNativePath(WgsNativeContext context, string container, string blobName, int blobCount)
            => blobCount == 1 ? container + Sav : null;

        public WgsNativeTarget? FromNativePath(WgsNativeContext context, string relativePath)
            => WgsNativeLayouts.WithoutSuffix(relativePath, Sav) is { } c && !c.Contains('/', StringComparison.Ordinal) ? new WgsNativeTarget(c) : null;

        private static byte Swap(byte v) => (byte)(((v >> 4) & 0x03) | ((v & 0x03) << 4) | (v & 0xCC));

        public byte[] ToNativeContent(string container, string blobName, byte[] blob)
        {
            var output = new byte[blob.Length];
            for (var i = 0; i < blob.Length; i++) output[i] = (byte)(Swap(blob[i]) ^ Key[i % Key.Length]);
            return output;
        }

        public byte[] ToBlobContent(string? container, string? blobName, byte[] native)
        {
            var output = new byte[native.Length];
            for (var i = 0; i < native.Length; i++) output[i] = Swap((byte)(native[i] ^ Key[i % Key.Length]));
            return output;
        }
    }

    /// <summary>One Lonely Outpost: the first container holds one gzipped JSON document carrying every native file
    /// as text. This unwraps those files; rebuilding the document is not supported.</summary>
    public static IWgsNativeLayout OneLonelyOutpost { get; } = new OneLonelyOutpostLayout();

    private sealed class ControlLayout : IWgsNativeLayout
    {
        private const string DisplayName = "--containerDisplayName.chunk";
        public string Name => "control";

        public string? ToNativePath(WgsNativeContext context, string container, string blobName, int blobCount)
            => $"{container}/{blobName}.chunk";

        public IReadOnlyList<WgsNativeFile>? UnwrapContainer(WgsNativeContext context, string container,
            IReadOnlyDictionary<string, byte[]> blobs)
            => [new WgsNativeFile($"{container}/{DisplayName}", Encoding.UTF8.GetBytes(container)),
                .. blobs.Select(b => new WgsNativeFile($"{container}/{b.Key}.chunk", b.Value))];

        public WgsNativeTarget? FromNativePath(WgsNativeContext context, string relativePath)
            => WgsNativeLayouts.SplitLast(relativePath) is { } p && p.File != DisplayName
               && WgsNativeLayouts.WithoutSuffix(p.File, ".chunk") is { } blob
                ? new WgsNativeTarget(p.Folder, blob)
                : null;
    }

    private sealed class JoinedPartsLayout(string name, string headerPart, string newPartPrefix) : IWgsNativeLayout
    {
        private static readonly byte[] Padding = Encoding.ASCII.GetBytes("padding\0padding\0");
        public string Name => name;
        public bool CanWrap => false;

        public string? ToNativePath(WgsNativeContext context, string container, string blobName, int blobCount) => null;
        public WgsNativeTarget? FromNativePath(WgsNativeContext context, string relativePath) => null;

        public IReadOnlyList<WgsNativeFile>? UnwrapContainer(WgsNativeContext context, string container,
            IReadOnlyDictionary<string, byte[]> blobs)
        {
            // Other containers (Settings/...) are not saves.
            if (WgsNativeLayouts.SplitLast(container) is not { } p || p.Folder.Split('/')[^1] != "Saves") return [];
            var newFormat = blobs.ContainsKey("toc");
            var parts = new SortedDictionary<int, byte[]>();
            foreach (var (name, data) in blobs)
            {
                if (name == "toc") continue;
                var index = newFormat ? ParseIndex(name, newPartPrefix, 0)
                    : name == headerPart ? 0
                    : ParseIndex(name.Trim('P'), string.Empty, 1);
                if (!parts.TryAdd(index, data)) throw new InvalidDataException($"two parts claim position {index}");
            }
            using var sfs = new MemoryStream();
            foreach (var part in parts.Values)
            {
                sfs.Write(part);
                var pad = 16 - part.Length % 16;
                if (pad != 16) sfs.Write(Padding, 0, pad);
            }
            return [new WgsNativeFile(p.File, sfs.ToArray())];
        }

        private static int ParseIndex(string name, string prefix, int offset)
            => name.StartsWith(prefix, StringComparison.Ordinal)
               && int.TryParse(name.AsSpan(prefix.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n)
                ? n + offset
                : throw new FormatException($"'{name}' is not a known part name for this save");
    }

    private sealed class OneLonelyOutpostLayout : IWgsNativeLayout
    {
        public string Name => "one-lonely-outpost";
        public bool CanWrap => false;

        public string? ToNativePath(WgsNativeContext context, string container, string blobName, int blobCount) => null;
        public WgsNativeTarget? FromNativePath(WgsNativeContext context, string relativePath) => null;

        public IReadOnlyList<WgsNativeFile>? UnwrapContainer(WgsNativeContext context, string container,
            IReadOnlyDictionary<string, byte[]> blobs)
        {
            if (context.ContainerNames.Count == 0 || context.ContainerNames[0] != container || blobs.Count == 0) return [];
            using var gz = new GZipStream(new MemoryStream(blobs.First().Value), CompressionMode.Decompress);
            using var json = JsonDocument.Parse(gz);
            var files = new List<WgsNativeFile>();
            foreach (var file in json.RootElement.GetProperty("files").GetProperty("$values").EnumerateArray())
            {
                var name = file.GetProperty("name").GetString() ?? throw new InvalidDataException("a file has no name");
                if (name.StartsWith("ConsoleSaves/", StringComparison.Ordinal)) name = name["ConsoleSaves/".Length..];
                var text = file.GetProperty("datas").GetProperty("$values")[0].GetString() ?? string.Empty;
                files.Add(new WgsNativeFile(name, Encoding.UTF8.GetBytes(text)));
            }
            return files;
        }
    }
}
