namespace GamePassStorage;

/// <summary>Where a discovered store lives.</summary>
public enum WgsStoreLocationSource
{
    /// <summary><c>%LOCALAPPDATA%\Packages\&lt;PackageFamilyName&gt;\SystemAppData\wgs\&lt;XUID&gt;_&lt;SCID&gt;</c>.</summary>
    LocalAppData,
    /// <summary><c>&lt;drive&gt;:\XboxGames\GameSave\wgs\...</c>.</summary>
    XboxGamesDrive,
}

/// <summary>A wgs store found on this machine.</summary>
/// <param name="PackageFamilyName">The owning package family. For <see cref="WgsStoreLocationSource.LocalAppData"/>
/// this is the package folder name; for other sources it is taken from the index (the part before the
/// <c>!</c>), or empty when the index cannot be read.</param>
/// <param name="Xuid">The Xbox user id half of the store folder name (before the first underscore).</param>
/// <param name="Scid">The service configuration id half (after the first underscore).</param>
/// <param name="StorePath">The folder holding <c>containers.index</c>.</param>
public sealed record WgsStoreLocation(string PackageFamilyName, string Xuid, string Scid, string StorePath,
    WgsStoreLocationSource Source);

/// <summary>A package's <c>SystemAppData\wgs</c> folder and the stores in it. A folder with no stores is normal for a
/// title that keeps its saves only in the cloud, or one installed but never played on this account.</summary>
public sealed record WgsSaveFolder(string PackageFamilyName, string Path, IReadOnlyList<WgsStoreLocation> Stores);

/// <summary>Where to look. Every member has a real default; inject them to test on any OS.</summary>
public sealed class WgsStoreDiscoveryOptions
{
    public IWgsFileSystem FileSystem { get; init; } = PhysicalWgsFileSystem.Instance;

    /// <summary>The <c>%LOCALAPPDATA%</c> folder. Null means the current user's; empty skips the location.</summary>
    public string? LocalAppData { get; init; }

    /// <summary>Drive roots to probe for <c>XboxGames\GameSave\wgs</c>. Null means the fixed and removable
    /// drives on Windows and none elsewhere.</summary>
    public IReadOnlyList<string>? DriveRoots { get; init; }
}

