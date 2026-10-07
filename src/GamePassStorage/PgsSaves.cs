using System.Globalization;
using System.Text.RegularExpressions;

namespace GamePassStorage;

// PGS: the file-oriented save layout newer GDK titles use under <drive>:\XboxGames\GameSave\pgs, alongside (not
// instead of) wgs. Layout, from brodrigz/XgpSaveTools (PgsGameSaveSource.cs, MIT) and Microsoft's XGameSaveFiles docs:
//
//   pgs\u_<xuid, decimal>_<game id, hex>\      one user and title
//     current                                 a link (reparse point) to the active snapshot folder
//     <N>\ContainersRoot\<container path>\<file>   numeric snapshot folders; the save files, with their real names
//     <N>.json                                Gaming Services metadata (undocumented; holds user and device data)
//
// The save files are already the game's own files, so reading needs no index. Writing does not: nothing public says
// how the metadata, snapshots and cloud sync must agree, and a wrong guess can lose a save. This is read-only by design.

/// <summary>One user's PGS save root for one title.</summary>
/// <param name="Path">The <c>u_&lt;xuid&gt;_&lt;gameId&gt;</c> folder.</param>
/// <param name="Xuid">The account id as the folder writes it (decimal).</param>
/// <param name="GameId">The title's PGS game id (hex, as written). Not the package family; see <see cref="PgsSaves.Find"/>.</param>
/// <param name="CurrentSnapshot">The snapshot <c>current</c> points to, or null when it is missing or unreadable.</param>
/// <param name="Snapshots">Every numeric snapshot folder, ascending.</param>
/// <param name="Problem">Why <see cref="CurrentSnapshot"/> is null, when it is.</param>
public sealed record PgsSaveRoot(string Path, string Xuid, string GameId, string? CurrentSnapshot,
    IReadOnlyList<string> Snapshots, string? Problem);

/// <summary>One save file in a snapshot: its path under <c>ContainersRoot</c> ('/' separators), size and time.</summary>
public sealed record PgsSaveFile(string RelativePath, long Size, DateTime LastWriteUtc);

/// <summary>Outcome of listing or copying a PGS snapshot.</summary>
public sealed record PgsResult(WgsOperationStatus Status, string? Snapshot, IReadOnlyList<PgsSaveFile> Files, string? Message)
{
    public bool Succeeded => Status == WgsOperationStatus.Ok;
}

/// <summary>Finds and reads PGS saves. Read-only: there is no write path, by design (see the remarks in the source).</summary>
public static partial class PgsSaves
{
    /// <summary>The folder under <c>ContainersRoot</c>'s parent snapshot that holds the save files.</summary>
    public const string ContainersRoot = "ContainersRoot";

