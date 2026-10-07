using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GamePassStorage;

/// <summary>One blob in an export manifest.</summary>
public sealed record WgsExportBlob(string Name, string File, long Size, string Sha256);

/// <summary>One container in an export manifest. <c>State</c> and <c>Etag</c> are informational only: an import
/// never applies them (an ETag is issued by the service, never carried over from a file).</summary>
public sealed record WgsExportContainer(string Name, WgsEntryState State, string Etag, byte ContainerNumber,
    IReadOnlyList<WgsExportBlob> Blobs);

/// <summary>The <c>wgs-export.json</c> document an export writes.</summary>
public sealed record WgsExportManifest(string Format, int Version, string PackageFamilyName,
    IReadOnlyList<WgsExportContainer> Containers);

/// <summary>Outcome of <see cref="WgsStore.TryExportTo"/>.</summary>
public sealed record WgsExportResult(WgsOperationStatus Status, WgsExportManifest? Manifest,
    IReadOnlyList<string> Skipped, string? Message)
{
    public bool Succeeded => Status == WgsOperationStatus.Ok;
}

/// <summary>What an import would do to one container.</summary>
public sealed record WgsImportItem(string ContainerName, bool IsNew, IReadOnlyList<string> BlobNames, long TotalBytes);

/// <summary>A preview of an import. Nothing is written to produce it. <c>Problems</c> block the import.</summary>
public sealed record WgsImportPlan(IReadOnlyList<WgsImportItem> Items, IReadOnlyList<string> Problems,
    WgsWriteAssessment Assessment)
{
    public bool CanApply => Problems.Count == 0 && Items.Count > 0 && Assessment.CanWrite;
}

/// <summary>Outcome of <see cref="WgsStore.TryImport"/>. On failure <c>Applied</c> lists the containers already written.</summary>
public sealed record WgsImportResult(WgsOperationStatus Status, IReadOnlyList<string> Applied, string? Message)
{
    public bool Succeeded => Status == WgsOperationStatus.Ok;
}

// Exporting every container's blobs to a folder and importing a folder back.
//
// Export layout: <folder>/<container>/<blob> for every container that is not a deletion tombstone
// (single-blob containers included, so the layout is uniform), plus <folder>/wgs-export.json listing
// names, states, ETags, sizes and SHA-256 of every file. Folder and file names are sanitised for the
// host filesystem; the JSON records the real container and blob names and the relative file paths.
//
// Import: reads wgs-export.json when present (verifying each file's size and SHA-256 and refusing a
// path that would leave the folder), otherwise takes <folder>/<container>/<blob> as it finds it.
// Each container is added (never uploaded: no ETag) or has the named blobs replaced (other blobs
// stay), through the same gate, concurrent-change check and generation write as every other write.
// It is not one transaction across containers: they are applied in turn and the first failure stops
// the import, leaving earlier containers fully written and later ones untouched.
public sealed partial class WgsStore
{
    /// <summary>File name of the export manifest.</summary>
    public const string ExportManifestFileName = "wgs-export.json";

