namespace GamePassStorage;

// Unwrapping a store into the game's native files, and wrapping native files back into containers.
//
// Unwrap reads only. Wrap goes through the same plan/apply path as import: every file is read and
// checked before the first write, each container is added (never uploaded, so no ETag) or has just the
// named blobs replaced, and the gate and concurrent-change check apply. Native files carry no state or
// ETag, so a wrap can never invent either.
public sealed partial class WgsStore
{
    /// <summary>What a layout may know about this store: live containers in index order and their blob names.</summary>
    public WgsNativeContext NativeContext()
        => new(_containers.Where(c => c.RawState != (uint)WgsEntryState.Deleted).Select(c => c.Name).ToList(),
            name => Find(name) is { } c && TryListBlobs(c) is { Succeeded: true } list
                ? list.Blobs!.Select(b => b.Name).ToList()
                : []);

    /// <summary>
    /// Writes the store's blobs to <paramref name="folder"/> (which must not exist or be empty) as the files
    /// <paramref name="layout"/> names. Deletion tombstones and anything the layout declines are skipped and reported.
    /// </summary>
    public WgsUnwrapResult TryUnwrapTo(string folder, IWgsNativeLayout layout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(layout);
        if (_fs.DirectoryExists(folder) && (_fs.EnumerateFiles(folder).Any() || _fs.EnumerateDirectories(folder).Any()))
        {
            return new WgsUnwrapResult(WgsOperationStatus.Refused, [], [], $"'{folder}' is not empty.");
        }
        var context = NativeContext();
        var skipped = new List<string>();
        var planned = new List<WgsNativeFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in _containers)
        {
            if (c.RawState == (uint)WgsEntryState.Deleted) { skipped.Add($"{c.Name}: deleted (pending deletion)"); continue; }
            var read = TryReadBlobs(c);
            if (!read.Succeeded)
            {
                return new WgsUnwrapResult(read.Status, [], skipped, $"'{c.Name}' could not be read: {read.Message}");
            }
            IReadOnlyList<WgsNativeFile> files;
            try { files = layout.UnwrapContainer(context, c.Name, read.Blobs!) ?? PerBlob(layout, context, c.Name, read.Blobs!, skipped); }
            catch (Exception ex) when (ex is InvalidDataException or FormatException or ArgumentException or KeyNotFoundException
                or InvalidOperationException or IndexOutOfRangeException or System.Text.Json.JsonException)
            {
                skipped.Add($"{c.Name}: layout '{layout.Name}' could not read it ({ex.Message})");
                continue;
            }
            if (files.Count == 0 && read.Blobs!.Count > 0 && !skipped.Any(s => s.StartsWith(c.Name + "/", StringComparison.Ordinal)))
            {
                skipped.Add($"{c.Name}: not part of layout '{layout.Name}'");
            }
            foreach (var file in files)
            {
                if (file.Path.Length == 0 || file.Path.Split('/').Any(s => SanitizeFileName(s) != s))
                {
                    skipped.Add($"{c.Name}: '{file.Path}' is not a safe file name on this system");
                    continue;
                }
                if (!seen.Add(file.Path))
                {
                    return new WgsUnwrapResult(WgsOperationStatus.Refused, [], skipped,
                        $"Layout '{layout.Name}' maps two blobs to '{file.Path}'.");
                }
                planned.Add(file);
            }
        }
        var written = new List<string>();
        try
        {
            foreach (var file in planned)
            {
                var full = Path.Combine([folder, .. file.Path.Split('/')]);
                _fs.CreateDirectory(Path.GetDirectoryName(full)!);
                _fs.WriteAllBytes(full, file.Data);
                written.Add(full);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RemoveBestEffort(written);
            return new WgsUnwrapResult(IsLockConflict(ex) ? WgsOperationStatus.LockConflict : WgsOperationStatus.Failed,
                [], skipped, ex.Message);
        }
        return new WgsUnwrapResult(WgsOperationStatus.Ok, planned.Select(p => p.Path).ToList(), skipped, null);
    }

    private static List<WgsNativeFile> PerBlob(IWgsNativeLayout layout, WgsNativeContext context, string container,
        IReadOnlyDictionary<string, byte[]> blobs, List<string> skipped)
    {
        var files = new List<WgsNativeFile>();
        foreach (var (blob, data) in blobs)
        {
            var path = layout.ToNativePath(context, container, blob, blobs.Count);
            if (path is null) { skipped.Add($"{container}/{blob}: not part of layout '{layout.Name}'"); continue; }
            files.Add(new WgsNativeFile(path, layout.ToNativeContent(container, blob, data)));
        }
        return files;
    }

