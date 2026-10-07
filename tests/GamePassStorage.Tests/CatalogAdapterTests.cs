using System.IO.Compression;
using System.Text;
using GamePassStorage.Adapters.Catalog;
using GamePassStorage.Tool;

namespace GamePassStorage.Tests;

/// <summary>The catalog of community-sourced native layouts: integrity, and every game-specific mapping in both directions.
/// These are synthetic stores shaped like the source tool describes; they are not evidence that a game loads the result.</summary>
public class CatalogAdapterTests
{
    private const string Root = "/store";

    private static WgsStoreOptions Options(MemFs fs)
        => new() { FileSystem = fs, Clock = new FixedClock(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero)) };

    private static byte[] Bytes(int length, int seed)
    {
        var b = new byte[length];
        new Random(seed).NextBytes(b);
        return b;
    }

    /// <summary>A store whose containers are given as name to (blob name to bytes), in index order.</summary>
    private static WgsStore Store(MemFs fs, params (string Name, Dictionary<string, byte[]> Blobs)[] containers)
    {
        var options = Options(fs);
        WgsStore.WriteNewContainer(Root, containers[0].Name, containers[0].Blobs, "Test.Game_abc!App", options);
        var store = WgsStore.Open(Root, options);
        foreach (var (name, blobs) in containers.Skip(1)) store.CreateContainer(name, blobs);
        return WgsStore.Open(Root, options);
    }

    private static Dictionary<string, byte[]> One(byte[] data, string name = "Data") => new() { [name] = data };

    /// <summary>Unwraps with the layout, wraps the result into an empty copy of the same container names and compares every blob.</summary>
    private static void AssertRoundTrip(MemFs fs, WgsStore store, IWgsNativeLayout layout)
    {
        Assert.True(store.TryUnwrapTo("/native", layout).Succeeded);
        var reopened = WgsStore.Open(Root, Options(fs));
        var before = reopened.Containers.ToDictionary(c => c.Name, c => reopened.ReadBlobs(c));
        var result = reopened.TryWrap("/native", layout);
        Assert.True(result.Succeeded, result.Message);
        var after = WgsStore.Open(Root, Options(fs));
        foreach (var (name, blobs) in before)
        {
            var now = after.ReadBlobs(after.Find(name)!);
            Assert.Equal(blobs.Keys.Order(), now.Keys.Order());
            foreach (var (blob, data) in blobs) Assert.Equal(data, now[blob]);
        }
    }

    [Fact]
    public void Catalog_ids_and_families_are_unique_and_every_family_resolves_to_its_entry()
    {
        var registry = new WgsGameAdapterRegistry();
        foreach (var a in GameCatalog.CreateAdapters()) registry.Register(a);

        Assert.Equal(GameCatalog.Entries.Count, GameCatalog.Entries.Select(e => e.PackageFamily.ToUpperInvariant()).Distinct().Count());
        foreach (var e in GameCatalog.Entries)
        {
            Assert.Matches("^[^!_]+_[a-z0-9]{13}$", e.PackageFamily);
            Assert.Equal(e.Id, registry.Resolve(e.PackageFamily + "!App").Id);
            Assert.Same(e.Layout, registry.Resolve(e.PackageFamily).NativeLayout);
        }
    }

    [Fact]
    public void The_tool_builds_in_the_catalog_and_abiotic_factor_keeps_its_own_adapter()
    {
        using var stdout = new StringWriter();
        Assert.Equal(WgsCli.Ok, WgsCli.Run(["adapters"], stdout, TextWriter.Null));
        Assert.Contains("palworld  Palworld (Game Pass, catalog)  [built-in]", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("native layout: starfield (unwrap only)", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("abiotic-factor", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Palworld_turns_dashes_into_folders_and_back()
    {
        var fs = new MemFs();
        var store = Store(fs, ("Level", One(Bytes(40, 1))), ("Players-0123ABCD", One(Bytes(30, 2))), ("LevelMeta", One(Bytes(5, 3))));

        var unwrap = store.TryUnwrapTo("/native", CatalogLayouts.Palworld);

        Assert.Equal(["Level.sav", "LevelMeta.sav", "Players/0123ABCD.sav"], unwrap.Files.Order().ToArray());
        fs.Files.Keys.Where(k => k.StartsWith("/native/", StringComparison.Ordinal)).ToList().ForEach(k => fs.Files.Remove(k));
        AssertRoundTrip(fs, store, CatalogLayouts.Palworld);
    }

    [Fact]
    public void Forza_joins_container_and_blob_with_a_dot_and_splits_on_the_longest_known_container()
    {
        var fs = new MemFs();
        var store = Store(fs, ("User.Profile", One(Bytes(20, 1), "Header")), ("User", One(Bytes(10, 2), "Profile.Data")));

        var unwrap = store.TryUnwrapTo("/native", CatalogLayouts.Forza);

        Assert.Contains("User.Profile.Header", unwrap.Files);
        Assert.Contains("User.Profile.Data", unwrap.Files);
        fs.Files.Keys.Where(k => k.StartsWith("/native/", StringComparison.Ordinal)).ToList().ForEach(k => fs.Files.Remove(k));
        AssertRoundTrip(fs, store, CatalogLayouts.Forza);
    }

    [Fact]
    public void Lies_of_p_drops_the_numeric_prefix_and_wraps_only_onto_an_existing_save()
    {
        var fs = new MemFs();
        var store = Store(fs, ("123456SaveSlot0", One(Bytes(20, 1))));
        fs.Files["/edit/SaveSlot0.sav"] = Bytes(25, 9);
        fs.Files["/edit/NewSlot.sav"] = Bytes(25, 8);

        Assert.Equal(["SaveSlot0.sav"], store.TryUnwrapTo("/native", CatalogLayouts.LiesOfP).Files.ToArray());
        var plan = store.PlanWrap("/edit", CatalogLayouts.LiesOfP);

        Assert.Equal(["NewSlot.sav"], plan.Unmapped.ToArray());
        Assert.Equal("123456SaveSlot0", Assert.Single(plan.Import.Items).ContainerName);
    }

    [Fact]
    public void Coral_island_puts_backups_in_a_folder()
    {
        var fs = new MemFs();
        var store = Store(fs, ("Slot1", One(Bytes(20, 1))), ("BackupSlot1", One(Bytes(20, 2))));

        Assert.Equal(["Backup/Slot1.sav", "Slot1.sav"], store.TryUnwrapTo("/native", CatalogLayouts.BackupFolder).Files.Order().ToArray());
        fs.Files.Keys.Where(k => k.StartsWith("/native/", StringComparison.Ordinal)).ToList().ForEach(k => fs.Files.Remove(k));
        AssertRoundTrip(fs, store, CatalogLayouts.BackupFolder);
    }

    [Fact]
    public void Arcade_paradise_and_railway_empire_2_map_fixed_names()
    {
        var fs = new MemFs();
        var store = Store(fs, ("Anything", One(Bytes(20, 1))));
        Assert.Equal(["RATSaveData.dat"], store.TryUnwrapTo("/arcade", CatalogLayouts.ArcadeParadise).Files.ToArray());
        Assert.True(store.TryWrap("/arcade", CatalogLayouts.ArcadeParadise).Succeeded);

        var fs2 = new MemFs();
        var rail = Store(fs2, ("Campaign 1", new() { ["savegame"] = Bytes(50, 1), ["description"] = Bytes(9, 2) }));
        Assert.Equal(["Campaign 1"], rail.TryUnwrapTo("/native", CatalogLayouts.RailwayEmpire2).Files.ToArray());
        fs2.Files["/native/Campaign 1"] = Bytes(60, 3);
        Assert.True(rail.TryWrap("/native", CatalogLayouts.RailwayEmpire2).Succeeded);
        var after = WgsStore.Open(Root, Options(fs2)).ReadBlobs(WgsStore.Open(Root, Options(fs2)).Find("Campaign 1")!);
        Assert.Equal(Bytes(60, 3), after["savegame"]);
        Assert.Equal(Bytes(9, 2), after["description"]);   // kept
    }

    [Fact]
    public void Cricket_24_renames_chunk_zero_and_reports_other_chunks()
    {
        var fs = new MemFs();
        var store = Store(fs, ("Career", new() { ["PROFILE.CHUNK0"] = Bytes(20, 1), ["PROFILE.CHUNK1"] = Bytes(20, 2) }));

        var unwrap = store.TryUnwrapTo("/native", CatalogLayouts.Cricket24);

        Assert.Equal(["Career/PROFILE.SAV"], unwrap.Files.ToArray());
        Assert.Contains(unwrap.Skipped, s => s.Contains("CHUNK1", StringComparison.Ordinal));
        Assert.Equal(new WgsNativeTarget("Career", "PROFILE.CHUNK0"), CatalogLayouts.Cricket24.FromNativePath(WgsNativeContext.Empty, "Career/PROFILE.SAV"));
    }

    [Fact]
    public void Control_adds_the_display_name_file_and_ignores_it_on_wrap()
    {
        var fs = new MemFs();
        var store = Store(fs, ("save0", new() { ["meta"] = Bytes(8, 1), ["data"] = Bytes(80, 2) }));

        var unwrap = store.TryUnwrapTo("/native", CatalogLayouts.Control);

        Assert.Equal("save0", Encoding.UTF8.GetString(fs.Files["/native/save0/--containerDisplayName.chunk"]));
        Assert.Equal(Bytes(80, 2), fs.Files["/native/save0/data.chunk"]);
        Assert.Equal(["save0/--containerDisplayName.chunk"], store.PlanWrap("/native", CatalogLayouts.Control).Unmapped.ToArray());
        fs.Files.Keys.Where(k => k.StartsWith("/native/", StringComparison.Ordinal)).ToList().ForEach(k => fs.Files.Remove(k));
        AssertRoundTrip(fs, store, CatalogLayouts.Control);
    }

    [Fact]
    public void Starfield_joins_parts_with_padding_in_both_formats_and_refuses_to_wrap()
    {
        var fs = new MemFs();
        var store = Store(fs,
            ("Saves/Old.sfs", new() { ["P1P"] = [3, 3], ["BETHESDAPFH"] = [1, 1, 1], ["P0P"] = Enumerable.Repeat((byte)2, 16).ToArray() }),
            ("Saves/New.sfs", new() { ["toc"] = [9], ["BlobData1"] = [5], ["BlobData0"] = [4] }),
            ("Settings/Prefs", One([7])));

        var unwrap = store.TryUnwrapTo("/native", CatalogLayouts.Starfield);

        Assert.Equal(["New.sfs", "Old.sfs"], unwrap.Files.Order().ToArray());
        var pad = "padding\0padding\0"u8.ToArray();
        Assert.Equal([1, 1, 1, .. pad[..13], .. Enumerable.Repeat((byte)2, 16), 3, 3, .. pad[..14]], fs.Files["/native/Old.sfs"]);
        Assert.Equal([4, .. pad[..15], 5, .. pad[..15]], fs.Files["/native/New.sfs"]);
        Assert.False(store.PlanWrap("/native", CatalogLayouts.Starfield).CanApply);
    }

    [Fact]
    public void One_lonely_outpost_unpacks_the_gzipped_json()
    {
        var json = """{"files":{"$values":[{"name":"ConsoleSaves/Farm/save.json","datas":{"$values":["{\"day\":3}"]}}]}}""";
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true)) gz.Write(Encoding.UTF8.GetBytes(json));
        var fs = new MemFs();
        var store = Store(fs, ("Save", One(ms.ToArray())));

        var unwrap = store.TryUnwrapTo("/native", CatalogLayouts.OneLonelyOutpost);

        Assert.True(unwrap.Succeeded, unwrap.Message);
        Assert.Equal("""{"day":3}""", Encoding.UTF8.GetString(fs.Files["/native/Farm/save.json"]));
    }

    [Fact]
    public void A_layout_that_cannot_read_a_container_skips_it_instead_of_failing()
    {
        var fs = new MemFs();
        var store = Store(fs, ("Save", One([1, 2, 3])));   // not gzip

        var unwrap = store.TryUnwrapTo("/native", CatalogLayouts.OneLonelyOutpost);

        Assert.True(unwrap.Succeeded);
        Assert.Empty(unwrap.Files);
        Assert.Contains(unwrap.Skipped, s => s.Contains("could not read", StringComparison.Ordinal));
    }

    [Fact]
    public void State_of_decay_2_keeps_the_last_name_segment_and_is_unwrap_only()
    {
        var fs = new MemFs();
        var store = Store(fs, ("Main", new() { ["Saves/Community/Slot1"] = Bytes(10, 1) }), ("Other", One([1])));

        Assert.Equal(["Slot1.sav"], store.TryUnwrapTo("/native", CatalogLayouts.StateOfDecay2).Files.ToArray());
        Assert.False(CatalogLayouts.StateOfDecay2.CanWrap);
    }

    [Fact]
    public void Wgs_unwrap_uses_the_catalog_layout_for_a_known_family_and_container_folders_otherwise()
    {
        var dir = Directory.CreateTempSubdirectory("wgs-catalog-");
        try
        {
            var pal = Path.Combine(dir.FullName, "pal");
            WgsStore.WriteNewContainer(pal, "Players-AB", [1, 2], "PocketpairInc.Palworld_ad4psfrxyesvt!AppPalShipping");
            var other = Path.Combine(dir.FullName, "other");
            WgsStore.WriteNewContainer(other, "Slot1", [3], "Some.Game_abc!App");

            using var o1 = new StringWriter();
            Assert.Equal(WgsCli.Ok, WgsCli.Run(["unwrap", pal, Path.Combine(dir.FullName, "out1")], o1, TextWriter.Null));
            Assert.True(File.Exists(Path.Combine(dir.FullName, "out1", "Players", "AB.sav")));
            Assert.Contains("layout palworld", o1.ToString(), StringComparison.Ordinal);

            using var o2 = new StringWriter();
            Assert.Equal(WgsCli.Ok, WgsCli.Run(["unwrap", other, Path.Combine(dir.FullName, "out2")], o2, TextWriter.Null));
            Assert.True(File.Exists(Path.Combine(dir.FullName, "out2", "Slot1", "Data")));

            using var o3 = new StringWriter();
            Assert.Equal(WgsCli.Ok, WgsCli.Run(["wrap", pal, Path.Combine(dir.FullName, "out1"), "--dry-run"], o3, TextWriter.Null));
            Assert.Contains("would replace 'Players-AB'", o3.ToString(), StringComparison.Ordinal);
        }
        finally { dir.Delete(recursive: true); }
    }
}
