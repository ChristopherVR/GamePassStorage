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
    public static IWgsNativeLayout Starfield { get; } = new StarfieldLayout();

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

    private sealed class StarfieldLayout : IWgsNativeLayout
    {
        private static readonly byte[] Padding = Encoding.ASCII.GetBytes("padding\0padding\0");
        public string Name => "starfield";
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
                var index = newFormat ? ParseIndex(name, "BlobData", 0)
                    : name == "BETHESDAPFH" ? 0
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
                : throw new FormatException($"'{name}' is not a known Starfield part name");
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