/// <summary>
/// Finds wgs stores on the machine without knowing the title. Layouts:
/// <list type="bullet">
/// <item><c>%LOCALAPPDATA%\Packages\&lt;PackageFamilyName&gt;\SystemAppData\wgs\&lt;XUID&gt;_&lt;SCID&gt;</c>, the
/// path in docs/wgs-format.md and the one XGP-save-extractor and libNOM.io read.</item>
/// <item><c>&lt;drive&gt;:\XboxGames\GameSave\wgs\...</c>. Assumption: the structure below <c>wgs</c> there is
/// not documented in the sources consulted, so a child folder that holds a <c>containers.index</c> is
/// taken as a store, and a child folder that holds such stores is taken as a package folder.</item>
/// </list>
/// A store folder name is split at its first underscore into XUID and SCID. Roots that do not exist
/// simply yield nothing; nothing here throws for an unreadable folder.
/// </summary>
public static class WgsStoreDiscovery
{
    /// <summary>
    /// Finds stores. <paramref name="packageFamilyName"/> filters by package family name: a
    /// case-insensitive substring, or the whole name when <paramref name="exact"/> is true.
    /// </summary>
    public static IReadOnlyList<WgsStoreLocation> Find(string? packageFamilyName = null, bool exact = false,
        WgsStoreDiscoveryOptions? options = null)
    {
        var opts = options ?? new WgsStoreDiscoveryOptions();
        var fs = opts.FileSystem;
        var found = new List<WgsStoreLocation>();

        var localAppData = opts.LocalAppData
            ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData))
        {
            foreach (var package in Children(fs, Path.Combine(localAppData, "Packages")))
            {
                var wgs = Path.Combine(package, "SystemAppData", "wgs");
                AddStores(fs, wgs, Path.GetFileName(package), WgsStoreLocationSource.LocalAppData, found);
            }
        }

        var drives = opts.DriveRoots ?? DefaultDriveRoots();
        foreach (var drive in drives)
        {
            var wgs = Path.Combine(drive, "XboxGames", "GameSave", "wgs");
            foreach (var child in Children(fs, wgs))
            {
                if (WgsStore.IsContainerFolder(child, fs))
                {
                    AddStore(fs, child, null, WgsStoreLocationSource.XboxGamesDrive, found);
                }
                else
                {
                    AddStores(fs, child, Path.GetFileName(child), WgsStoreLocationSource.XboxGamesDrive, found);
                }
            }
        }

        return found
            .Where(l => Matches(l.PackageFamilyName, packageFamilyName, exact))
            .OrderBy(l => l.PackageFamilyName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(l => l.StorePath, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Every package that has a save folder, with its stores, including folders that hold none. Covers
    /// <c>%LOCALAPPDATA%\Packages\*\SystemAppData\wgs</c>; stores under <c>XboxGames\GameSave\wgs</c> are grouped by the
    /// package family their index records.
    /// </summary>
    public static IReadOnlyList<WgsSaveFolder> FindSaveFolders(WgsStoreDiscoveryOptions? options = null)
    {
        var opts = options ?? new WgsStoreDiscoveryOptions();
        var stores = Find(null, false, opts);
        var folders = new Dictionary<string, (string Path, List<WgsStoreLocation> Stores)>(StringComparer.OrdinalIgnoreCase);
        var localAppData = opts.LocalAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData))
        {
            foreach (var package in Children(opts.FileSystem, Path.Combine(localAppData, "Packages")))
            {
                var wgs = Path.Combine(package, "SystemAppData", "wgs");
                if (opts.FileSystem.DirectoryExists(wgs)) folders[Path.GetFileName(package)] = (wgs, []);
            }
        }
        foreach (var s in stores)
        {
            if (!folders.TryGetValue(s.PackageFamilyName, out var f))
            {
                folders[s.PackageFamilyName] = f = (Path.GetDirectoryName(s.StorePath) ?? s.StorePath, []);
            }
            f.Stores.Add(s);
        }
        return folders.Select(kv => new WgsSaveFolder(kv.Key, kv.Value.Path, kv.Value.Stores))
            .OrderBy(f => f.PackageFamilyName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool Matches(string family, string? filter, bool exact)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        var f = filter.Trim();
        return exact
            ? string.Equals(family, f, StringComparison.OrdinalIgnoreCase)
            : family.Contains(f, StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> DefaultDriveRoots()
    {
        if (!OperatingSystem.IsWindows()) return [];
        try
        {
            return DriveInfo.GetDrives()
                .Where(d => d.DriveType is DriveType.Fixed or DriveType.Removable)
                .Select(d => d.RootDirectory.FullName).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static List<string> Children(IWgsFileSystem fs, string directory)
    {
        try
        {
            return fs.DirectoryExists(directory) ? fs.EnumerateDirectories(directory).OrderBy(x => x, StringComparer.Ordinal).ToList() : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void AddStores(IWgsFileSystem fs, string wgs, string? family, WgsStoreLocationSource source,
        List<WgsStoreLocation> into)
    {
        foreach (var store in Children(fs, wgs))
        {
            if (WgsStore.IsContainerFolder(store, fs)) AddStore(fs, store, family, source, into);
        }
    }

    private static void AddStore(IWgsFileSystem fs, string path, string? family, WgsStoreLocationSource source,
        List<WgsStoreLocation> into)
    {
        var name = Path.GetFileName(path);
        var underscore = name.IndexOf('_', StringComparison.Ordinal);
        var xuid = underscore < 0 ? name : name[..underscore];
        var scid = underscore < 0 ? string.Empty : name[(underscore + 1)..];
        into.Add(new WgsStoreLocation(family ?? FamilyFromIndex(fs, path), xuid, scid, path, source));
    }

    private static string FamilyFromIndex(IWgsFileSystem fs, string path)
    {
        var opened = WgsStore.TryOpen(path, new WgsStoreOptions { FileSystem = fs });
        if (!opened.Succeeded) return string.Empty;
        var id = opened.Store!.PackageFamilyName;
        var bang = id.IndexOf('!', StringComparison.Ordinal);
        return bang < 0 ? id : id[..bang];
    }
}
