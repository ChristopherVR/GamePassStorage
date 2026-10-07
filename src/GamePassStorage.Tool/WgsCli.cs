using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GamePassStorage.Tool;

/// <summary>
/// The <c>wgs</c> command line. Every command reads unless it says otherwise; the writes
/// (<c>put</c>, <c>delete</c>, <c>restore</c>, <c>import</c>) require a backup folder (or
/// <c>--dry-run</c>) and go through the library's write gate and concurrent-change check.
/// </summary>
public static class WgsCli
{
    public const int Ok = 0;
    public const int Failure = 1;
    public const int Usage = 2;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private const string HelpText = """
        wgs - inspect and edit Xbox Connected Storage (Game Pass) save folders

        Usage:
          wgs list      <store> [--json]                     List containers in the index.
          wgs diagnose  <store> [--json]                     Report store health; changes nothing.
          wgs extract   <store> <container> <out-file>       Copy a container's blob to a file.
          wgs backup    <store> <destination>                Copy the whole store folder.
          wgs snapshot  <store> [-o <file.json>]             Fingerprint every container (SHA-256).
          wgs compare   <before.json> <after.json>           Describe what changed between snapshots.
          wgs put       <store> <container> <blob-file> --backup <dir> [--dry-run]
                                                             Add or replace a container's blob.
          wgs find      [--package <text>] [--exact] [--json]
                                                             Find wgs stores on this machine.
          wgs blobs     <store> <container> [--json]         List the blobs inside a container.
          wgs delete    <store> <container> --backup <dir> [--dry-run]
                                                             Delete a container (never-uploaded: removed;
                                                             known to the cloud: kept as a Deleted tombstone).
          wgs restore   <store> <backup-folder> --backup <dir> [--dry-run]
                                                             Restore a `wgs backup` over the store; the
                                                             current store is copied to <dir> first.
          wgs adapters  [--json]                             List the game adapters in use.
          wgs inspect   <store> [<container>] [--json]       Describe what containers hold, using the
                                                             matching game adapter (generic if none).
          wgs export    <store> <out-folder>                 Write every container's blobs and a manifest.
          wgs import    <store> <folder> --backup <dir> [--dry-run]
          wgs unwrap    <store> <out-folder> [--layout <spec>]       # the game's plain save files, no Xbox wrapper
          wgs wrap      <store> <folder> --backup <dir> [--layout <spec>] [--dry-run]
          (layouts: container-folders[:<suffix>], one-file[:<suffix>], blobs[:<container>]; default: the game's own, else container-folders)
                                                             Add or replace containers from an export folder.

        <store> is a wgs folder (the one holding containers.index) or a folder above it.
        Close the game and the Xbox app before any write; local writes cannot control cloud sync.
        Write commands also accept --refuse-if-running <name[,name]> to refuse while that process runs.
        Game adapters: built-in ones ship with the tool; more load from --adapters <dir> (repeatable, or
        WGS_ADAPTERS_DIR) and run with full trust. --no-builtin-adapters forces the generic model.
        `find` also accepts --local-app-data <dir> and --drive-root <dir> to search elsewhere.
        """;

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        if (args.Length == 0 || args[0] is "help" or "-h" or "--help")
        {
            stdout.WriteLine(HelpText);
            return args.Length == 0 ? Usage : Ok;
        }
        if (args[0] is "--version" or "version")
        {
            stdout.WriteLine(typeof(WgsCli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown");
            return Ok;
        }

        var command = args[0];
        var rest = new Arguments(args.Skip(1));
        try
        {
            var ctx = BuildContext(rest, stderr);
            return command switch
            {
                "adapters" => Adapters(rest, stdout, ctx),
                "inspect" => Inspect(rest, stdout, stderr, ctx),
                "list" => List(rest, stdout, stderr, ctx),
                "diagnose" => Diagnose(rest, stdout, stderr, ctx),
                "extract" => Extract(rest, stdout, stderr, ctx),
                "backup" => Backup(rest, stdout, stderr, ctx),
                "snapshot" => Snapshot(rest, stdout, stderr, ctx),
                "compare" => Compare(rest, stdout, stderr),
                "put" => Put(rest, stdout, stderr, ctx),
                "find" => Find(rest, stdout),
                "blobs" => Blobs(rest, stdout, stderr, ctx),
                "delete" => Delete(rest, stdout, stderr, ctx),
                "restore" => Restore(rest, stdout, stderr, ctx),
                "export" => Export(rest, stdout, stderr, ctx),
                "import" => Import(rest, stdout, stderr, ctx),
                "unwrap" => Unwrap(rest, stdout, stderr, ctx),
                "wrap" => Wrap(rest, stdout, stderr, ctx),
                _ => UsageError(stderr, $"Unknown command '{command}'."),
            };
        }
        catch (UsageException ex)
        {
            return UsageError(stderr, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
            or InvalidOperationException or JsonException)
        {
            stderr.WriteLine($"error: {ex.Message}");
            return Failure;
        }
    }

    /// <summary>Everything the adapter-aware commands share: the registry (built-ins plus plugins) and how it was loaded.</summary>
    private sealed record Ctx(WgsGameAdapterRegistry Registry, IReadOnlyList<string> BuiltInIds,
        WgsAdapterLoadResult Loaded, IReadOnlyList<string> Problems);

    /// <summary>
    /// Builds the registry: the adapters that ship with the tool (unless <c>--no-builtin-adapters</c>), then
    /// third-party adapters from <c>--adapters &lt;dir&gt;</c> and <c>WGS_ADAPTERS_DIR</c>. Plugins run with
    /// full trust; a problem loading one is reported as a warning and never stops the command.
    /// </summary>
    private static Ctx BuildContext(Arguments a, TextWriter stderr)
    {
        var noBuiltIns = a.Flag("--no-builtin-adapters");
        var dirs = new List<string>();
        while (a.Option("--adapters") is { } dir) dirs.AddRange(dir.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        dirs.AddRange(WgsAdapterLoader.DirectoriesFromEnvironment());

        var registry = new WgsGameAdapterRegistry();
        var builtInIds = new List<string>();
        var problems = new List<string>();
        if (!noBuiltIns)
        {
            foreach (var adapter in BuiltInAdapters.Create())
            {
                registry.Register(adapter);
                builtInIds.Add(adapter.Id);
            }
        }
        var loaded = dirs.Count == 0 ? WgsAdapterLoadResult.Empty : WgsAdapterLoader.LoadFromDirectories(dirs);
        problems.AddRange(loaded.Errors);
        problems.AddRange(loaded.RegisterInto(registry));
        foreach (var p in problems) stderr.WriteLine($"warning: adapter: {p}");
        return new Ctx(registry, builtInIds, loaded, problems);
    }

    private static int Adapters(Arguments a, TextWriter stdout, Ctx ctx)
    {
        var json = a.Flag("--json");
        a.EnsureConsumed();
        var rows = ctx.Registry.Adapters.Select(x => new
        {
            x.Id,
            x.DisplayName,
            Source = ctx.BuiltInIds.Contains(x.Id, StringComparer.OrdinalIgnoreCase) ? "built-in" : "plugin",
            Assembly = x.GetType().Assembly.Location,
            PackageFamilyNames = x.KnownPackageFamilyNames,
            Conventions = x.ContainerNameConventions,
            Codec = x.Codec?.Name,
            NativeLayout = x.NativeLayout is { } l ? l.Name + (l.CanWrap ? "" : " (unwrap only)") : null,
            HasBlobInspector = x.BlobInspector is not null,
            HasWriteGate = x.WriteGate is not null,
        }).ToList();
        if (json)
        {
            stdout.WriteLine(JsonSerializer.Serialize(rows, Json));
            return Ok;
        }
        foreach (var r in rows)
        {
            stdout.WriteLine($"{r.Id}  {r.DisplayName}  [{r.Source}]");
            if (r.PackageFamilyNames.Count > 0) stdout.WriteLine($"  serves: {string.Join(", ", r.PackageFamilyNames)}");
            foreach (var c in r.Conventions) stdout.WriteLine($"  container: {c}");
            stdout.WriteLine($"  inspector: {(r.HasBlobInspector ? "yes" : "no")}  write gate: {(r.HasWriteGate ? "yes" : "no")}  codec: {r.Codec ?? "none"}  native layout: {r.NativeLayout ?? "none"}");
            if (r.Source == "plugin") stdout.WriteLine($"  loaded from: {r.Assembly}");
        }
        stdout.WriteLine($"{GenericWgsAdapter.GenericId}  {GenericWgsAdapter.Instance.DisplayName}  [always last]");
        return Ok;
    }

    private static int Inspect(Arguments a, TextWriter stdout, TextWriter stderr, Ctx ctx)
    {
        var json = a.Flag("--json");
        var storePath = a.Positional(0, "store");
        var only = a.HasPositional(1) ? a.Positional(1, "container") : null;
        a.EnsureConsumed();
        if (!TryOpen(ctx, storePath, stderr, out var store, out var adapter)) return Failure;
        var targets = store.Containers.Where(c => only is null || string.Equals(c.Name, only, StringComparison.OrdinalIgnoreCase)).ToList();
        if (targets.Count == 0)
        {
            stderr.WriteLine(only is null ? "error: the store has no containers." : $"error: no container named '{only}'.");
            return Failure;
        }
        var results = new List<object>();
        var failed = false;
        if (!json) stdout.WriteLine($"adapter: {adapter.DisplayName} ({adapter.Id})");
        foreach (var c in targets)
        {
            if (c.RawState == (uint)WgsEntryState.Deleted)
            {
                if (!json) stdout.WriteLine($"{c.Name}: deleted (pending deletion)");
                continue;
            }
            var read = store.TryReadBlobs(c);
            if (!read.Succeeded)
            {
                failed = true;
                stderr.WriteLine($"error: {c.Name}: {read.Status}: {read.Message}");
                continue;
            }
            foreach (var (blobName, bytes) in read.Blobs!)
            {
                WgsContentDescription description;
                try
                {
                    description = read.Blobs.Count == 1 ? adapter.Describe(c, bytes) : adapter.Describe(c, blobName, bytes);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    description = WgsContentDescription.Generic(c.Name, bytes) with
                    {
                        Notes = [$"The {adapter.Id} adapter failed on this blob ({ex.GetType().Name}: {ex.Message}); showing the generic view."],
                    };
                }
                results.Add(new { Container = c.Name, Blob = blobName, Adapter = adapter.Id, Description = description });
                if (json) continue;
                stdout.WriteLine($"{c.Name}{(read.Blobs.Count > 1 ? "/" + blobName : string.Empty)}  [{description.Kind}]");
                stdout.WriteLine($"  {description.Summary}");
                foreach (var m in description.Members)
                {
                    stdout.WriteLine($"    {m.Name}{(m.Size is { } sz ? $"  {sz:N0} bytes" : string.Empty)}{(m.Type is null ? string.Empty : $"  {m.Type}")}{(m.Note is null ? string.Empty : $"  ({m.Note})")}");
                }
                foreach (var n in description.Notes) stdout.WriteLine($"  note: {n}");
                if (description.Preview is not null)
                {
                    stdout.WriteLine("  ---");
                    foreach (var line in description.Preview.TrimEnd((char)13, (char)10).Split('\n')) stdout.WriteLine($"  {line.TrimEnd('\r')}");
                    stdout.WriteLine("  ---");
                }
            }
            if (adapter.Codec is { } codec && !json && read.Blobs.Count == 1 && c.RawState != (uint)WgsEntryState.Deleted)
            {
                var decode = codec.TryDecode(c, read.Blobs.Values.First());
                stdout.WriteLine($"  codec {codec.Name}: {(decode.Succeeded ? $"decodes to {decode.Data!.Length:N0} bytes" : "unavailable: " + decode.Message)}");
            }
        }
        if (json) stdout.WriteLine(JsonSerializer.Serialize(results, Json));
        return failed ? Failure : Ok;
    }

    private static int List(Arguments a, TextWriter stdout, TextWriter stderr, Ctx ctx)
    {
        var json = a.Flag("--json");
        if (!TryOpen(ctx, a.Positional(0, "store"), stderr, out var store, out var adapter)) return Failure;
        a.EnsureConsumed();
        if (json)
        {
            stdout.WriteLine(JsonSerializer.Serialize(store.Containers.Select(c => new
            {
                c.Name, c.State, c.Etag, c.ContainerNumber, c.BlobSize, c.FolderName,
                LastWrittenUtc = DateTime.FromFileTimeUtc(c.FileTime),
            }), Json));
            return Ok;
        }
        stdout.WriteLine($"{store.RootPath}");
        stdout.WriteLine($"title: {store.PackageFamilyName}  sync: {store.SyncState}  containers: {store.Containers.Count}");
        stdout.WriteLine($"adapter: {adapter.DisplayName} ({adapter.Id})");
        foreach (var c in store.Containers)
        {
            var when = DateTime.FromFileTimeUtc(c.FileTime).ToString("u", CultureInfo.InvariantCulture);
            stdout.WriteLine($"  {c.Name,-40} {c.State,-9} {c.BlobSize,12:N0} bytes  {when}  {(string.IsNullOrEmpty(c.Etag) ? "(no etag)" : c.Etag)}");
        }
        return Ok;
    }

    private static int Diagnose(Arguments a, TextWriter stdout, TextWriter stderr, Ctx ctx)
    {
        var json = a.Flag("--json");
        if (!TryOpen(ctx, a.Positional(0, "store"), stderr, out var store, out var adapter)) return Failure;
        a.EnsureConsumed();
        var d = store.Diagnose();
        if (json)
        {
            stdout.WriteLine(JsonSerializer.Serialize(d, Json));
            return d.WriteAssessment.CanWrite ? Ok : Failure;
        }
        stdout.WriteLine($"index version: {d.IndexVersion}{(d.IsKnownIndexVersion ? "" : " (unrecognised)")}");
        stdout.WriteLine($"title: {d.PackageFamilyName}");
        stdout.WriteLine($"adapter: {adapter.DisplayName} ({adapter.Id})");
        stdout.WriteLine($"sync state: {d.SyncState}");
        stdout.WriteLine($"containers: {d.ContainerCount}");
        Section(stdout, "invalid state", d.InvalidStateContainers);
        Section(stdout, "unsafe to build on", d.UnsafeStateContainers);
        Section(stdout, "need repair", d.ContainersNeedingRepair);
        Section(stdout, "multi-blob (supported; see `wgs blobs`)", d.MultiBlobContainers);
        Section(stdout, "malformed manifest (damaged)", d.MalformedManifestContainers);
        Section(stdout, "orphaned folders", d.Orphans.Select(o =>
            $"{o.FolderName} ({o.BlobSize:N0} bytes{(o.Label is null ? "" : ", " + o.Label)})").ToList());
        foreach (var concern in d.WriteAssessment.Concerns)
        {
            stdout.WriteLine($"{(concern.Blocking ? "BLOCKS WRITES" : "note")}: {concern.Message}");
        }
        stdout.WriteLine(d.WriteAssessment.CanWrite ? "writable: yes" : "writable: no");
        return d.WriteAssessment.CanWrite ? Ok : Failure;
    }

    private static int Extract(Arguments a, TextWriter stdout, TextWriter stderr, Ctx ctx)
    {
        var storePath = a.Positional(0, "store");
        var name = a.Positional(1, "container");
        var output = a.Positional(2, "out-file");
        a.EnsureConsumed();
        if (!TryOpen(ctx, storePath, stderr, out var store)) return Failure;
        var container = store.Find(name);
        if (container is null)
        {
            stderr.WriteLine($"error: no container named '{name}'.");
            return Failure;
        }
        var read = store.TryReadBlob(container);
        if (!read.Succeeded)
        {
            stderr.WriteLine($"error: {read.Status}: {read.Message}");
            return Failure;
        }
        File.WriteAllBytes(output, read.Blob!);
        stdout.WriteLine($"wrote {read.Blob!.Length:N0} bytes to {output}{(read.UsedFallback ? " (read through the previous blob id)" : "")}");
        return Ok;
    }

    private static int Backup(Arguments a, TextWriter stdout, TextWriter stderr, Ctx ctx)
    {
        var storePath = a.Positional(0, "store");
        var destination = a.Positional(1, "destination");
        a.EnsureConsumed();
        if (!TryOpen(ctx, storePath, stderr, out var store)) return Failure;
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
        {
            stderr.WriteLine($"error: '{destination}' is not empty.");
            return Failure;
        }
        store.CopyStoreTo(destination);
        stdout.WriteLine($"copied {store.RootPath} to {destination}");
        return Ok;
    }

    private static int Snapshot(Arguments a, TextWriter stdout, TextWriter stderr, Ctx ctx)
    {
        var output = a.Option("-o");
        if (!TryOpen(ctx, a.Positional(0, "store"), stderr, out var store)) return Failure;
        a.EnsureConsumed();
        var text = JsonSerializer.Serialize(WgsSnapshot.Capture(store), Json);
        if (output is null)
        {
            stdout.WriteLine(text);
        }
        else
        {
            File.WriteAllText(output, text);
            stdout.WriteLine($"snapshot of {store.Containers.Count} containers written to {output}");
        }
        return Ok;
    }

    private static int Compare(Arguments a, TextWriter stdout, TextWriter stderr)
    {
        var before = Load(a.Positional(0, "before.json"));
        var after = Load(a.Positional(1, "after.json"));
        a.EnsureConsumed();
        var lines = WgsSnapshot.Compare(before, after);
        if (lines.Count == 0)
        {
            stdout.WriteLine("identical");
            return Ok;
        }
        foreach (var line in lines) stdout.WriteLine(line);
        return Ok;

        static WgsSnapshot Load(string path)
            => JsonSerializer.Deserialize<WgsSnapshot>(File.ReadAllText(path), Json)
               ?? throw new InvalidDataException($"'{path}' is not a snapshot.");
    }

    private static int Put(Arguments a, TextWriter stdout, TextWriter stderr, Ctx ctx)
    {
        var backup = a.Option("--backup");
        var dryRun = a.Flag("--dry-run");
        var gate = a.Option("--refuse-if-running");
        var storePath = a.Positional(0, "store");
        var name = a.Positional(1, "container");
        var blobPath = a.Positional(2, "blob-file");
        a.EnsureConsumed();
        RequireBackupOrDryRun("put", backup, dryRun);
        if (!TryOpen(ctx, storePath, stderr, out var store, gate)) return Failure;
        var blob = File.ReadAllBytes(blobPath);

        var existing = store.Find(name);
        if (dryRun)
        {
            if (existing is null)
            {
                stdout.WriteLine($"would add a new container '{name}' ({blob.Length:N0} bytes)");
                var verdict = store.AssessWrite();
                foreach (var c in verdict.Concerns) stdout.WriteLine($"{(c.Blocking ? "BLOCKS WRITES" : "note")}: {c.Message}");
                return verdict.CanWrite ? Ok : Failure;
            }
            var plan = store.PlanWrite(existing, blob.Length);
            stdout.WriteLine($"would write '{plan.ContainerName}' as container.{plan.NewNumber} ({plan.NewBlobSize:N0} bytes, state {plan.NewState})");
            foreach (var step in plan.Steps) stdout.WriteLine($"  - {step}");
            foreach (var file in plan.FilesToRemove) stdout.WriteLine($"  removes {file}");
            foreach (var c in plan.Assessment.Concerns) stdout.WriteLine($"{(c.Blocking ? "BLOCKS WRITES" : "note")}: {c.Message}");
            return plan.Assessment.CanWrite ? Ok : Failure;
        }

        if (!TakeBackup(store, backup!, stderr)) return Failure;
        var result = existing is null
            ? store.TryAddOrReplaceContainer(name, blob)
            : store.TryWriteBlob(existing, blob);
        if (!result.Status.Equals(WgsOperationStatus.Ok))
        {
            stderr.WriteLine($"error: {result.Status}: {result.Message}");
            stderr.WriteLine($"nothing was written; the backup is at {backup}");
            return Failure;
        }
        stdout.WriteLine($"wrote '{name}' ({blob.Length:N0} bytes) as {result.Container!.State}; backup at {backup}");
        return Ok;
    }

    private static int Find(Arguments a, TextWriter stdout)
    {
        var json = a.Flag("--json");
        var exact = a.Flag("--exact");
        var package = a.Option("--package");
        var localAppData = a.Option("--local-app-data");
        var driveRoot = a.Option("--drive-root");
        a.EnsureConsumed();
        var options = new WgsStoreDiscoveryOptions
        {
            LocalAppData = localAppData,
            DriveRoots = driveRoot is null ? null : [driveRoot],
        };
        var found = WgsStoreDiscovery.Find(package, exact, options);
        if (json)
        {
            stdout.WriteLine(JsonSerializer.Serialize(found, Json));
            return Ok;
        }
        if (found.Count == 0)
        {
            stdout.WriteLine("no wgs stores found");
            return Ok;
        }
        foreach (var l in found)
        {
            stdout.WriteLine($"{l.PackageFamilyName}  xuid {l.Xuid}  scid {l.Scid}  [{l.Source}]");
            stdout.WriteLine($"  {l.StorePath}");
        }
        return Ok;
    }

    private static int Blobs(Arguments a, TextWriter stdout, TextWriter stderr, Ctx ctx)
    {
        var json = a.Flag("--json");
        var storePath = a.Positional(0, "store");
        var name = a.Positional(1, "container");
        a.EnsureConsumed();
        if (!TryOpen(ctx, storePath, stderr, out var store)) return Failure;
        var container = store.Find(name);
        if (container is null)
        {
            stderr.WriteLine($"error: no container named '{name}'.");
            return Failure;
        }
        var list = store.TryListBlobs(container);
        if (!list.Succeeded)
        {
            stderr.WriteLine($"error: {list.Status}: {list.Message}");
            return Failure;
        }
        if (json)
        {
            stdout.WriteLine(JsonSerializer.Serialize(list.Blobs, Json));
            return Ok;
        }
        stdout.WriteLine($"{container.Name}: {list.Blobs!.Count} blob(s)");
        foreach (var b in list.Blobs)
        {
            var size = b.Size is { } n ? $"{n:N0} bytes" : "MISSING on disk";
            stdout.WriteLine($"  {b.Name,-40} {size,16}  {b.LocalId:N}{(b.SyncInFlight ? "  (sync in flight)" : "")}");
        }
        return Ok;
    }

    private static int Delete(Arguments a, TextWriter stdout, TextWriter stderr, Ctx ctx)
    {
        var backup = a.Option("--backup");
        var dryRun = a.Flag("--dry-run");
        var gate = a.Option("--refuse-if-running");
        var storePath = a.Positional(0, "store");
        var name = a.Positional(1, "container");
        a.EnsureConsumed();
        RequireBackupOrDryRun("delete", backup, dryRun);
        if (!TryOpen(ctx, storePath, stderr, out var store, gate)) return Failure;
        var container = store.Find(name);
        if (container is null)
        {
            stderr.WriteLine($"error: no container named '{name}'.");
            return Failure;
        }
        if (dryRun)
        {
            var plan = store.PlanDelete(container);
            stdout.WriteLine($"would delete '{plan.ContainerName}' ({plan.Action})");
            foreach (var step in plan.Steps) stdout.WriteLine($"  - {step}");
            foreach (var c in plan.Assessment.Concerns) stdout.WriteLine($"{(c.Blocking ? "BLOCKS WRITES" : "note")}: {c.Message}");
            return plan.Assessment.CanWrite ? Ok : Failure;
        }
        if (!TakeBackup(store, backup!, stderr)) return Failure;
        var result = store.TryDeleteContainer(container);
        if (!result.Succeeded)
        {
            stderr.WriteLine($"error: {result.Status}: {result.Message}");
            stderr.WriteLine($"nothing was written; the backup is at {backup}");
            return Failure;
        }
        stdout.WriteLine(result.Container is null
            ? $"removed '{name}' (never uploaded); backup at {backup}"
            : $"marked '{name}' Deleted, keeping its ETag so the deletion syncs; backup at {backup}");
        return Ok;
    }

    private static int Restore(Arguments a, TextWriter stdout, TextWriter stderr, Ctx ctx)
    {
        var safety = a.Option("--backup");
        var dryRun = a.Flag("--dry-run");
        var gate = a.Option("--refuse-if-running");
        var storePath = a.Positional(0, "store");
        var backupPath = a.Positional(1, "backup-folder");
        a.EnsureConsumed();
        RequireBackupOrDryRun("restore", safety, dryRun);
        if (!TryOpen(ctx, storePath, stderr, out var current, out var adapter, gate)) return Failure;
        var options = WgsGameAdapterRegistry.CreateOptions(adapter, OptionsFor(gate));
        var storeFolder = WgsStore.ResolveContainerFolder(storePath) ?? storePath;
        var backupFolder = WgsStore.ResolveContainerFolder(backupPath) ?? backupPath;
        if (dryRun)
        {
            var problems = WgsStore.ValidateStore(backupFolder, options);
            if (problems.Count > 0)
            {
                stderr.WriteLine($"error: '{backupPath}' is not a valid store:");
                foreach (var p in problems) stderr.WriteLine($"  {p}");
                return Failure;
            }
            var backupStore = WgsStore.Open(backupFolder, options);
            stdout.WriteLine($"would replace {current.Containers.Count} container(s) with the backup's {backupStore.Containers.Count}");
            stdout.WriteLine("  - copy the current store to the --backup folder first");
            stdout.WriteLine("  - restore blobs and manifests, then the index with a newer timestamp");
            var gateResult = current.AssessWrite();
            foreach (var c in gateResult.Concerns) stdout.WriteLine($"{(c.Blocking ? "BLOCKS WRITES" : "note")}: {c.Message}");
            return gateResult.CanWrite ? Ok : Failure;
        }
        var result = WgsStore.TryRestore(storeFolder, backupFolder, safety!, options);
        if (!result.Succeeded)
        {
            stderr.WriteLine($"error: {result.Status}: {result.Message}");
            foreach (var p in result.Problems) stderr.WriteLine($"  {p}");
            if (result.SafetyCopyPath is not null) stderr.WriteLine($"the previous store is kept at {result.SafetyCopyPath}");
            return Failure;
        }
        stdout.WriteLine($"restored {result.Store!.Containers.Count} container(s) from {backupFolder}; previous store at {result.SafetyCopyPath}");
        return Ok;
    }

    private static int Export(Arguments a, TextWriter stdout, TextWriter stderr, Ctx ctx)
    {
        var storePath = a.Positional(0, "store");
        var folder = a.Positional(1, "out-folder");
        a.EnsureConsumed();
        if (!TryOpen(ctx, storePath, stderr, out var store)) return Failure;
        var result = store.TryExportTo(folder);
        if (!result.Succeeded)
        {
            stderr.WriteLine($"error: {result.Status}: {result.Message}");
            return Failure;
        }
        foreach (var s in result.Skipped) stdout.WriteLine($"skipped {s}");
        var blobs = result.Manifest!.Containers.Sum(c => c.Blobs.Count);
        stdout.WriteLine($"exported {result.Manifest.Containers.Count} container(s), {blobs} blob(s) to {folder} ({WgsStore.ExportManifestFileName})");
        return Ok;
    }

    private static int Import(Arguments a, TextWriter stdout, TextWriter stderr, Ctx ctx)
    {
        var backup = a.Option("--backup");
        var dryRun = a.Flag("--dry-run");
        var gate = a.Option("--refuse-if-running");
        var storePath = a.Positional(0, "store");
        var folder = a.Positional(1, "folder");
        a.EnsureConsumed();
        RequireBackupOrDryRun("import", backup, dryRun);
        if (!TryOpen(ctx, storePath, stderr, out var store, gate)) return Failure;
        if (dryRun)
        {
            var plan = store.PlanImport(folder);
            foreach (var item in plan.Items)
            {
                stdout.WriteLine($"would {(item.IsNew ? "add" : "replace")} '{item.ContainerName}': {string.Join(", ", item.BlobNames)} ({item.TotalBytes:N0} bytes)");
            }
            foreach (var p in plan.Problems) stdout.WriteLine($"PROBLEM: {p}");
            foreach (var c in plan.Assessment.Concerns) stdout.WriteLine($"{(c.Blocking ? "BLOCKS WRITES" : "note")}: {c.Message}");
            return plan.CanApply ? Ok : Failure;
        }
        var check = store.PlanImport(folder);
        if (!check.CanApply)
        {
            foreach (var p in check.Problems) stderr.WriteLine($"error: {p}");
            if (!check.Assessment.CanWrite) stderr.WriteLine($"error: {check.Assessment.BlockingMessage()}");
            stderr.WriteLine("nothing was written");
            return Failure;
        }
        if (!TakeBackup(store, backup!, stderr)) return Failure;
        var result = store.TryImport(folder);
        foreach (var name in result.Applied) stdout.WriteLine($"imported '{name}'");
        if (!result.Succeeded)
        {
            stderr.WriteLine($"error: {result.Status}: {result.Message}");
            stderr.WriteLine($"the backup is at {backup}");
            return Failure;
        }
        stdout.WriteLine($"imported {result.Applied.Count} container(s); backup at {backup}");
        return Ok;
    }

    private static IWgsNativeLayout ResolveLayout(string? spec, IWgsGameAdapter adapter)
    {
        if (spec is not null)
        {
            return WgsNativeLayouts.Parse(spec)
                ?? throw new UsageException($"Unknown layout '{spec}'. Use container-folders[:<suffix>], one-file[:<suffix>] or blobs[:<container>].");
        }
        // container-folders is lossless for any store, so it is the default when the game has no layout of its own.
        return adapter.NativeLayout ?? WgsNativeLayouts.ContainerFolders;
    }

    private static int Unwrap(Arguments a, TextWriter stdout, TextWriter stderr, Ctx ctx)
    {
        var spec = a.Option("--layout");
        var storePath = a.Positional(0, "store");
        var folder = a.Positional(1, "out-folder");
        a.EnsureConsumed();
        if (!TryOpen(ctx, storePath, stderr, out var store, out var adapter)) return Failure;
        var layout = ResolveLayout(spec, adapter);
        var result = store.TryUnwrapTo(folder, layout);
        foreach (var s in result.Skipped) stdout.WriteLine($"skipped {s}");
        if (!result.Succeeded)
        {
            stderr.WriteLine($"error: {result.Status}: {result.Message}");
            return Failure;
        }
        foreach (var f in result.Files) stdout.WriteLine($"wrote {f}");
        stdout.WriteLine($"unwrapped {result.Files.Count} file(s) to {folder} (layout {layout.Name})");
        return Ok;
    }

    private static int Wrap(Arguments a, TextWriter stdout, TextWriter stderr, Ctx ctx)
    {
        var spec = a.Option("--layout");
        var backup = a.Option("--backup");
        var dryRun = a.Flag("--dry-run");
        var gate = a.Option("--refuse-if-running");
        var storePath = a.Positional(0, "store");
        var folder = a.Positional(1, "folder");
        a.EnsureConsumed();
        RequireBackupOrDryRun("wrap", backup, dryRun);
        if (!TryOpen(ctx, storePath, stderr, out var store, out var adapter, gate)) return Failure;
        var layout = ResolveLayout(spec, adapter);
        var plan = store.PlanWrap(folder, layout);
        foreach (var u in plan.Unmapped) stdout.WriteLine($"ignored {u} (not part of layout {layout.Name})");
        if (dryRun)
        {
            foreach (var item in plan.Import.Items)
            {
                stdout.WriteLine($"would {(item.IsNew ? "add" : "replace")} '{item.ContainerName}': {string.Join(", ", item.BlobNames)} ({item.TotalBytes:N0} bytes)");
            }
            foreach (var p in plan.Import.Problems) stdout.WriteLine($"PROBLEM: {p}");
            foreach (var c in plan.Import.Assessment.Concerns) stdout.WriteLine($"{(c.Blocking ? "BLOCKS WRITES" : "note")}: {c.Message}");
            return plan.CanApply ? Ok : Failure;
        }
        if (!plan.CanApply)
        {
            foreach (var p in plan.Import.Problems) stderr.WriteLine($"error: {p}");
            if (!plan.Import.Assessment.CanWrite) stderr.WriteLine($"error: {plan.Import.Assessment.BlockingMessage()}");
            stderr.WriteLine("nothing was written");
            return Failure;
        }
        if (!TakeBackup(store, backup!, stderr)) return Failure;
        var result = store.TryWrap(folder, layout);
        foreach (var name in result.Applied) stdout.WriteLine($"wrapped '{name}'");
        if (!result.Succeeded)
        {
            stderr.WriteLine($"error: {result.Status}: {result.Message}");
            stderr.WriteLine($"the backup is at {backup}");
            return Failure;
        }
        stdout.WriteLine($"wrapped {result.Applied.Count} container(s); backup at {backup}");
        return Ok;
    }

    private static void RequireBackupOrDryRun(string command, string? backup, bool dryRun)
    {
        if (backup is null && !dryRun)
        {
            throw new UsageException($"{command} needs --backup <dir> (a whole-folder copy is the only rollback), or --dry-run.");
        }
    }

    private static bool TakeBackup(WgsStore store, string backup, TextWriter stderr)
    {
        if (Directory.Exists(backup) && Directory.EnumerateFileSystemEntries(backup).Any())
        {
            stderr.WriteLine($"error: backup folder '{backup}' is not empty.");
            return false;
        }
        store.CopyStoreTo(backup);
        return true;
    }

    private static WgsStoreOptions OptionsFor(string? refuseIfRunning)
    {
        if (string.IsNullOrWhiteSpace(refuseIfRunning)) return WgsStoreOptions.Default;
        var names = refuseIfRunning.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (names.Length == 0) throw new UsageException("--refuse-if-running needs at least one process name.");
        return new WgsStoreOptions
        {
            WriteGate = WgsWriteGates.Combine(WgsWriteGates.Structural, WgsWriteGates.RefuseWhileRunning(names)),
        };
    }

    private static bool TryOpen(Ctx ctx, string path, TextWriter stderr, out WgsStore store, string? refuseIfRunning = null)
        => TryOpen(ctx, path, stderr, out store, out _, refuseIfRunning);

    /// <summary>Opens a store with the adapter that serves it (a game adapter's gate and inspector, or the generic fallback).</summary>
    private static bool TryOpen(Ctx ctx, string path, TextWriter stderr, out WgsStore store, out IWgsGameAdapter adapter,
        string? refuseIfRunning = null)
    {
        var folder = WgsStore.ResolveContainerFolder(path) ?? path;
        var opened = ctx.Registry.TryOpen(folder, OptionsFor(refuseIfRunning));
        if (!opened.Open.Succeeded)
        {
            stderr.WriteLine($"error: {opened.Open.Status}: {opened.Open.Message ?? $"'{path}' is not a wgs save folder."}");
            store = null!;
            adapter = GenericWgsAdapter.Instance;
            return false;
        }
        store = opened.Open.Store!;
        adapter = opened.Adapter ?? GenericWgsAdapter.Instance;
        return true;
    }

    private static void Section(TextWriter stdout, string title, IReadOnlyList<string> items)
    {
        if (items.Count == 0) return;
        stdout.WriteLine($"{title}:");
        foreach (var item in items) stdout.WriteLine($"  {item}");
    }

    private static int UsageError(TextWriter stderr, string message)
    {
        stderr.WriteLine($"error: {message}");
        stderr.WriteLine("Run `wgs help` for usage.");
        return Usage;
    }

    private sealed class UsageException(string message) : Exception(message);

    /// <summary>Minimal argument reader: flags, options with a value, and positionals.</summary>
    private sealed class Arguments(IEnumerable<string> args)
    {
        private readonly List<string> _args = args.ToList();

        public bool Flag(string name) => _args.Remove(name);

        public string? Option(string name)
        {
            var i = _args.IndexOf(name);
            if (i < 0) return null;
            if (i + 1 >= _args.Count) throw new UsageException($"{name} needs a value.");
            var value = _args[i + 1];
            _args.RemoveRange(i, 2);
            return value;
        }

        public string Positional(int index, string label)
        {
            var positionals = _args.Where(x => !x.StartsWith('-')).ToList();
            if (index >= positionals.Count) throw new UsageException($"missing <{label}>.");
            return positionals[index];
        }

        public bool HasPositional(int index) => _args.Count(x => !x.StartsWith('-')) > index;

        public void EnsureConsumed()
        {
            var unknown = _args.FirstOrDefault(x => x.StartsWith('-'));
            if (unknown is not null) throw new UsageException($"unknown option '{unknown}'.");
        }
    }
}
