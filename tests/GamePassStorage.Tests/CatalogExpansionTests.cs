using System.Text;
using GamePassStorage.Adapters.Catalog;
using GamePassStorage.Tool;

namespace GamePassStorage.Tests;

/// <summary>The XgpSaveTools-sourced catalog titles, id Tech checksums and Steam layout, and the derived-blob hook.</summary>
public class CatalogExpansionTests
{
    private const string Root = "/store";
    private const string DoomFamily = "BethesdaSoftworks.ProjectTitan_3275kfvn8vcwc!Game";
    private const string SteamId = "76561198000000001";

    private static WgsStoreOptions Options(MemFs fs, IWgsDerivedBlobs? derived = null)
        => new() { FileSystem = fs, DerivedBlobs = derived, Clock = new FixedClock(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero)) };

    private static WgsStore Store(MemFs fs, string family, IWgsDerivedBlobs? derived, params (string Name, Dictionary<string, byte[]> Blobs)[] containers)
    {
        var plain = Options(fs);
        WgsStore.WriteNewContainer(Root, containers[0].Name, containers[0].Blobs, family, plain);
        var store = WgsStore.Open(Root, plain);
        foreach (var (name, blobs) in containers.Skip(1)) store.CreateContainer(name, blobs);
        return WgsStore.Open(Root, Options(fs, derived));
    }

    private static Dictionary<string, byte[]> One(byte[] data, string name = "Data") => new() { [name] = data };

    private static byte[] SlotFile(uint version, string body)
        => [.. BitConverter.GetBytes(version), 8, 0, 0, 0, .. "SlotFile"u8, .. Encoding.ASCII.GetBytes(body)];

    /// <summary>A DOOM slot as the game writes it: four files and a correct 8-byte checksum beside each .dat.</summary>
    private static Dictionary<string, byte[]> DoomSlot(uint version = 10)
    {
        var dat = SlotFile(version, "progress");
        var backup = SlotFile(version, "older");
        return new()
        {
            ["game.details"] = "checksum=1\nslotId=1\n"u8.ToArray(),
            ["game.details-BACKUP"] = "checksum=0\nslotId=1\n"u8.ToArray(),
            ["game_duration.dat"] = dat,
            ["game_duration.dat-BACKUP"] = backup,
            ["game_duration.dat.checksum"] = IdTechSaves.Sidecar(dat, 8),
            ["game_duration.dat-BACKUP.checksum"] = IdTechSaves.Sidecar(backup, 8),
        };
    }

    private static void ClearFolder(MemFs fs, string folder)
        => fs.Files.Keys.Where(k => k.StartsWith(folder + "/", StringComparison.Ordinal)).ToList().ForEach(k => fs.Files.Remove(k));

    [Fact]
    public void The_catalog_has_unique_ids_and_families_and_every_entry_records_a_source()
    {
        Assert.Equal(76, GameCatalog.Entries.Count);
        Assert.Equal(GameCatalog.Entries.Count, GameCatalog.Entries.Select(e => e.Id).Distinct().Count());
        Assert.All(GameCatalog.Entries, e => Assert.NotEqual(CatalogSource.None, e.Sources));
        Assert.DoesNotContain(GameCatalog.Entries, e => e.PackageFamily == "PlayStack.AbioticFactor_3wcqaesafpzfy");   // its own adapter
    }

    [Fact]
    public void The_id_tech_checksum_is_the_md5_words_xored()
    {
        // MD5("") = d41d8cd98f00b204e9800998ecf8427e: words 0xd98c1dd4, 0x04b2008f, 0x980980e9, 0x7e42f8ec.
        Assert.Equal(0xd98c1dd4u ^ 0x04b2008fu ^ 0x980980e9u ^ 0x7e42f8ecu, IdTechSaves.Checksum([]));
        Assert.Equal([.. BitConverter.GetBytes(IdTechSaves.Checksum("x"u8)), 0, 0, 0, 0], IdTechSaves.Sidecar("x"u8, 8));
    }

    [Fact]
    public void Writing_a_doom_save_recomputes_its_checksum_whatever_the_write_path()
    {
        var fs = new MemFs();
        var store = Store(fs, DoomFamily, IdTechSaves.DoomTheDarkAges, ("GAME-AUTOSAVE1", DoomSlot()));
        var edited = SlotFile(10, "edited progress");

        var commit = store.TryWriteNamedBlob(store.Find("GAME-AUTOSAVE1")!, "game_duration.dat", edited);

        Assert.True(commit.Succeeded, commit.Message);
        var blobs = WgsStore.Open(Root, Options(fs)).ReadBlobs(WgsStore.Open(Root, Options(fs)).Find("GAME-AUTOSAVE1")!);
        Assert.Equal(IdTechSaves.Sidecar(edited, 8), blobs["game_duration.dat.checksum"]);
        Assert.Equal(DoomSlot()["game_duration.dat-BACKUP.checksum"], blobs["game_duration.dat-BACKUP.checksum"]);   // untouched
    }

    [Fact]
    public void An_explicit_checksum_from_the_caller_wins_over_the_derived_one()
    {
        var fs = new MemFs();
        var store = Store(fs, DoomFamily, IdTechSaves.DoomTheDarkAges, ("GAME-AUTOSAVE1", DoomSlot()));

        store.WriteBlobs(store.Find("GAME-AUTOSAVE1")!, new Dictionary<string, byte[]>
        {
            ["game_duration.dat"] = SlotFile(10, "x"),
            ["game_duration.dat.checksum"] = [1, 2, 3, 4, 5, 6, 7, 8],
        });

        var reopened = WgsStore.Open(Root, Options(fs));
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], reopened.ReadBlobs(reopened.Find("GAME-AUTOSAVE1")!)["game_duration.dat.checksum"]);
    }

    [Fact]
    public void The_steam_layout_encrypts_slots_for_one_account_and_wraps_back_with_fresh_checksums()
    {
        var fs = new MemFs();
        var store = Store(fs, DoomFamily, IdTechSaves.DoomTheDarkAges, ("GAME-AUTOSAVE1", DoomSlot()), ("PROFILE", One([5, 5], "profile.bin")));
        var layout = IdTechSaves.DoomTheDarkAges.SteamLayout(SteamId);

        var unwrap = store.TryUnwrapTo("/steam", layout);

        Assert.True(unwrap.Succeeded, unwrap.Message);
        Assert.Equal(IdTechSaves.SlotFiles.Select(f => $"GAME-AUTOSAVE1/{f}").Order(), unwrap.Files.Order());
        var encrypted = fs.Files["/steam/GAME-AUTOSAVE1/game_duration.dat"];
        var plain = IdTechSaves.Decrypt(encrypted, "game_duration.dat", SteamId);
        Assert.Equal(11u, IdTechSaves.SlotFileVersion(plain));   // Steam's version
        Assert.Throws<InvalidDataException>(() => IdTechSaves.Decrypt(encrypted, "game_duration.dat", "76561198000000002"));
        Assert.Throws<InvalidDataException>(() => IdTechSaves.Decrypt(encrypted, "game_duration.dat-BACKUP", SteamId));

        // An edited Steam save for the same account goes back as Game Pass's version, with both checksums recomputed.
        var steamEdit = SlotFile(11, "played on steam");
        fs.Files["/steam/GAME-AUTOSAVE1/game_duration.dat"] = IdTechSaves.Encrypt(steamEdit, "game_duration.dat", SteamId);
        var result = store.TryWrap("/steam", layout);

        Assert.True(result.Succeeded, result.Message);
        var after = WgsStore.Open(Root, Options(fs)).ReadBlobs(WgsStore.Open(Root, Options(fs)).Find("GAME-AUTOSAVE1")!);
        Assert.Equal(10u, IdTechSaves.SlotFileVersion(after["game_duration.dat"]));
        Assert.Equal(IdTechSaves.Sidecar(after["game_duration.dat"], 8), after["game_duration.dat.checksum"]);
        Assert.Equal(DoomSlot()["game.details"], after["game.details"]);
    }

    [Fact]
    public void The_steam_layout_only_targets_existing_slots_and_rejects_a_bad_steam_id()
    {
        var fs = new MemFs();
        var store = Store(fs, DoomFamily, IdTechSaves.DoomTheDarkAges, ("GAME-AUTOSAVE1", DoomSlot()));
        fs.Files["/steam/GAME-AUTOSAVE9/game.details"] = [1];

        var plan = store.PlanWrap("/steam", IdTechSaves.DoomTheDarkAges.SteamLayout(SteamId));

        Assert.Equal(["GAME-AUTOSAVE9/game.details"], plan.Unmapped.ToArray());
        Assert.Throws<ArgumentException>(() => IdTechSaves.DoomTheDarkAges.SteamLayout("123"));
    }

    [Fact]
    public void Wgs_offers_the_doom_steam_layout_by_name_and_applies_checksums_through_the_adapter()
    {
        var dir = Directory.CreateTempSubdirectory("wgs-doom-");
        try
        {
            var store = Path.Combine(dir.FullName, "store");
            WgsStore.WriteNewContainer(store, "GAME-AUTOSAVE1", DoomSlot(), DoomFamily);
            var steam = Path.Combine(dir.FullName, "steam");

            Assert.Equal(WgsCli.Ok, WgsCli.Run(["unwrap", store, steam, "--layout", $"steam:{SteamId}"], TextWriter.Null, TextWriter.Null));
            Assert.Equal(4, Directory.GetFiles(Path.Combine(steam, "GAME-AUTOSAVE1")).Length);
            Assert.Equal(WgsCli.Usage, WgsCli.Run(["unwrap", store, Path.Combine(dir.FullName, "x"), "--layout", "steam:12"], TextWriter.Null, TextWriter.Null));

            var edited = SlotFile(10, "edited");
            var blobFile = Path.Combine(dir.FullName, "edited.dat");
            File.WriteAllBytes(blobFile, edited);
            var backup = Path.Combine(dir.FullName, "backup");
            Assert.Equal(WgsCli.Ok, WgsCli.Run(["put", store, "GAME-AUTOSAVE1", blobFile, "--blob", "game_duration.dat", "--backup", backup], TextWriter.Null, TextWriter.Null));

            var reopened = WgsStore.Open(store);
            var blobs = reopened.ReadBlobs(reopened.Find("GAME-AUTOSAVE1")!);
            Assert.Equal(IdTechSaves.Sidecar(edited, 8), blobs["game_duration.dat.checksum"]);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void Doom_blobs_are_described()
    {
        Assert.Contains("version 10", IdTechSaves.Describe("S/game_duration.dat", SlotFile(10, "x"))!.Summary, StringComparison.Ordinal);
        Assert.Contains("slotId", IdTechSaves.Describe("S/game.details", "checksum=1\nslotId=2\n"u8.ToArray())!.Members[0].Note, StringComparison.Ordinal);
        Assert.Equal("id Tech checksum", IdTechSaves.Describe("S/game_duration.dat.checksum", new byte[8])!.Kind);
        Assert.Null(IdTechSaves.Describe("PROFILE/profile.bin", [1]));
    }

    [Fact]
    public void Fallout_4_joins_its_parts_like_starfield()
    {
        var fs = new MemFs();
        var store = Store(fs, "Test.Game_abc!App", null,
            ("Saves/Save1.fos", new() { ["FO4_SAVEGAME"] = [1], ["P0P"] = [2] }), ("Saves/Save2.fos", new() { ["toc"] = [0], ["ChunkData0"] = [3] }));

        var unwrap = store.TryUnwrapTo("/native", CatalogLayouts.Fallout4);

        Assert.Equal(["Save1.fos", "Save2.fos"], unwrap.Files.Order().ToArray());
        Assert.Equal([1, .. "padding\0padding"u8.ToArray(), 2, .. "padding\0padding"u8.ToArray()], fs.Files["/native/Save1.fos"]);
    }

    public static TheoryData<string> RoundTripLayouts => ["galacticare", "balatro", "silksong", "metaphor", "ninja-gaiden-2", "scorn", "all-blobs-flat", "persona-3-reload", "like-a-dragon", "call-of-duty"];

    [Theory]
    [MemberData(nameof(RoundTripLayouts))]
    public void Each_new_layout_unwraps_to_the_documented_names_and_wraps_back_losslessly(string which)
    {
        var (layout, containers, expected) = which switch
        {
            "galacticare" => (CatalogLayouts.Galacticare, new[] { ("Slot1", new Dictionary<string, byte[]> { ["PlayerData"] = [1], ["Thumb"] = [2] }) }, new[] { "Slot1" }),
            "balatro" => (CatalogLayouts.Balatro, [("common", One([1], "settings.jkr")), ("1", One([2], "save.jkr"))], ["1/save.jkr", "settings.jkr"]),
            "silksong" => (CatalogLayouts.Silksong, [("shared", One([1], "shared.dat")), ("user1save", One([2], "user1.dat")), ("restore3", One([3], "user1.dat")), ("Misc", One([4], "x.dat"))],
                ["Misc/x.dat", "Restore_Points3/user1.dat", "shared.dat", "user1.dat"]),
            "metaphor" => (CatalogLayouts.MetaphorReFantazio, [("SystemData", One([1])), ("SaveData0001", One([2]))], ["save0001.sav", "system.sav"]),
            "ninja-gaiden-2" => (CatalogLayouts.NinjaGaiden2Black, [("SAVESAVE01DAT", One([1])), ("OPTIONOPTION7DAT", One([2]))], ["OPTION/OPTION.sav", "SAVE/SAVE.sav"]),
            "scorn" => (CatalogLayouts.Scorn, [("save1dat", One([1])), ("profile.sav", One([2])), ("plain", One([3]))], ["plain", "profile.sav", "save1.dat"]),
            "all-blobs-flat" => (CatalogLayouts.AllBlobsFlat, [("A", One([1], "a.json")), ("B", One([2], "b.json"))], ["a.json", "b.json"]),
            "persona-3-reload" => (CatalogLayouts.Persona3Reload, [("SAVE0001", One(Enumerable.Range(0, 300).Select(i => (byte)i).ToArray()))], ["SAVE0001.sav"]),
            "like-a-dragon" => (CatalogLayouts.LikeADragon("dds"), [("slot0/datasav", new Dictionary<string, byte[]> { ["data"] = [1], ["icon"] = [2] }), ("slot0/datasys", One([3], "data"))],
                ["slot0/data.sav", "slot0/data.sys", "slot0/slot0_icon.dds"]),
            _ => (GameCatalog.Entries.Single(e => e.Id == "call-of-duty-hq").Layout,
                [("sp24/savegame_1.svg", One([1])), ("7300/cerberus_savegame_progression_1", One([2])), ("keybinds.pc.cod24.kb", One([3]))],
                ["7300/cerberus_savegame_progression_1", "keybinds.pc.cod24.kb", "sp24/savegame_1.svg"]),
        };
        var fs = new MemFs();
        var store = Store(fs, "Test.Game_abc!App", null, containers);

        var unwrap = store.TryUnwrapTo("/native", layout);

        Assert.True(unwrap.Succeeded, unwrap.Message);
        Assert.Equal(expected.Order(), unwrap.Files.Order());
        var before = containers.ToDictionary(c => c.Item1, c => store.ReadBlobs(store.Find(c.Item1)!));
        var plan = store.PlanWrap("/native", layout);
        Assert.True(plan.CanApply, string.Join(" ", plan.Import.Problems));
        Assert.Empty(plan.Unmapped);
        Assert.All(plan.Import.Items, i => Assert.False(i.IsNew));
        Assert.True(store.TryWrap("/native", layout).Succeeded);
        var after = WgsStore.Open(Root, Options(fs));
        foreach (var (name, blobs) in before)
        {
            var now = after.ReadBlobs(after.Find(name)!);
            foreach (var (blob, data) in blobs) Assert.Equal(data, now[blob]);
        }
        if (which == "persona-3-reload") Assert.NotEqual(before["SAVE0001"]["Data"], fs.Files["/native/SAVE0001.sav"]);   // transformed on disk
        ClearFolder(fs, "/native");
    }
}
