using System.Text;
using GamePassStorage.Adapters.AbioticFactor;

namespace GamePassStorage.Tests;

/// <summary>The shipped Abiotic Factor adapter: matching, classification, TOC parsing, ini decoding, orphan naming, gate.</summary>
public class AbioticAdapterTests
{
    public const string FullFamily = "PlayStack.AbioticFactor_3wcqaesafpzfy!AppAbioticFactorShipping";

    internal static string FixtureBlobPath()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "fixtures", "GamePassSaves", "0009000000000001_0000000000000000000000000000ABCD", "B4EB70880D954F7EA9F5B594BA605699");
        return Path.Combine(dir, "051F0A7072A04642908BCC393EE119F7");
    }

    internal static byte[] FixtureBlob() => File.ReadAllBytes(FixtureBlobPath());

    private static WgsContainer Named(string name) => new() { Name = name, Name2 = name };

    private static byte[] FString(string s)
    {
        if (s.All(c => c < 128))
        {
            return [.. BitConverter.GetBytes(s.Length + 1), .. Encoding.ASCII.GetBytes(s), 0];
        }
        return [.. BitConverter.GetBytes(-(s.Length + 1)), .. Encoding.Unicode.GetBytes(s), 0, 0];
    }

    /// <summary>A synthetic bundle: real table of contents, a dummy payload (the body is never read).</summary>
    internal static byte[] Bundle(params (string Path, int Size, string Class, int Flag)[] members)
    {
        var bytes = new List<byte>();
        bytes.AddRange(FString("ABF_SAVE_VERSION"));
        bytes.AddRange(BitConverter.GetBytes(3));
        bytes.AddRange(BitConverter.GetBytes(0));
        bytes.AddRange(BitConverter.GetBytes(16));
        bytes.AddRange(BitConverter.GetBytes(members.Length));
        foreach (var m in members)
        {
            bytes.AddRange(FString(m.Path));
            bytes.AddRange(BitConverter.GetBytes(m.Size));
            bytes.AddRange(FString(m.Class));
            bytes.AddRange(BitConverter.GetBytes(m.Flag));
        }
        bytes.AddRange(BitConverter.GetBytes(1));
        bytes.AddRange(BitConverter.GetBytes(4));
        bytes.AddRange([1, 2, 3, 4]);
        return [.. bytes];
    }

    internal static byte[] Gvas(string saveClass)
    {
        var b = new List<byte>();
        b.AddRange("GVAS"u8.ToArray());
        b.AddRange(BitConverter.GetBytes(3));     // save game version
        b.AddRange(BitConverter.GetBytes(522));   // UE4 package version
        b.AddRange(BitConverter.GetBytes(1012));  // UE5 package version
        b.AddRange(BitConverter.GetBytes((ushort)5));
        b.AddRange(BitConverter.GetBytes((ushort)4));
        b.AddRange(BitConverter.GetBytes((ushort)2));
        b.AddRange(BitConverter.GetBytes(0u));
        b.AddRange(FString("++UE5+Release-5.4"));
        b.AddRange(BitConverter.GetBytes(3));     // custom version format
        b.AddRange(BitConverter.GetBytes(2));     // two custom versions
        b.AddRange(new byte[40]);
        b.AddRange(FString(saveClass));
        b.AddRange([0, 1, 2, 3]);
        return [.. b];
    }

    // ---- matching -----------------------------------------------------------------------

    [Fact]
    public void The_adapter_matches_the_game_by_family_with_or_without_the_app_suffix()
    {
        var adapter = new AbioticFactorAdapter();
        Assert.True(adapter.Matches(FullFamily));
        Assert.True(adapter.Matches("PlayStack.AbioticFactor_3wcqaesafpzfy"));
        Assert.True(adapter.Matches("playstack.abioticfactor_3WCQAESAFPZFY!App"));
        Assert.False(adapter.Matches("PlayStack.AbioticFactor_other"));
        Assert.False(adapter.Matches("Synthetic.AbioticTest_0000000000000!App"));
        Assert.False(adapter.Matches(string.Empty));
        Assert.Equal(["PlayStack.AbioticFactor_3wcqaesafpzfy"], adapter.KnownPackageFamilyNames);
        Assert.Equal("abiotic-factor", adapter.Id);
        Assert.Equal(4, adapter.ContainerNameConventions.Count);
    }

    // ---- classification -----------------------------------------------------------------

    [Theory]
    [InlineData("ForScience-WC", AbfContainerKind.WorldBundle, "ForScience")]
    [InlineData("ForScience-WC-B", AbfContainerKind.WorldBackup, "ForScience")]
    [InlineData("ProfileUnlocks", AbfContainerKind.Profile, null)]
    [InlineData("ProfileScientistCustomization_2", AbfContainerKind.Profile, null)]
    [InlineData("ProfileSpot-WC", AbfContainerKind.WorldBundle, "ProfileSpot")]   // suffix beats the Profile prefix
    [InlineData("Settings", AbfContainerKind.Settings, null)]
    [InlineData("gameusersettings", AbfContainerKind.Settings, null)]
    [InlineData("SomethingElse", AbfContainerKind.Unknown, null)]
    public void Containers_are_classified_by_name(string name, AbfContainerKind kind, string? world)
    {
        Assert.Equal(kind, AbioticContainers.Classify(name));
        Assert.Equal(world, AbioticContainers.WorldNameOf(name));
    }

    // ---- ini ----------------------------------------------------------------------------

    [Fact]
    public void Settings_ini_is_decoded_by_decrementing_every_byte_and_encoded_back_exactly()
    {
        const string ini = "[/Script/Engine.GameUserSettings]\r\nResolutionSizeX=1920\r\nFullscreenMode=1\r\n";
        var plain = Encoding.UTF8.GetBytes(ini);
        var stored = AbioticContainers.EncodeIni(plain);
        Assert.Equal((byte)(plain[0] + 1), stored[0]);
        Assert.Equal(plain, AbioticContainers.DecodeIni(stored));
        Assert.True(AbioticContainers.LooksLikeText(plain));

        var edge = AbioticContainers.DecodeIni([0x00, 0xFF]);       // wraps, never throws
        Assert.Equal(new byte[] { 0xFF, 0xFE }, edge);
        Assert.False(AbioticContainers.LooksLikeText(new byte[] { 1, 2, 3 }));
        Assert.False(AbioticContainers.LooksLikeText([]));
    }

    [Fact]
    public void A_settings_container_is_described_with_its_sections_and_a_decoded_preview()
    {
        var ini = "[Audio]\nMaster=0.5\nMusic=1\n[Video]\n;comment\nRes=1080\n";
        var blob = AbioticContainers.EncodeIni(Encoding.UTF8.GetBytes(ini));
        var d = new AbioticFactorAdapter().Describe(Named("GameUserSettings"), blob);

        Assert.Equal("settings ini", d.Kind);
        Assert.Equal(["[Audio]", "[Video]"], d.Members.Select(m => m.Name).ToArray());
        Assert.Equal("2 setting(s)", d.Members[0].Note);
        Assert.Equal("1 setting(s)", d.Members[1].Note);
        Assert.Equal(ini, d.Preview);

        var codec = new AbioticFactorAdapter().Codec!;
        var decoded = codec.TryDecode(Named("Settings"), blob);
        Assert.True(decoded.Succeeded);
        Assert.Equal(ini, Encoding.UTF8.GetString(decoded.Data!));
        Assert.Equal(blob, codec.TryEncode(Named("Settings"), decoded.Data!).Data);

        // Bytes that do not decode to text are reported, not described as ini.
        var junk = new AbioticFactorAdapter().Describe(Named("Settings"), [200, 201, 202, 203]);
        Assert.Equal("opaque", junk.Kind);
        Assert.False(codec.TryDecode(Named("Settings"), [200, 201, 202, 203]).Succeeded);
    }

    // ---- world bundle table of contents -------------------------------------------------

    [Fact]
    public void The_real_fixture_bundle_table_of_contents_is_read_without_decompressing_the_body()
    {
        var blob = FixtureBlob();
        var toc = AbfBundleToc.TryRead(blob, out var error);

        Assert.NotNull(toc);
        Assert.Null(error);
        Assert.Equal(3, toc!.Version);
        Assert.Equal(3, toc.Members.Count);
        Assert.Equal(1, toc.PayloadMethod);
        Assert.Equal("TestWorld", toc.WorldName);
        Assert.Equal("Profile/Worlds/TestWorld/WorldSave_MetaData", toc.Members[0].Path);
        Assert.Equal(48097, toc.Members[0].Size);
        Assert.Equal("Abiotic_WorldMetadataSave", toc.Members[0].ShortClass);
        Assert.Equal(0, toc.Members[0].Flag);
        Assert.True(toc.CompressedSize > 0 && toc.CompressedSize <= blob.Length);
        Assert.True(toc.UncompressedTotal > toc.CompressedSize);
    }

    [Fact]
    public void A_world_bundle_is_described_and_its_codec_says_the_body_cannot_be_decoded()
    {
        var adapter = new AbioticFactorAdapter();
        var d = adapter.Describe(Named("TestWorld-WC"), FixtureBlob());

        Assert.Equal("world bundle", d.Kind);
        Assert.Contains("'TestWorld'", d.Summary, StringComparison.Ordinal);
        Assert.Equal(3, d.Members.Count);
        Assert.Contains(d.Notes, n => n.Contains("Oodle", StringComparison.Ordinal));

        var backup = adapter.Describe(Named("TestWorld-WC-B"), FixtureBlob());
        Assert.Equal("world backup bundle", backup.Kind);
        Assert.Contains(backup.Notes, n => n.Contains("spare copy", StringComparison.Ordinal));

        var decode = adapter.Codec!.TryDecode(Named("TestWorld-WC"), FixtureBlob());
        Assert.False(decode.Succeeded);
        Assert.Null(decode.Data);
        Assert.Contains("Oodle", decode.Message, StringComparison.Ordinal);
        Assert.False(adapter.Codec.TryEncode(Named("TestWorld-WC"), [1]).Succeeded);
    }

    [Fact]
    public void A_synthetic_bundle_shows_the_ini_member_flag_wide_names_and_a_renamed_world()
    {
        var blob = Bundle(
            ("Profile/Worlds/Caf\u00e9/WorldSave_Facility", 100, "/Game/Saves/A.A_C", 0),
            ("C:/Users/x/AppData/SandboxSettings.ini", 20, "/Game/Saves/S.S_C", 1));
        var d = new AbioticFactorAdapter().Describe(Named("Other-WC"), blob);

        Assert.Equal(2, d.Members.Count);
        Assert.Equal("Profile/Worlds/Caf\u00e9/WorldSave_Facility", d.Members[0].Name);
        Assert.Contains("SandboxSettings.ini", d.Members[1].Note, StringComparison.Ordinal);
        Assert.Contains("'Other'", d.Summary, StringComparison.Ordinal);
        Assert.Contains(d.Notes, n => n.Contains("Caf\u00e9", StringComparison.Ordinal));
        Assert.True(AbfBundleToc.TryRead(blob, out _)!.Members[1].IsIni);
    }

    [Fact]
    public void A_truncated_or_foreign_bundle_never_throws_and_falls_back_to_a_generic_description()
    {
        var blob = FixtureBlob();
        for (var length = 0; length < 700; length += 7)
        {
            var toc = AbfBundleToc.TryRead(blob.AsSpan(0, length), out var error);
            if (toc is null) Assert.NotNull(error);
        }
        var adapter = new AbioticFactorAdapter();
        var cut = adapter.Describe(Named("X-WC"), blob[..200]);
        Assert.Equal("opaque", cut.Kind);
        Assert.Contains(cut.Notes, n => n.Contains("Expected a world bundle", StringComparison.Ordinal));
        Assert.Null(AbfBundleToc.TryRead(Encoding.ASCII.GetBytes("not a bundle at all"), out var why));
        Assert.NotNull(why);

        // A bundle under an unconventional name is still recognised.
        Assert.Equal("world bundle", adapter.Describe(Named("Whatever"), blob).Kind);
        Assert.Equal("opaque", adapter.Describe(Named("Whatever"), [1, 2, 3]).Kind);
    }

    // ---- profile ------------------------------------------------------------------------

    [Fact]
    public void A_profile_container_is_described_from_its_gvas_header()
    {
        var adapter = new AbioticFactorAdapter();
        var d = adapter.Describe(Named("ProfileScientistCustomization_1"), Gvas("/Game/Blueprints/Saves/Cust.Cust_C"));

        Assert.Equal("profile save (GVAS)", d.Kind);
        Assert.Contains("Cust_C", d.Summary, StringComparison.Ordinal);
        Assert.Contains(d.Notes, n => n.Contains("slot", StringComparison.Ordinal));

        var notGvas = adapter.Describe(Named("ProfileUnlocks"), [1, 2, 3, 4, 5]);
        Assert.Contains(notGvas.Notes, n => n.Contains("GVAS magic", StringComparison.Ordinal));
        Assert.Null(AbioticContainers.TryReadGvasClass(Gvas("X.X_C")[..30]));
        Assert.Equal("X.X_C", AbioticContainers.TryReadGvasClass(Gvas("X.X_C")));
        Assert.False(adapter.Codec!.TryDecode(Named("ProfileUnlocks"), [1]).Succeeded);
    }

    // ---- orphans and gate ---------------------------------------------------------------

    [Fact]
    public void An_orphaned_world_folder_is_named_from_its_table_of_contents()
    {
        var inspector = new AbfBlobInspector();
        var fixture = FixtureBlob();
        var found = inspector.Inspect(fixture.AsSpan(0, inspector.HeadBytes));
        Assert.Equal("TestWorld", found!.Label);
        Assert.Equal("TestWorld-WC", found.SuggestedContainerName);
        Assert.Null(inspector.Inspect("GVAS...."u8));
        Assert.Null(inspector.Inspect(new byte[10]));
        Assert.Null(inspector.Inspect(Bundle(("Loose/File", 1, "c", 0))));

        // End to end: an orphan next to a live container is labelled through the adapter's inspector.
        var fs = new MemFs();
        var options = WgsGameAdapterRegistry.CreateOptions(new AbioticFactorAdapter(), new WgsStoreOptions { FileSystem = fs });
        WgsStore.WriteNewContainer("/s", "Live-WC", Bundle(("Profile/Worlds/Live/W", 1, "c", 0)), FullFamily, options);
        var folder = Guid.NewGuid().ToString("N").ToUpperInvariant();
        var blobId = Guid.NewGuid();
        fs.Files[$"/s/{folder}/{blobId.ToString("N").ToUpperInvariant()}"] = fixture;
        var manifest = new byte[168];
        BitConverter.GetBytes(4u).CopyTo(manifest, 0);
        BitConverter.GetBytes(1u).CopyTo(manifest, 4);
        blobId.ToByteArray().CopyTo(manifest, 136);
        blobId.ToByteArray().CopyTo(manifest, 152);
        fs.Files[$"/s/{folder}/container.3"] = manifest;

        var orphan = Assert.Single(WgsStore.FindOrphanedContainers("/s", options));
        Assert.Equal("TestWorld-WC", orphan.SuggestedContainerName);
    }

    [Fact]
    public void The_write_gate_watches_for_the_game_process()
    {
        var adapter = new AbioticFactorAdapter();
        Assert.NotNull(adapter.WriteGate);
        // The wildcard the adapter uses matches the shipped process name.
        var gate = WgsWriteGates.RefuseWhileRunning(new StaticProcesses("AbioticFactor-Win64-Shipping.exe"), "AbioticFactor*");
        var fs = new MemFs();
        WgsStore.WriteNewContainer("/s", "W-WC", [1], FullFamily, new WgsStoreOptions { FileSystem = fs });
        var store = WgsStore.Open("/s", new WgsStoreOptions { FileSystem = fs });
        Assert.False(gate.Assess(store).CanWrite);
        Assert.True(WgsWriteGates.RefuseWhileRunning(new StaticProcesses("AbioticFactorHelper"), "AbioticFactor").Assess(store).CanWrite);
    }

    private sealed class StaticProcesses(params string[] names) : IWgsProcessLister
    {
        public IReadOnlyCollection<string> GetRunningProcessNames() => names;
    }
}
