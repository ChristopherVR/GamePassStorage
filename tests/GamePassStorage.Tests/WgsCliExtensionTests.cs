using System.Diagnostics;
using System.Text;
using GamePassStorage.Adapters.AbioticFactor;
using GamePassStorage.Tool;

namespace GamePassStorage.Tests;

/// <summary>The newer <c>wgs</c> commands end to end against real temporary stores.</summary>
public sealed class WgsCliExtensionTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("wgs-cli2-");
    private string P(string name) => Path.Combine(_dir.FullName, name);
    public void Dispose() => _dir.Delete(recursive: true);

    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var code = WgsCli.Run(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    /// <summary>A store for the given family with a world, a settings container and a profile container.</summary>
    private string AbioticStore(string family = AbioticAdapterTests.FullFamily)
    {
        var path = P("abiotic");
        if (Directory.Exists(path)) return path;
        WgsStore.WriteNewContainer(path, "TestWorld-WC", AbioticAdapterTests.FixtureBlob(), family);
        var store = WgsStore.Open(path);
        store.AddOrReplaceContainer("Settings", AbioticContainers.EncodeIni(Encoding.UTF8.GetBytes("[Audio]\nMaster=0.5\n")));
        store.AddOrReplaceContainer("ProfileUnlocks", AbioticAdapterTests.Gvas("/Game/Saves/U.U_C"));
        return path;
    }

    private string SlotStore(string name = "store")
    {
        var path = P(name);
        WgsStore.WriteNewContainer(path, "Slot1", [1, 2, 3, 4], "Test.Game_abc!App");
        return path;
    }

    // ---- adapters / inspect -------------------------------------------------------------

    [Fact]
    public void Adapters_lists_the_built_ins_and_the_generic_fallback_and_can_be_forced_generic()
    {
        var all = Run("adapters");
        Assert.Equal(WgsCli.Ok, all.Code);
        Assert.Contains("abiotic-factor", all.Out, StringComparison.Ordinal);
        Assert.Contains("[built-in]", all.Out, StringComparison.Ordinal);
        Assert.Contains("PlayStack.AbioticFactor_3wcqaesafpzfy", all.Out, StringComparison.Ordinal);
        Assert.Contains("generic", all.Out, StringComparison.Ordinal);

        var generic = Run("adapters", "--no-builtin-adapters");
        Assert.DoesNotContain("abiotic-factor", generic.Out, StringComparison.Ordinal);
        Assert.Contains("[always last]", generic.Out, StringComparison.Ordinal);

        Assert.Contains("\"Id\": \"abiotic-factor\"", Run("adapters", "--json").Out, StringComparison.Ordinal);
    }

    [Fact]
    public void List_and_diagnose_say_which_adapter_matched()
    {
        var store = AbioticStore();
        Assert.Contains("adapter: Abiotic Factor (Game Pass) (abiotic-factor)", Run("list", store).Out, StringComparison.Ordinal);
        Assert.Contains("adapter: Abiotic Factor (Game Pass) (abiotic-factor)", Run("diagnose", store).Out, StringComparison.Ordinal);
        Assert.Contains("(generic)", Run("list", store, "--no-builtin-adapters").Out, StringComparison.Ordinal);

        var other = SlotStore();
        Assert.Contains("(generic)", Run("list", other).Out, StringComparison.Ordinal);
        Assert.Contains("(generic)", Run("diagnose", other).Out, StringComparison.Ordinal);
    }

    [Fact]
    public void Inspect_uses_the_matching_adapter_to_describe_every_container()
    {
        var (code, output, _) = Run("inspect", AbioticStore());

        Assert.Equal(WgsCli.Ok, code);
        Assert.Contains("adapter: Abiotic Factor (Game Pass)", output, StringComparison.Ordinal);
        Assert.Contains("TestWorld-WC  [world bundle]", output, StringComparison.Ordinal);
        Assert.Contains("Profile/Worlds/TestWorld/WorldSave_MetaData", output, StringComparison.Ordinal);
        Assert.Contains("Oodle", output, StringComparison.Ordinal);
        Assert.Contains("Settings  [settings ini]", output, StringComparison.Ordinal);
        Assert.Contains("Master=0.5", output, StringComparison.Ordinal);            // decoded preview
        Assert.Contains("ProfileUnlocks  [profile save (GVAS)]", output, StringComparison.Ordinal);
        Assert.Contains("codec Abiotic Factor payload codec (settings ini only): decodes to", output, StringComparison.Ordinal);
        Assert.Contains("unavailable: World bundles hold an Oodle", output, StringComparison.Ordinal);

        var one = Run("inspect", AbioticStore(), "Settings");
        Assert.DoesNotContain("TestWorld-WC", one.Out, StringComparison.Ordinal);
        Assert.Equal(WgsCli.Failure, Run("inspect", AbioticStore(), "Nope").Code);

        var json = Run("inspect", AbioticStore(), "TestWorld-WC", "--json");
        Assert.Contains("\"Adapter\": \"abiotic-factor\"", json.Out, StringComparison.Ordinal);
        Assert.Contains("\"Kind\": \"world bundle\"", json.Out, StringComparison.Ordinal);
    }

    [Fact]
    public void Inspect_falls_back_to_the_generic_view_when_no_adapter_matches_or_built_ins_are_off()
    {
        var generic = Run("inspect", SlotStore());
        Assert.Equal(WgsCli.Ok, generic.Code);
        Assert.Contains("adapter: Generic (no game-specific adapter) (generic)", generic.Out, StringComparison.Ordinal);
        Assert.Contains("Slot1  [opaque]", generic.Out, StringComparison.Ordinal);
        Assert.Contains("sha256", generic.Out, StringComparison.Ordinal);

        var forced = Run("inspect", AbioticStore(), "TestWorld-WC", "--no-builtin-adapters");
        Assert.Contains("[opaque]", forced.Out, StringComparison.Ordinal);
        Assert.Contains("(generic)", forced.Out, StringComparison.Ordinal);
    }

    [Fact]
    public void A_plugin_folder_adds_adapters_on_top_and_a_bad_folder_only_warns()
    {
        var plugins = P("plugins");
        Directory.CreateDirectory(plugins);
        foreach (var name in new[] { "GamePassStorage.Adapters.AbioticFactor.dll", "GamePassStorage.dll", "GamePassStorage.Adapters.AbioticFactor.deps.json" })
        {
            var from = Path.Combine(AppContext.BaseDirectory, name);
            if (File.Exists(from)) File.Copy(from, Path.Combine(plugins, name));
        }

        // With the built-in off, the plugin alone serves the store.
        var viaPlugin = Run("adapters", "--no-builtin-adapters", "--adapters", plugins);
        Assert.Contains("[plugin]", viaPlugin.Out, StringComparison.Ordinal);
        Assert.Contains("loaded from:", viaPlugin.Out, StringComparison.Ordinal);
        Assert.Contains("world bundle", Run("inspect", AbioticStore(), "TestWorld-WC", "--no-builtin-adapters", "--adapters", plugins).Out, StringComparison.Ordinal);

        // With the built-in on, the duplicate id is a warning and the command still works.
        var both = Run("adapters", "--adapters", plugins);
        Assert.Equal(WgsCli.Ok, both.Code);
        Assert.Contains("already registered", both.Err, StringComparison.Ordinal);

        var missing = Run("adapters", "--adapters", P("nope"));
        Assert.Equal(WgsCli.Ok, missing.Code);
        Assert.Contains("does not exist", missing.Err, StringComparison.Ordinal);
    }

    // ---- find / blobs -------------------------------------------------------------------

    [Fact]
    public void Find_lists_stores_under_the_given_roots_and_says_so_when_there_are_none()
    {
        var store = P("lad/Packages/Test.Game_abc/SystemAppData/wgs/2535_scid");
        WgsStore.WriteNewContainer(store, "Slot1", [1], "Test.Game_abc!App");

        var found = Run("find", "--local-app-data", P("lad"));
        Assert.Equal(WgsCli.Ok, found.Code);
        Assert.Contains("Test.Game_abc  xuid 2535  scid scid", found.Out, StringComparison.Ordinal);
        Assert.Contains(store, found.Out, StringComparison.Ordinal);
        Assert.Contains("\"StorePath\"", Run("find", "--local-app-data", P("lad"), "--json").Out, StringComparison.Ordinal);

        Assert.Contains("Test.Game_abc", Run("find", "--local-app-data", P("lad"), "--package", "game_ab").Out, StringComparison.Ordinal);
        Assert.Contains("no wgs stores found", Run("find", "--local-app-data", P("lad"), "--package", "game_ab", "--exact").Out, StringComparison.Ordinal);
        Assert.Contains("no wgs stores found", Run("find", "--local-app-data", P("empty")).Out, StringComparison.Ordinal);
    }

    [Fact]
    public void Blobs_lists_what_a_container_holds()
    {
        var path = P("multi");
        WgsStore.WriteNewContainer(path, "Slot", new Dictionary<string, byte[]> { ["Meta"] = [1, 2], ["Body"] = new byte[300] }, "Test.Game_abc!App");

        var listed = Run("blobs", path, "Slot");
        Assert.Equal(WgsCli.Ok, listed.Code);
        Assert.Contains("2 blob(s)", listed.Out, StringComparison.Ordinal);
        Assert.Contains("Body", listed.Out, StringComparison.Ordinal);
        Assert.Contains("300 bytes", listed.Out, StringComparison.Ordinal);
        Assert.Contains("\"Name\": \"Meta\"", Run("blobs", path, "Slot", "--json").Out, StringComparison.Ordinal);
        Assert.Equal(WgsCli.Failure, Run("blobs", path, "Nope").Code);
        Assert.Contains("multi-blob", Run("diagnose", path).Out, StringComparison.Ordinal);

        // extract still says multi-blob is not the single-blob layout, and inspect handles it.
        Assert.Equal(WgsCli.Failure, Run("extract", path, "Slot", P("x.bin")).Code);
        Assert.Contains("Slot/Body", Run("inspect", path).Out, StringComparison.Ordinal);
    }

    // ---- delete -------------------------------------------------------------------------

    [Fact]
    public void Delete_needs_a_backup_previews_and_removes_a_never_uploaded_container()
    {
        var path = SlotStore();
        WgsStore.Open(path).AddOrReplaceContainer("Slot2", [9]);

        Assert.Equal(WgsCli.Usage, Run("delete", path, "Slot2").Code);
        var dry = Run("delete", path, "Slot2", "--dry-run");
        Assert.Equal(WgsCli.Ok, dry.Code);
        Assert.Contains("RemoveFromIndex", dry.Out, StringComparison.Ordinal);
        Assert.NotNull(WgsStore.Open(path).Find("Slot2"));

        var backup = P("bk");
        var real = Run("delete", path, "Slot2", "--backup", backup);
        Assert.Equal(WgsCli.Ok, real.Code);
        Assert.Contains("never uploaded", real.Out, StringComparison.Ordinal);
        Assert.Null(WgsStore.Open(path).Find("Slot2"));
        Assert.NotNull(WgsStore.Open(backup).Find("Slot2"));
        Assert.Equal(WgsCli.Failure, Run("delete", path, "Nope", "--backup", P("bk2")).Code);
    }

    [Fact]
    public void Delete_of_a_container_the_cloud_knows_leaves_a_tombstone()
    {
        var path = SlotStore();
        var store = WgsStore.Open(path);
        store.Etag("Slot1", "\"0x1\"");
        store.WriteBlob(store.Find("Slot1")!, [5]);

        var dry = Run("delete", path, "Slot1", "--dry-run");
        Assert.Contains("MarkDeleted", dry.Out, StringComparison.Ordinal);
        var real = Run("delete", path, "Slot1", "--backup", P("bk"));
        Assert.Equal(WgsCli.Ok, real.Code);
        Assert.Contains("keeping its ETag", real.Out, StringComparison.Ordinal);
        var tomb = WgsStore.Open(path).Find("Slot1")!;
        Assert.Equal(WgsEntryState.Deleted, tomb.State);
        Assert.Equal("\"0x1\"", tomb.Etag);
    }

    // ---- restore ------------------------------------------------------------------------

    [Fact]
    public void Restore_puts_a_backup_back_after_a_safety_copy_and_refuses_a_bad_backup()
    {
        var path = SlotStore();
        var backup = P("known-good");
        Assert.Equal(WgsCli.Ok, Run("backup", path, backup).Code);
        var blob = P("new.bin");
        File.WriteAllBytes(blob, [9, 9, 9]);
        Assert.Equal(WgsCli.Ok, Run("put", path, "Slot1", blob, "--backup", P("b1")).Code);

        Assert.Equal(WgsCli.Usage, Run("restore", path, backup).Code);
        var dry = Run("restore", path, backup, "--dry-run");
        Assert.Equal(WgsCli.Ok, dry.Code);
        Assert.Contains("would replace", dry.Out, StringComparison.Ordinal);
        Assert.Equal(new byte[] { 9, 9, 9 }, ReadSlot(path));

        var safety = P("safety");
        var restored = Run("restore", path, backup, "--backup", safety);
        Assert.Equal(WgsCli.Ok, restored.Code);
        Assert.Contains("previous store at", restored.Out, StringComparison.Ordinal);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, ReadSlot(path));
        Assert.Equal(new byte[] { 9, 9, 9 }, ReadSlot(safety));

        var junk = P("junk");
        Directory.CreateDirectory(junk);
        var refused = Run("restore", path, junk, "--backup", P("safety2"));
        Assert.Equal(WgsCli.Failure, refused.Code);
        Assert.Contains("error", refused.Err, StringComparison.Ordinal);
        Assert.False(Directory.Exists(P("safety2")));
        Assert.Equal(WgsCli.Failure, Run("restore", path, junk, "--dry-run").Code);
    }

    // ---- export / import ----------------------------------------------------------------

    [Fact]
    public void Export_writes_blobs_and_a_manifest_and_import_brings_them_back_through_the_gate()
    {
        var source = SlotStore("source");
        WgsStore.WriteNewContainer(P("multi"), "Slot", new Dictionary<string, byte[]> { ["Meta"] = [1, 2], ["Body"] = [3, 4, 5] }, "Test.Game_abc!App");
        var exported = P("out");
        var result = Run("export", source, exported);
        Assert.Equal(WgsCli.Ok, result.Code);
        Assert.Contains("exported 1 container(s), 1 blob(s)", result.Out, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(exported, "wgs-export.json")));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(Path.Combine(exported, "Slot1", "Data")));
        Assert.Equal(WgsCli.Failure, Run("export", source, exported).Code);   // not empty

        var multiOut = P("multi-out");
        Assert.Equal(WgsCli.Ok, Run("export", P("multi"), multiOut).Code);

        var target = SlotStore("target");
        File.WriteAllBytes(Path.Combine(exported, "Slot1", "Data"), [1, 2, 3, 4]);   // unchanged
        // Importing multi-blob content over the single-blob target adds a new container.
        Assert.Equal(WgsCli.Usage, Run("import", target, multiOut).Code);
        var dry = Run("import", target, multiOut, "--dry-run");
        Assert.Equal(WgsCli.Ok, dry.Code);
        Assert.Contains("would add 'Slot'", dry.Out, StringComparison.Ordinal);
        Assert.Null(WgsStore.Open(target).Find("Slot"));

        var backup = P("imp-bk");
        var imported = Run("import", target, multiOut, "--backup", backup);
        Assert.Equal(WgsCli.Ok, imported.Code);
        Assert.Contains("imported 'Slot'", imported.Out, StringComparison.Ordinal);
        var reopened = WgsStore.Open(target);
        Assert.Equal(new byte[] { 3, 4, 5 }, reopened.ReadBlobs(reopened.Find("Slot")!)["Body"]);
        Assert.Null(WgsStore.Open(backup).Find("Slot"));

        // A tampered file stops the import before anything is written.
        File.WriteAllBytes(Path.Combine(multiOut, "Slot", "Body"), [7, 7, 7]);
        var tampered = Run("import", target, multiOut, "--backup", P("imp-bk2"));
        Assert.Equal(WgsCli.Failure, tampered.Code);
        Assert.Contains("SHA-256", tampered.Err, StringComparison.Ordinal);
        Assert.False(Directory.Exists(P("imp-bk2")));
    }

    // ---- process gate -------------------------------------------------------------------

    [Fact]
    public void Refuse_if_running_blocks_writes_while_the_named_process_runs()
    {
        var path = SlotStore();
        var blob = P("new.bin");
        File.WriteAllBytes(blob, [9]);
        var me = Process.GetCurrentProcess().ProcessName;

        var refused = Run("put", path, "Slot1", blob, "--backup", P("bk"), "--refuse-if-running", me);
        Assert.Equal(WgsCli.Failure, refused.Code);
        Assert.Contains("is running", refused.Err, StringComparison.Ordinal);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, ReadSlot(path));

        var allowed = Run("put", path, "Slot1", blob, "--backup", P("bk2"), "--refuse-if-running", "definitely-not-running-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(WgsCli.Ok, allowed.Code);
        Assert.Equal(WgsCli.Usage, Run("put", path, "Slot1", blob, "--dry-run", "--refuse-if-running", ",").Code);
    }

    private static byte[] ReadSlot(string path)
    {
        var store = WgsStore.Open(path);
        return store.ReadBlob(store.Find("Slot1")!);
    }
}