    /// <summary>Previews <see cref="TryWrap"/>. Writes nothing.</summary>
    public WgsWrapPlan PlanWrap(string folder, IWgsNativeLayout layout) => PrepareWrap(folder, layout, out _);

    private WgsWrapPlan PrepareWrap(string folder, IWgsNativeLayout layout, out PreparedImport prepared)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(layout);
        var problems = new List<string>();
        var unmapped = new List<string>();
        var groups = new Dictionary<string, Dictionary<string, byte[]>>(StringComparer.Ordinal);
        if (!layout.CanWrap)
        {
            problems.Add($"Layout '{layout.Name}' can only unwrap; it cannot rebuild containers from native files.");
            prepared = FinishPrepared([], problems, $"'{folder}'");
            return new WgsWrapPlan(prepared.Plan, unmapped);
        }
        var context = NativeContext();
        try
        {
            foreach (var rel in EnumerateRelative(folder, string.Empty).OrderBy(r => r, StringComparer.Ordinal))
            {
                var target = rel == ExportManifestFileName ? null : layout.FromNativePath(context, rel);
                if (target is null) { unmapped.Add(rel); continue; }
                var container = target.Container ?? (context.ContainerNames.Count > 0 ? context.ContainerNames[0] : null);
                if (string.IsNullOrEmpty(container))
                {
                    problems.Add($"'{rel}' belongs in the store's first container, and the store has none to put it in.");
                    continue;
                }
                byte[] data;
                try { data = layout.ToBlobContent(container, target.Blob, _fs.ReadAllBytes(Path.Combine([folder, .. rel.Split('/')]))); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    problems.Add($"'{rel}': {ex.Message}");
                    continue;
                }
                var blob = target.Blob ?? SoleBlobName(context, container);
                if (blob is null)
                {
                    problems.Add($"'{rel}' maps to '{container}', which holds several blobs; the layout must name one.");
                    continue;
                }
                if (!groups.TryGetValue(container, out var blobs)) groups[container] = blobs = new(StringComparer.Ordinal);
                if (!blobs.TryAdd(blob, data)) problems.Add($"'{rel}' maps to '{container}/{blob}', which another file already fills.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add($"The folder could not be read: {ex.Message}");
        }
        prepared = FinishPrepared(groups.Select(g => (g.Key, g.Value, true)).ToList(), problems, $"'{folder}'");
        return new WgsWrapPlan(prepared.Plan, unmapped);
    }

    /// <summary>
    /// Writes native files back into the store as <paramref name="layout"/> maps them. Every file is read and the whole
    /// plan checked before the first write; a plan with problems writes nothing.
    /// </summary>
    public WgsImportResult TryWrap(string folder, IWgsNativeLayout layout)
    {
        var plan = PrepareWrap(folder, layout, out var prepared);
        if (plan.Import.Problems.Count > 0)
        {
            return new WgsImportResult(WgsOperationStatus.Failed, [], "Nothing was wrapped: " + string.Join(" ", plan.Import.Problems));
        }
        if (!plan.Import.Assessment.CanWrite)
        {
            return new WgsImportResult(WgsOperationStatus.Refused, [], plan.Import.Assessment.BlockingMessage());
        }
        return ApplyPrepared(prepared);
    }

    /// <summary>The only blob's name of an existing container, the default for a new one, or null when it holds several.</summary>
    private static string? SoleBlobName(WgsNativeContext context, string container)
        => context.BlobNamesOf(container) switch
        {
            { Count: 0 } => WgsNativeLayouts.DefaultBlobName,
            { Count: 1 } names => names[0],
            _ => null,
        };

    private IEnumerable<string> EnumerateRelative(string root, string prefix)
    {
        var dir = prefix.Length == 0 ? root : Path.Combine([root, .. prefix.TrimEnd('/').Split('/')]);
        foreach (var f in _fs.EnumerateFiles(dir)) yield return prefix + Path.GetFileName(f);
        foreach (var d in _fs.EnumerateDirectories(dir))
        {
            foreach (var r in EnumerateRelative(root, prefix + Path.GetFileName(d) + "/")) yield return r;
        }
    }
}