    [GeneratedRegex("^u_(?<xuid>[0-9]+)_(?<game>[0-9A-Fa-f]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex UserRootPattern();

    /// <summary>
    /// Every PGS save root on this machine: <c>&lt;drive&gt;:\XboxGames\GameSave\pgs\u_*_*</c> on each ready fixed drive, or
    /// under <paramref name="pgsFolders"/> when given. Optionally only one game id (case-insensitive).
    /// </summary>
    public static IReadOnlyList<PgsSaveRoot> Find(IEnumerable<string>? pgsFolders = null, string? gameId = null)
    {
        var roots = new List<PgsSaveRoot>();
        foreach (var pgs in pgsFolders ?? DefaultFolders())
        {
            if (!Directory.Exists(pgs)) continue;
            IEnumerable<string> dirs;
            try { dirs = Directory.EnumerateDirectories(pgs).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var dir in dirs.Order(StringComparer.OrdinalIgnoreCase))
            {
                if (TryOpen(dir) is { } root && (gameId is null || root.GameId.Equals(gameId, StringComparison.OrdinalIgnoreCase)))
                {
                    roots.Add(root);
                }
            }
        }
        return roots;
    }

    /// <summary>The default places PGS saves live: <c>XboxGames\GameSave\pgs</c> on every ready fixed drive (Windows only).</summary>
    public static IEnumerable<string> DefaultFolders()
    {
        if (!OperatingSystem.IsWindows()) return [];
        return DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => Path.Combine(d.RootDirectory.FullName, "XboxGames", "GameSave", "pgs"));
    }

    /// <summary>Reads one <c>u_&lt;xuid&gt;_&lt;gameId&gt;</c> folder, or null when the name does not match.</summary>
    public static PgsSaveRoot? TryOpen(string userRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userRoot);
        var full = Path.GetFullPath(userRoot);
        var m = UserRootPattern().Match(Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
        if (!m.Success || !Directory.Exists(full)) return null;
        var snapshots = new List<string>();
        try
        {
            foreach (var d in new DirectoryInfo(full).EnumerateDirectories())
            {
                if (d.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                if (IsSnapshotName(d.Name)) snapshots.Add(d.Name);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new PgsSaveRoot(full, m.Groups["xuid"].Value, m.Groups["game"].Value, null, [], ex.Message);
        }
        snapshots.Sort((a, b) => ulong.Parse(a, CultureInfo.InvariantCulture).CompareTo(ulong.Parse(b, CultureInfo.InvariantCulture)));
        var (current, problem) = ResolveCurrent(full);
        return new PgsSaveRoot(full, m.Groups["xuid"].Value, m.Groups["game"].Value, current, snapshots, problem);
    }

    /// <summary>Lists the save files of a snapshot (<paramref name="snapshot"/>, else the one <c>current</c> points to).</summary>
    public static PgsResult List(PgsSaveRoot root, string? snapshot = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        var chosen = Choose(root, snapshot, out var refusal);
        if (chosen is null) return refusal!;
        try
        {
            return new PgsResult(WgsOperationStatus.Ok, chosen, Scan(SnapshotFiles(root, chosen)), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new PgsResult(WgsOperationStatus.Failed, chosen, [], ex.Message);
        }
    }

    /// <summary>
    /// Copies a snapshot's save files, untouched and with their real names, to <paramref name="destination"/> (empty or
    /// absent). They are already the game's own files, so this is the native save. The file set and every size and
    /// time are compared before and after; if anything changed (the game or a sync was writing) the copy is removed
    /// and the result is <see cref="WgsOperationStatus.ConcurrentChange"/>.
    /// </summary>
    public static PgsResult Extract(PgsSaveRoot root, string destination, string? snapshot = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        var chosen = Choose(root, snapshot, out var refusal);
        if (chosen is null) return refusal!;
        return CopyConsistently(SnapshotFiles(root, chosen), destination, chosen);
    }

    /// <summary>
    /// Copies the whole user root (every snapshot and the metadata files) to <paramref name="destination"/>, with the same
    /// before-and-after check as <see cref="Extract"/>. The metadata holds Xbox user and device data: keep the copy private.
    /// The <c>current</c> link is not copied (it would point back at the live folder); <see cref="PgsResult.Snapshot"/>
    /// records which snapshot it named.
    /// </summary>
    public static PgsResult Backup(PgsSaveRoot root, string destination)
    {
        ArgumentNullException.ThrowIfNull(root);
        return CopyConsistently(root.Path, destination, root.CurrentSnapshot);
    }

    private static string SnapshotFiles(PgsSaveRoot root, string snapshot) => Path.Combine(root.Path, snapshot, ContainersRoot);

    private static bool IsSnapshotName(string name) => name.Length > 0 && name.All(char.IsAsciiDigit) && ulong.TryParse(name, CultureInfo.InvariantCulture, out _);

    private static string? Choose(PgsSaveRoot root, string? snapshot, out PgsResult? refusal)
    {
        refusal = null;
        if (snapshot is not null)
        {
            if (root.Snapshots.Contains(snapshot, StringComparer.Ordinal)) return snapshot;
            refusal = new PgsResult(WgsOperationStatus.Refused, null, [],
                $"'{snapshot}' is not a snapshot of {root.Path}. Snapshots: {string.Join(", ", root.Snapshots)}.");
            return null;
        }
        if (root.CurrentSnapshot is not null) return root.CurrentSnapshot;
        refusal = new PgsResult(WgsOperationStatus.Refused, null, [],
            $"No current snapshot ({root.Problem}). Name one of: {string.Join(", ", root.Snapshots)}.");
        return null;
    }

    private static (string? Snapshot, string? Problem) ResolveCurrent(string root)
    {
        var current = new DirectoryInfo(Path.Combine(root, "current"));
        try
        {
            if (!current.Exists) return (null, "there is no 'current' link");
            if (!current.Attributes.HasFlag(FileAttributes.ReparsePoint)) return (null, "'current' is a plain folder, not a link");
            var target = current.ResolveLinkTarget(returnFinalTarget: true);
            if (target is null || !target.Exists) return (null, "'current' points nowhere");
            var parent = Path.GetDirectoryName(Path.GetFullPath(target.FullName).TrimEnd(Path.DirectorySeparatorChar));
            if (!string.Equals(parent, root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                || !IsSnapshotName(target.Name))
            {
                return (null, $"'current' points outside this save root ({target.FullName})");
            }
            return (target.Name, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, $"'current' could not be read: {ex.Message}");
        }
    }

    /// <summary>Every file under <paramref name="folder"/>, skipping links (which could leave it), as relative '/' paths.</summary>
    private static List<PgsSaveFile> Scan(string folder)
    {
        if (!Directory.Exists(folder)) throw new InvalidDataException($"{folder} does not exist.");
        var files = new List<PgsSaveFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<DirectoryInfo>([new DirectoryInfo(folder)]);
        var rootFull = Path.GetFullPath(folder);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            foreach (var entry in dir.EnumerateFileSystemInfos())
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                if (entry is DirectoryInfo sub) { pending.Push(sub); continue; }
                var rel = Path.GetRelativePath(rootFull, entry.FullName).Replace('\\', '/');
                if (rel.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(rel) || !seen.Add(rel))
                {
                    throw new InvalidDataException($"'{rel}' is not a unique path inside {folder}.");
                }
                var file = (FileInfo)entry;
                files.Add(new PgsSaveFile(rel, file.Length, file.LastWriteTimeUtc));
            }
        }
        files.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        return files;
    }

    private static PgsResult CopyConsistently(string source, string destination, string? snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
        {
            return new PgsResult(WgsOperationStatus.Refused, snapshot, [], $"'{destination}' is not empty.");
        }
        var written = new List<string>();
        try
        {
            var before = Scan(source);
            foreach (var f in before)
            {
                var to = Path.Combine([destination, .. f.RelativePath.Split('/')]);
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(Path.Combine([source, .. f.RelativePath.Split('/')]), to);
                written.Add(to);
            }
            var after = Scan(source);
            if (!before.SequenceEqual(after))
            {
                RemoveCopies(written, destination);
                return new PgsResult(WgsOperationStatus.ConcurrentChange, snapshot, [],
                    "The saves changed while they were being copied. Close the game, wait for it to sync, and retry.");
            }
            return new PgsResult(WgsOperationStatus.Ok, snapshot, before, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            RemoveCopies(written, destination);
            return new PgsResult(ex is IOException io && (io.HResult & 0xFFFF) is 32 or 33 ? WgsOperationStatus.LockConflict : WgsOperationStatus.Failed,
                snapshot, [], ex.Message);
        }
    }

    private static void RemoveCopies(List<string> written, string destination)
    {
        foreach (var f in written)
        {
            try { File.Delete(f); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        try
        {
            foreach (var d in Directory.EnumerateDirectories(destination, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
            {
                if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
