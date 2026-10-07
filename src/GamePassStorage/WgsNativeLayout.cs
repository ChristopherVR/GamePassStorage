namespace GamePassStorage;

/// <summary>Where a native file belongs in a store. A null <see cref="Container"/> means the store's first container
/// (layouts for games that keep everything in one); a null <see cref="Blob"/> means "the container's only blob":
/// the name it already has, or <see cref="WgsNativeLayouts.DefaultBlobName"/> for a container being created.</summary>
public sealed record WgsNativeTarget(string? Container, string? Blob = null);

/// <summary>One file of a game's native save: a relative path ('/' separators) and its bytes.</summary>
public sealed record WgsNativeFile(string Path, byte[] Data);

/// <summary>What a layout may know about the store it maps: the live (not deleted) containers in index order and
/// the blob names each holds. Lets a layout undo a lossy name (a stripped prefix) by matching what exists.</summary>
public sealed class WgsNativeContext(IReadOnlyList<string> containerNames, Func<string, IReadOnlyList<string>> blobNamesOf)
{
    public static WgsNativeContext Empty { get; } = new([], _ => []);

    public IReadOnlyList<string> ContainerNames { get; } = containerNames;

    /// <summary>Blob names of an existing container, empty when it does not exist or cannot be listed.</summary>
    public IReadOnlyList<string> BlobNamesOf(string container) => blobNamesOf(container);
}

/// <summary>
/// Maps a store's containers and blobs to the plain files and folders the game itself uses outside Xbox
/// (the layout a Steam or Epic install keeps), and back. A layout that can wrap must be invertible so unwrapping
/// and re-wrapping a save is lossless. A layout never touches the store: <see cref="WgsStore.TryUnwrapTo"/>
/// and <see cref="WgsStore.TryWrap"/> do the reading and writing through the usual gate.
/// </summary>
public interface IWgsNativeLayout
{
    /// <summary>A short id, e.g. <c>container-folders</c>.</summary>
    string Name { get; }

    /// <summary>False for a layout that can only unwrap (for instance one that joins several blobs into one file
    /// in a way it cannot split again). <see cref="WgsStore.PlanWrap"/> reports such a layout as a problem.</summary>
    bool CanWrap => true;

    /// <summary>The relative path ('/' separators) a blob unwraps to, or null when it is not part of the native save.</summary>
    string? ToNativePath(WgsNativeContext context, string container, string blobName, int blobCount);

    /// <summary>Where a native file wraps to, or null when this layout does not recognise the path.</summary>
    WgsNativeTarget? FromNativePath(WgsNativeContext context, string relativePath);

    /// <summary>Whole-container hook for games whose native files do not map blob by blob (several blobs joined into
    /// one file, a file the game expects that Xbox does not store). Null (the default) means map blob by blob with
    /// <see cref="ToNativePath"/>; an empty list leaves the container out.</summary>
    IReadOnlyList<WgsNativeFile>? UnwrapContainer(WgsNativeContext context, string container,
        IReadOnlyDictionary<string, byte[]> blobs) => null;

    /// <summary>The bytes to write as the native file. Identity unless the game wraps its payload for Xbox.</summary>
    byte[] ToNativeContent(string container, string blobName, byte[] blob) => blob;

    /// <summary>The bytes to store as the blob. Must undo <see cref="ToNativeContent"/>.</summary>
    byte[] ToBlobContent(string? container, string? blobName, byte[] native) => native;
}

/// <summary>The declarative layouts that cover most games without code, and a builder for the rest.</summary>
public static class WgsNativeLayouts
{
    /// <summary>Blob name given to a single-blob container created by a wrap when no existing blob names it.</summary>
    public const string DefaultBlobName = "Data";

    /// <summary><c>&lt;container&gt;/&lt;blob&gt;&lt;suffix&gt;</c>: every container is a folder, every blob a file in it.
    /// Lossless for any store, so it is the fallback when a game has no layout of its own.</summary>
    public static IWgsNativeLayout ContainerFolders { get; } = new FolderLayout(string.Empty);

    /// <summary><see cref="ContainerFolders"/> with a suffix added to every blob's file name.</summary>
    public static IWgsNativeLayout ContainerFoldersWithSuffix(string suffix) => new FolderLayout(suffix);

    /// <summary><c>&lt;container&gt;&lt;suffix&gt;</c>: each single-blob container is one file (for example
    /// <c>Slot1</c> becomes <c>Slot1.sav</c>). Multi-blob containers are left out and reported.</summary>
    public static IWgsNativeLayout OneFilePerContainer(string suffix = "") => new OneFileLayout(suffix);

    /// <summary><c>&lt;blob&gt;&lt;suffix&gt;</c>: the blobs of one container as files side by side; every other container
    /// is left out. A null <paramref name="container"/> means the store's first container.</summary>
    public static IWgsNativeLayout BlobsOfContainer(string? container, string suffix = "") => new FlatLayout(container, suffix);