    private static readonly JsonSerializerOptions ExportJson = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Writes every non-deleted container's blobs to <paramref name="folder"/> (which must not exist or
    /// be empty) and an export manifest. Reads the store only. On failure the files this call wrote are removed.
    /// </summary>
    public WgsExportResult TryExportTo(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        if (_fs.DirectoryExists(folder) && (_fs.EnumerateFiles(folder).Any() || _fs.EnumerateDirectories(folder).Any()))
        {
            return new WgsExportResult(WgsOperationStatus.Refused, null, [], $"'{folder}' is not empty.");
        }
        var written = new List<string>();
        var skipped = new List<string>();
        var containers = new List<WgsExportContainer>();
        var usedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            _fs.CreateDirectory(folder);
            foreach (var c in _containers)
            {
                if (c.RawState == (uint)WgsEntryState.Deleted)
                {
                    skipped.Add($"{c.Name}: deleted (pending deletion)");
                    continue;
                }
                var read = TryReadBlobs(c);
                if (!read.Succeeded)
                {
                    RemoveBestEffort(written);
                    return new WgsExportResult(read.Status, null, skipped, $"'{c.Name}' could not be read: {read.Message}");
                }
                var dirName = Unique(SanitizeFileName(c.Name), usedDirs);
                var dir = Path.Combine(folder, dirName);
                _fs.CreateDirectory(dir);
                var usedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var blobs = new List<WgsExportBlob>();
                foreach (var (name, data) in read.Blobs!)
                {
                    var fileName = Unique(SanitizeFileName(name), usedFiles);
                    var path = Path.Combine(dir, fileName);
                    _fs.WriteAllBytes(path, data);
                    written.Add(path);
                    blobs.Add(new WgsExportBlob(name, $"{dirName}/{fileName}", data.Length,
                        Convert.ToHexString(SHA256.HashData(data))));
                }
                containers.Add(new WgsExportContainer(c.Name, c.State, c.Etag, c.ContainerNumber, blobs));
            }
            var manifest = new WgsExportManifest("wgs-export", 1, PackageFamilyName, containers);
            var manifestPath = Path.Combine(folder, ExportManifestFileName);
            _fs.WriteAllBytes(manifestPath, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, ExportJson)));
            written.Add(manifestPath);
            return new WgsExportResult(WgsOperationStatus.Ok, manifest, skipped, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RemoveBestEffort(written);
            return new WgsExportResult(IsLockConflict(ex) ? WgsOperationStatus.LockConflict : WgsOperationStatus.Failed,
                null, skipped, ex.Message);
        }
    }

    internal sealed record ImportSource(string ContainerName, List<(string BlobName, string Path, long? Size, string? Sha256)> Blobs);
    internal sealed record PreparedImport(WgsImportPlan Plan, List<(string Name, Dictionary<string, byte[]> Blobs)> Containers);

    /// <summary>Previews <see cref="TryImport"/>: what would be added or replaced, and every problem that would stop it. Writes nothing.</summary>
    public WgsImportPlan PlanImport(string folder) => PrepareImport(folder).Plan;

    private PreparedImport PrepareImport(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var problems = new List<string>();
        List<ImportSource> sources;
        try { sources = DiscoverImport(folder, problems); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            problems.Add($"The import folder could not be read: {ex.Message}");
            sources = [];
        }
        return BuildPrepared(sources, problems, $"'{folder}'");
    }

    private PreparedImport BuildPrepared(List<ImportSource> sources, List<string> problems, string origin)
    {
        var loaded = new List<(string Name, Dictionary<string, byte[]> Blobs, bool Ok)>();
        foreach (var source in sources)
        {
            var ok = true;
            var blobs = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var b in source.Blobs)
            {
                var data = ReadVerified(source.ContainerName, b, problems);
                if (data is null) { ok = false; continue; }
                blobs.Add(b.BlobName, data);
            }
            loaded.Add((source.ContainerName, blobs, ok));
        }
        return FinishPrepared(loaded, problems, origin);
    }

    // Shared by import and wrap: checks each container against the store and builds the plan.
    private PreparedImport FinishPrepared(List<(string Name, Dictionary<string, byte[]> Blobs, bool Ok)> loaded,
        List<string> problems, string origin)
    {
        var items = new List<WgsImportItem>();
        var containers = new List<(string Name, Dictionary<string, byte[]> Blobs)>();
        foreach (var (name, blobs, readOk) in loaded)
        {
            var ok = readOk;
            var existing = Find(name);
            if (existing is not null && existing.RawState == (uint)WgsEntryState.Deleted)
            {
                problems.Add($"'{name}' is deleted and waiting for the cloud to learn of it; it cannot be imported over.");
                ok = false;
            }
            if (!ok) continue;
            try { ValidateBlobSet(blobs); }
            catch (ArgumentException ex) { problems.Add($"'{name}': {ex.Message}"); continue; }
            items.Add(new WgsImportItem(name, existing is null, blobs.Keys.ToList(), blobs.Values.Sum(v => (long)v.Length)));
            containers.Add((name, blobs));
        }
        if (items.Count == 0 && problems.Count == 0) problems.Add($"{origin} holds nothing to import.");
        return new PreparedImport(new WgsImportPlan(items, problems, AssessWrite()), containers);
    }

    /// <summary>
    /// Imports a folder written by <see cref="TryExportTo"/> (or laid out the same way by hand). Every file is
    /// read and verified before the first write; a plan with problems writes nothing.
    /// </summary>
    public WgsImportResult TryImport(string folder)
    {
        var prepared = PrepareImport(folder);
        var plan = prepared.Plan;
        if (plan.Problems.Count > 0)
        {
            return new WgsImportResult(WgsOperationStatus.Failed, [], "Nothing was imported: " + string.Join(" ", plan.Problems));
        }
        if (!plan.Assessment.CanWrite)
        {
            return new WgsImportResult(WgsOperationStatus.Refused, [], plan.Assessment.BlockingMessage());
        }
        return ApplyPrepared(prepared);
    }

    private WgsImportResult ApplyPrepared(PreparedImport prepared)
    {
        var applied = new List<string>();
        foreach (var (name, changes) in prepared.Containers)
        {
            var result = Find(name) is { } existing
                ? TryWriteBlobs(existing, changes)
                : TryCreateContainer(name, changes);
            if (!result.Succeeded)
            {
                return new WgsImportResult(result.Status, applied,
                    $"'{name}' was not imported ({result.Status}): {result.Message}");
            }
            applied.Add(name);
        }
        return new WgsImportResult(WgsOperationStatus.Ok, applied, null);
    }

    private List<ImportSource> DiscoverImport(string folder, List<string> problems)
    {
        var sources = new List<ImportSource>();
        var manifestPath = Path.Combine(folder, ExportManifestFileName);
        if (_fs.FileExists(manifestPath))
        {
            WgsExportManifest? manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<WgsExportManifest>(_fs.ReadAllBytes(manifestPath), ExportJson);
            }
            catch (JsonException ex)
            {
                problems.Add($"{ExportManifestFileName} is not valid: {ex.Message}");
                return sources;
            }
            if (manifest is null || manifest.Format != "wgs-export" || manifest.Containers is null || manifest.PackageFamilyName is null)
            {
                problems.Add($"{ExportManifestFileName} is not a wgs export manifest.");
                return sources;
            }
            if (manifest.Version != 1)
            {
                problems.Add($"Export manifest version {manifest.Version} is not supported.");
                return sources;
            }
            if (PackageFamilyName.Length > 0 && manifest.PackageFamilyName.Length > 0
                && !string.Equals(WgsGameAdapterRegistry.FamilyOf(PackageFamilyName),
                    WgsGameAdapterRegistry.FamilyOf(manifest.PackageFamilyName), StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"The export belongs to '{manifest.PackageFamilyName}', the store to '{PackageFamilyName}'.");
                return sources;
            }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in manifest.Containers)
            {
                if (c is null || string.IsNullOrWhiteSpace(c.Name) || c.Name.Contains('\0', StringComparison.Ordinal)
                    || c.Blobs is null || c.Blobs.Count == 0 || c.Blobs.Count > MaxManifestBlobs)
                {
                    problems.Add("The manifest lists a container with no name or no blobs.");
                    continue;
                }
                if (!seen.Add(c.Name)) { problems.Add($"The manifest lists '{c.Name}' twice."); continue; }
                var src = new ImportSource(c.Name, []);
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var b in c.Blobs)
                {
                    if (b is null || string.IsNullOrEmpty(b.Name) || b.Name.Length >= BlobNameFieldBytes / 2
                        || b.Name.Contains('\0', StringComparison.Ordinal) || !names.Add(b.Name))
                    {
                        problems.Add($"'{c.Name}' lists a null, invalid or duplicate blob name.");
                        continue;
                    }
                    if (b.Size < 0 || b.Sha256 is null || b.Sha256.Length != 64 || !IsHex(b.Sha256))
                    {
                        problems.Add($"'{c.Name}/{b.Name}' has an invalid size or SHA-256.");
                        continue;
                    }
                    var full = SafeRelativePath(folder, b.File);
                    if (full is null)
                    {
                        problems.Add($"'{c.Name}/{b.Name}': '{b.File}' is not a path inside the import folder.");
                        continue;
                    }
                    src.Blobs.Add((b.Name, full, b.Size, b.Sha256));
                }
                sources.Add(src);
            }
            return sources;
        }

        foreach (var dir in _fs.EnumerateDirectories(folder).OrderBy(d => d, StringComparer.Ordinal))
        {
            var src = new ImportSource(Path.GetFileName(dir), []);
            foreach (var file in _fs.EnumerateFiles(dir).OrderBy(f => f, StringComparer.Ordinal))
            {
                src.Blobs.Add((Path.GetFileName(file), file, null, null));
            }
            if (src.Blobs.Count > 0) sources.Add(src);
        }
        return sources;
    }

    private byte[]? ReadVerified(string container, (string BlobName, string Path, long? Size, string? Sha256) b, List<string> problems)
    {
        try
        {
            if (!_fs.FileExists(b.Path))
            {
                problems.Add($"'{container}/{b.BlobName}': the file is missing.");
                return null;
            }
            var data = _fs.ReadAllBytes(b.Path);
            if (b.Size is { } size && size != data.Length)
            {
                problems.Add($"'{container}/{b.BlobName}': the file is {data.Length} bytes, the manifest says {size}.");
                return null;
            }
            if (b.Sha256 is { Length: > 0 } sha && !string.Equals(sha, Convert.ToHexString(SHA256.HashData(data)), StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"'{container}/{b.BlobName}': the file does not match the manifest's SHA-256 (changed or damaged).");
                return null;
            }
            return data;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add($"'{container}/{b.BlobName}': {ex.Message}");
            return null;
        }
    }

    /// <summary>Resolves a manifest-supplied relative path under <paramref name="root"/>, or null when it is rooted or climbs out.</summary>
    private static string? SafeRelativePath(string root, string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return null;
        var normalized = relative.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Contains(':', StringComparison.Ordinal)) return null;
        var segments = normalized.Split('/');
        if (segments.Any(s => s.Length == 0 || s is "." or "..")) return null;
        return Path.Combine([root, .. segments]);
    }

    private static readonly string[] ReservedDeviceNames =
        ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
         "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

    /// <summary>A name safe on Windows and Linux: invalid characters replaced, no trailing dot or space, no device names.</summary>
    private static string SanitizeFileName(string name)
    {
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars()) { '/', '\\', ':', '*', '?', '"', '<', '>', '|' };
        var sb = new StringBuilder();
        foreach (var ch in name) sb.Append(invalid.Contains(ch) || char.IsControl(ch) ? '_' : ch);
        var s = sb.ToString().TrimEnd('.', ' ');
        if (s.Length > 100) s = s[..100].TrimEnd('.', ' ');
        if (s.Length == 0 || s is "." or "..") s = "_";
        var stem = s.Split('.')[0];
        if (ReservedDeviceNames.Contains(stem, StringComparer.OrdinalIgnoreCase)) s = "_" + s;
        return s;
    }

    private static string Unique(string name, HashSet<string> used)
    {
        var candidate = name;
        for (var i = 2; !used.Add(candidate); i++) candidate = $"{name}~{i}";
        return candidate;
    }
}
