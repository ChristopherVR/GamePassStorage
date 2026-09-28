using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GamePassStorage.Tool;

/// <summary>
/// The <c>wgs</c> command line. Every command reads unless it says otherwise; the only write
/// (<c>put</c>) requires a backup folder and goes through the library's write gate and
/// concurrent-change check.
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

        <store> is a wgs folder (the one holding containers.index) or a folder above it.
        Close the game and the Xbox app before `put`; local writes cannot control cloud sync.
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
            return command switch
            {
                "list" => List(rest, stdout, stderr),
                "diagnose" => Diagnose(rest, stdout, stderr),
                "extract" => Extract(rest, stdout, stderr),
                "backup" => Backup(rest, stdout, stderr),
                "snapshot" => Snapshot(rest, stdout, stderr),
                "compare" => Compare(rest, stdout, stderr),
                "put" => Put(rest, stdout, stderr),
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

    private static int List(Arguments a, TextWriter stdout, TextWriter stderr)
    {
        var json = a.Flag("--json");
        if (!TryOpen(a.Positional(0, "store"), stderr, out var store)) return Failure;
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
        foreach (var c in store.Containers)
        {
            var when = DateTime.FromFileTimeUtc(c.FileTime).ToString("u", CultureInfo.InvariantCulture);
            stdout.WriteLine($"  {c.Name,-40} {c.State,-9} {c.BlobSize,12:N0} bytes  {when}  {(string.IsNullOrEmpty(c.Etag) ? "(no etag)" : c.Etag)}");
        }
        return Ok;
    }

    private static int Diagnose(Arguments a, TextWriter stdout, TextWriter stderr)
    {
        var json = a.Flag("--json");
        if (!TryOpen(a.Positional(0, "store"), stderr, out var store)) return Failure;
        a.EnsureConsumed();
        var d = store.Diagnose();
        if (json)
        {
            stdout.WriteLine(JsonSerializer.Serialize(d, Json));
            return d.WriteAssessment.CanWrite ? Ok : Failure;
        }
        stdout.WriteLine($"index version: {d.IndexVersion}{(d.IsKnownIndexVersion ? "" : " (unrecognised)")}");
        stdout.WriteLine($"title: {d.PackageFamilyName}");
        stdout.WriteLine($"sync state: {d.SyncState}");
        stdout.WriteLine($"containers: {d.ContainerCount}");
        Section(stdout, "invalid state", d.InvalidStateContainers);
        Section(stdout, "unsafe to build on", d.UnsafeStateContainers);
        Section(stdout, "need repair", d.ContainersNeedingRepair);
        Section(stdout, "multi-blob (unsupported)", d.MultiBlobContainers);
        Section(stdout, "orphaned folders", d.Orphans.Select(o =>
            $"{o.FolderName} ({o.BlobSize:N0} bytes{(o.Label is null ? "" : ", " + o.Label)})").ToList());
        foreach (var concern in d.WriteAssessment.Concerns)
        {
            stdout.WriteLine($"{(concern.Blocking ? "BLOCKS WRITES" : "note")}: {concern.Message}");
        }
        stdout.WriteLine(d.WriteAssessment.CanWrite ? "writable: yes" : "writable: no");
        return d.WriteAssessment.CanWrite ? Ok : Failure;
    }

    private static int Extract(Arguments a, TextWriter stdout, TextWriter stderr)
    {
        var storePath = a.Positional(0, "store");
        var name = a.Positional(1, "container");
        var output = a.Positional(2, "out-file");
        a.EnsureConsumed();
        if (!TryOpen(storePath, stderr, out var store)) return Failure;
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

    private static int Backup(Arguments a, TextWriter stdout, TextWriter stderr)
    {
        var storePath = a.Positional(0, "store");
        var destination = a.Positional(1, "destination");
        a.EnsureConsumed();
        if (!TryOpen(storePath, stderr, out var store)) return Failure;
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
        {
            stderr.WriteLine($"error: '{destination}' is not empty.");
            return Failure;
        }
        store.CopyStoreTo(destination);
        stdout.WriteLine($"copied {store.RootPath} to {destination}");
        return Ok;
    }

    private static int Snapshot(Arguments a, TextWriter stdout, TextWriter stderr)
    {
        var output = a.Option("-o");
        if (!TryOpen(a.Positional(0, "store"), stderr, out var store)) return Failure;
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

    private static int Put(Arguments a, TextWriter stdout, TextWriter stderr)
    {
        var backup = a.Option("--backup");
        var dryRun = a.Flag("--dry-run");
        var storePath = a.Positional(0, "store");
        var name = a.Positional(1, "container");
        var blobPath = a.Positional(2, "blob-file");
        a.EnsureConsumed();
        if (backup is null && !dryRun)
        {
            throw new UsageException("put needs --backup <dir> (a whole-folder copy is the only rollback), or --dry-run.");
        }
        if (!TryOpen(storePath, stderr, out var store)) return Failure;
        var blob = File.ReadAllBytes(blobPath);

        var existing = store.Find(name);
        if (dryRun)
        {
            if (existing is null)
            {
                stdout.WriteLine($"would add a new container '{name}' ({blob.Length:N0} bytes)");
                var gate = store.AssessWrite();
                foreach (var c in gate.Concerns) stdout.WriteLine($"{(c.Blocking ? "BLOCKS WRITES" : "note")}: {c.Message}");
                return gate.CanWrite ? Ok : Failure;
            }
            var plan = store.PlanWrite(existing, blob.Length);
            stdout.WriteLine($"would write '{plan.ContainerName}' as container.{plan.NewNumber} ({plan.NewBlobSize:N0} bytes, state {plan.NewState})");
            foreach (var step in plan.Steps) stdout.WriteLine($"  - {step}");
            foreach (var file in plan.FilesToRemove) stdout.WriteLine($"  removes {file}");
            foreach (var c in plan.Assessment.Concerns) stdout.WriteLine($"{(c.Blocking ? "BLOCKS WRITES" : "note")}: {c.Message}");
            return plan.Assessment.CanWrite ? Ok : Failure;
        }

        if (Directory.Exists(backup) && Directory.EnumerateFileSystemEntries(backup!).Any())
        {
            stderr.WriteLine($"error: backup folder '{backup}' is not empty.");
            return Failure;
        }
        store.CopyStoreTo(backup!);
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

    private static bool TryOpen(string path, TextWriter stderr, out WgsStore store)
    {
        var folder = WgsStore.ResolveContainerFolder(path) ?? path;
        var opened = WgsStore.TryOpen(folder);
        if (!opened.Succeeded)
        {
            stderr.WriteLine($"error: {opened.Status}: {opened.Message ?? $"'{path}' is not a wgs save folder."}");
            store = null!;
            return false;
        }
        store = opened.Store!;
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

        public void EnsureConsumed()
        {
            var unknown = _args.FirstOrDefault(x => x.StartsWith('-'));
            if (unknown is not null) throw new UsageException($"unknown option '{unknown}'.");
        }
    }
}