    /// <summary>A layout from two functions, for a mapping the declarative layouts do not cover. A null
    /// <paramref name="fromNative"/> makes it unwrap-only (for a mapping that loses what it would need to go back).</summary>
    public static IWgsNativeLayout Map(string name,
        Func<WgsNativeContext, string, string, int, string?> toNative,
        Func<WgsNativeContext, string, WgsNativeTarget?>? fromNative) => new MapLayout(name, toNative, fromNative);

    /// <summary>Resolves a layout by id for the CLI: <c>container-folders[:suffix]</c>, <c>one-file[:suffix]</c>,
    /// <c>blobs</c> (first container) or <c>blobs:&lt;container&gt;</c>.</summary>
    public static IWgsNativeLayout? Parse(string spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return null;
        var colon = spec.IndexOf(':', StringComparison.Ordinal);
        var head = colon < 0 ? spec : spec[..colon];
        var arg = colon < 0 ? null : spec[(colon + 1)..];
        return head.ToLowerInvariant() switch
        {
            "container-folders" => arg is null ? ContainerFolders : ContainerFoldersWithSuffix(arg),
            "one-file" => OneFilePerContainer(arg ?? string.Empty),
            "blobs" => arg is { Length: 0 } ? null : BlobsOfContainer(arg),
            _ => null,
        };
    }

    /// <summary>Splits <c>a/b/c</c> into (<c>a/b</c>, <c>c</c>); null when there is no folder part.</summary>
    public static (string Folder, string File)? SplitLast(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash <= 0 || slash == path.Length - 1 ? null : (path[..slash], path[(slash + 1)..]);
    }

    /// <summary>Strips <paramref name="suffix"/> from <paramref name="name"/>; null when it is absent or would leave nothing.</summary>
    public static string? WithoutSuffix(string name, string suffix)
        => suffix.Length == 0 ? name
            : name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal) ? name[..^suffix.Length] : null;

    private sealed class FolderLayout(string suffix) : IWgsNativeLayout
    {
        public string Name => suffix.Length == 0 ? "container-folders" : $"container-folders:{suffix}";

        public string? ToNativePath(WgsNativeContext context, string container, string blobName, int blobCount)
            => $"{container}/{blobName}{suffix}";

        public WgsNativeTarget? FromNativePath(WgsNativeContext context, string relativePath)
            => SplitLast(relativePath) is { } p && WithoutSuffix(p.File, suffix) is { } blob ? new WgsNativeTarget(p.Folder, blob) : null;
    }

    private sealed class OneFileLayout(string suffix) : IWgsNativeLayout
    {
        public string Name => suffix.Length == 0 ? "one-file" : $"one-file:{suffix}";

        public string? ToNativePath(WgsNativeContext context, string container, string blobName, int blobCount)
            => blobCount == 1 ? container + suffix : null;

        public WgsNativeTarget? FromNativePath(WgsNativeContext context, string relativePath)
            => WithoutSuffix(relativePath, suffix) is { } container ? new WgsNativeTarget(container) : null;
    }

    private sealed class FlatLayout(string? container, string suffix) : IWgsNativeLayout
    {
        public string Name => (container is null ? "blobs" : $"blobs:{container}") + (suffix.Length == 0 ? "" : $" (suffix {suffix})");

        private string? Target(WgsNativeContext context) => container ?? (context.ContainerNames.Count > 0 ? context.ContainerNames[0] : null);

        public string? ToNativePath(WgsNativeContext context, string c, string blobName, int blobCount)
            => string.Equals(c, Target(context), StringComparison.Ordinal) ? blobName + suffix : null;

        public WgsNativeTarget? FromNativePath(WgsNativeContext context, string relativePath)
            => relativePath.Contains('/', StringComparison.Ordinal) || WithoutSuffix(relativePath, suffix) is not { } blob
                ? null
                : new WgsNativeTarget(container, blob);
    }

    private sealed class MapLayout(string name,
        Func<WgsNativeContext, string, string, int, string?> toNative,
        Func<WgsNativeContext, string, WgsNativeTarget?>? fromNative) : IWgsNativeLayout
    {
        public string Name => name;
        public bool CanWrap => fromNative is not null;

        public string? ToNativePath(WgsNativeContext context, string container, string blobName, int blobCount)
            => toNative(context, container, blobName, blobCount);

        public WgsNativeTarget? FromNativePath(WgsNativeContext context, string relativePath) => fromNative?.Invoke(context, relativePath);
    }
}

/// <summary>Outcome of <see cref="WgsStore.TryUnwrapTo"/>.</summary>
/// <param name="Files">Relative paths written, in the layout's own naming.</param>
/// <param name="Skipped">Containers or blobs left out, each with the reason.</param>
public sealed record WgsUnwrapResult(WgsOperationStatus Status, IReadOnlyList<string> Files,
    IReadOnlyList<string> Skipped, string? Message)
{
    public bool Succeeded => Status == WgsOperationStatus.Ok;
}

/// <summary>A preview of a wrap. <c>Unmapped</c> lists files the layout did not recognise; they are left alone and do not block.</summary>
public sealed record WgsWrapPlan(WgsImportPlan Import, IReadOnlyList<string> Unmapped)
{
    public bool CanApply => Import.CanApply;
}
