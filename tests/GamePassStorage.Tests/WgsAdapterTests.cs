using GamePassStorage.Adapters.AbioticFactor;

namespace GamePassStorage.Tests;

/// <summary>The adapter registry, the generic fallback, and plugin loading.</summary>
public sealed class WgsAdapterTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("wgs-adapters-");
    public void Dispose() => _dir.Delete(recursive: true);

    private sealed class FakeAdapter(string id, Func<string, bool> matches, string[]? known = null,
        IWgsWriteGate? gate = null, IWgsBlobInspector? inspector = null) : IWgsGameAdapter
    {
        public string Id => id;
        public string DisplayName => id;
        public IReadOnlyList<string> KnownPackageFamilyNames => known ?? [];
        public bool Matches(string packageFamilyName) => matches(packageFamilyName);
        public IWgsWriteGate? WriteGate => gate;
        public IWgsBlobInspector? BlobInspector => inspector;
        public WgsContentDescription Describe(WgsContainer container, byte[] blob) => WgsContentDescription.Generic(container.Name, blob);
    }

    private sealed class DenyAll : IWgsWriteGate
    {
        public WgsWriteAssessment Assess(WgsStore store) => new([new WgsWriteConcern("deny", true, "adapter says no")]);
    }

    private sealed class NoInspector : IWgsBlobInspector
    {
        public int HeadBytes => 1;
        public WgsBlobDescription? Inspect(ReadOnlySpan<byte> head) => null;
    }

    // ---- resolution ---------------------------------------------------------------------

    [Fact]
    public void Resolution_prefers_an_exact_family_over_a_substring_match_whatever_the_registration_order()
    {
        var substring = new FakeAdapter("loose", f => f.Contains("Abiotic", StringComparison.OrdinalIgnoreCase));
        var exact = new FakeAdapter("exact", f => f.StartsWith("PlayStack.AbioticFactor", StringComparison.Ordinal),
            ["PlayStack.AbioticFactor_3wcqaesafpzfy"]);
        var family = "PlayStack.AbioticFactor_3wcqaesafpzfy!AppAbioticFactorShipping";

        var a = new WgsGameAdapterRegistry();
        a.Register(substring);
        a.Register(exact);
        Assert.Equal("exact", a.Resolve(family).Id);

        var b = new WgsGameAdapterRegistry();
        b.Register(exact);
        b.Register(substring);
        Assert.Equal("exact", b.Resolve(family).Id);

        // Only the substring adapter matches a different Abiotic package.
        Assert.Equal("loose", a.Resolve("Fork.AbioticFactor_zzz!App").Id);
    }

    [Fact]
    public void Equally_specific_adapters_resolve_to_the_first_registered()
    {
        var registry = new WgsGameAdapterRegistry();
        registry.Register(new FakeAdapter("first", _ => true));
        registry.Register(new FakeAdapter("second", _ => true));
        Assert.Equal("first", registry.Resolve("Any.Title_1!App").Id);
    }

    [Fact]
    public void The_generic_adapter_is_always_last_and_never_null()
    {
        var registry = new WgsGameAdapterRegistry();
        Assert.Same(GenericWgsAdapter.Instance, registry.Resolve("Nothing.Registered_1!App"));
        registry.Register(new FakeAdapter("only-x", f => f.StartsWith('X')));
        Assert.Equal("generic", registry.Resolve("Other_1").Id);
        Assert.Equal("generic", registry.Resolve(string.Empty).Id);
        Assert.Null(registry.ResolveSpecific("Other_1"));
        Assert.Equal("only-x", registry.ResolveSpecific("X_1")!.Id);
        Assert.True(GenericWgsAdapter.Instance.Matches("anything"));
        Assert.Single(registry.Adapters);
    }

    [Fact]
    public void Registration_rejects_duplicates_the_reserved_generic_id_and_ignores_a_faulty_matcher()
    {
        var registry = new WgsGameAdapterRegistry();
        registry.Register(new FakeAdapter("a", _ => false));
        Assert.Throws<ArgumentException>(() => registry.Register(new FakeAdapter("A", _ => false)));
        Assert.Throws<ArgumentException>(() => registry.Register(new FakeAdapter("generic", _ => true)));
        Assert.Throws<ArgumentException>(() => registry.Register(new FakeAdapter(" ", _ => true)));

        registry.Register(new FakeAdapter("boom", _ => throw new InvalidOperationException("plugin bug")));
        registry.Register(new FakeAdapter("fine", _ => true));
        Assert.Equal("fine", registry.Resolve("X_1").Id);
        Assert.True(registry.Unregister("FINE"));
        Assert.False(registry.Unregister("fine"));
    }

    [Fact]
    public void The_family_helper_strips_the_app_suffix()
    {
        Assert.Equal("Pub.Game_abc", WgsGameAdapterRegistry.FamilyOf("Pub.Game_abc!AppGameShipping"));
        Assert.Equal("Pub.Game_abc", WgsGameAdapterRegistry.FamilyOf("Pub.Game_abc"));
        Assert.Equal(string.Empty, WgsGameAdapterRegistry.FamilyOf(string.Empty));
    }

    // ---- options and opening ------------------------------------------------------------

    [Fact]
    public void Options_carry_the_adapters_inspector_and_add_its_gate_to_the_structural_one()
    {
        var inspector = new NoInspector();
        var adapter = new FakeAdapter("g", _ => true, gate: new DenyAll(), inspector: inspector);
        var fs = new MemFs();
        var options = WgsGameAdapterRegistry.CreateOptions(adapter, new WgsStoreOptions { FileSystem = fs });

        Assert.Same(inspector, options.BlobInspector);
        Assert.Same(fs, options.FileSystem);
        WgsStore.WriteNewContainer("/s", "A", [1], "T_1!A", options);
        var store = WgsStore.Open("/s", options);
        Assert.Equal(WgsOperationStatus.Refused, store.TryWriteBlob(store.Containers[0], [2]).Status);
        Assert.Contains("adapter says no", store.AssessWrite().BlockingMessage(), StringComparison.Ordinal);

        // No adapter, or the generic one, leaves the options alone.
        var baseline = new WgsStoreOptions { FileSystem = fs };
        Assert.Same(baseline, WgsGameAdapterRegistry.CreateOptions((IWgsGameAdapter?)null, baseline));
        var generic = WgsGameAdapterRegistry.CreateOptions(GenericWgsAdapter.Instance, baseline);
        Assert.Null(generic.WriteGate);
        Assert.Null(generic.BlobInspector);
    }

    [Fact]
    public void Opening_through_the_registry_applies_the_matching_adapters_gate_and_reports_generic_otherwise()
    {
        var fs = new MemFs();
        var plain = new WgsStoreOptions { FileSystem = fs };
        WgsStore.WriteNewContainer("/mine", "A", [1], "Mine.Game_1!App", plain);
        WgsStore.WriteNewContainer("/other", "A", [1], "Other.Game_1!App", plain);
        var registry = new WgsGameAdapterRegistry();
        registry.Register(new FakeAdapter("mine", f => f.StartsWith("Mine.", StringComparison.Ordinal), ["Mine.Game_1"], new DenyAll()));

        var mine = registry.TryOpen("/mine", plain);
        Assert.Equal("mine", mine.Adapter!.Id);
        Assert.False(mine.Open.Store!.AssessWrite().CanWrite);

        var other = registry.TryOpen("/other", plain);
        Assert.Equal("generic", other.Adapter!.Id);
        Assert.True(other.Open.Store!.AssessWrite().CanWrite);

        var missing = registry.TryOpen("/nowhere", plain);
        Assert.False(missing.Open.Succeeded);
        Assert.Null(missing.Adapter);
    }

    // ---- generic sniffing ---------------------------------------------------------------

    [Theory]
    [InlineData(new byte[] { 0x47, 0x56, 0x41, 0x53, 1, 2 }, "GVAS (Unreal save)")]
    [InlineData(new byte[] { 0x78, 0x9C, 1, 2, 3 }, "zlib stream")]
    [InlineData(new byte[] { 0x1F, 0x8B, 8, 0 }, "gzip")]
    [InlineData(new byte[] { 0x50, 0x4B, 3, 4, 0 }, "zip archive")]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 1 }, "PNG image")]
    [InlineData(new byte[] { 0xFF, 0x00, 0xFE, 0x01 }, "binary")]
    public void The_generic_description_names_recognisable_headers_and_reports_size_and_hash(byte[] blob, string type)
    {
        var d = WgsContentDescription.Generic("blob", blob);
        Assert.Equal("opaque", d.Kind);
        Assert.Equal(type, d.Members[0].Type);
        Assert.Equal(blob.Length, d.Members[0].Size);
        Assert.Contains(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(blob)), d.Members[0].Note, StringComparison.Ordinal);
    }

    [Fact]
    public void The_generic_description_tells_ini_like_text_from_plain_text()
    {
        Assert.Equal("text (ini-like)", WgsContentDescription.Generic("b", "[Section]\nk=v\n"u8.ToArray()).Members[0].Type);
        Assert.Equal("text", WgsContentDescription.Generic("b", "hello world"u8.ToArray()).Members[0].Type);
        Assert.Equal("binary", WgsContentDescription.Generic("b", []).Members[0].Type);
    }

    // ---- plugin loading -----------------------------------------------------------------

    private string StagePlugin(string sub = "")
    {
        var target = string.IsNullOrEmpty(sub) ? Path.Combine(_dir.FullName, "plugins") : Path.Combine(_dir.FullName, "plugins", sub);
        Directory.CreateDirectory(target);
        foreach (var name in new[] { "GamePassStorage.Adapters.AbioticFactor.dll", "GamePassStorage.dll", "GamePassStorage.Adapters.AbioticFactor.deps.json" })
        {
            var source = Path.Combine(AppContext.BaseDirectory, name);
            if (File.Exists(source)) File.Copy(source, Path.Combine(target, name));
        }
        return Path.Combine(_dir.FullName, "plugins");
    }

    [Fact]
    public void An_adapter_dll_in_a_folder_loads_through_an_isolated_context_that_shares_the_contract()
    {
        var plugins = StagePlugin();
        File.WriteAllText(Path.Combine(plugins, "native.dll"), "not a managed assembly");

        var result = WgsAdapterLoader.LoadFromDirectories([plugins]);

        var adapter = Assert.Single(result.Adapters);
        Assert.Equal("abiotic-factor", adapter.Id);
        Assert.Empty(result.Errors);
        Assert.Single(result.Assemblies);
        // A private copy of the adapter assembly, but the shared contract: this cast is the proof.
        Assert.NotSame(typeof(AbioticFactorAdapter).Assembly, adapter.GetType().Assembly);
        Assert.True(adapter.Matches(AbioticAdapterTests.FullFamily));
        Assert.NotNull(adapter.BlobInspector);
        Assert.NotNull(adapter.WriteGate);

        var registry = new WgsGameAdapterRegistry();
        Assert.Empty(result.RegisterInto(registry));
        Assert.Equal("abiotic-factor", registry.Resolve(AbioticAdapterTests.FullFamily).Id);

        // The plugin adapter describes a real bundle like the built-in one.
        var d = adapter.Describe(new WgsContainer { Name = "TestWorld-WC" }, AbioticAdapterTests.FixtureBlob());
        Assert.Equal("world bundle", d.Kind);
        Assert.Equal(3, d.Members.Count);

        // Loaded managed plugins must not hold their source files open, including on Windows.
        Directory.Delete(plugins, recursive: true);
        Assert.True(adapter.Matches(AbioticAdapterTests.FullFamily));
        Assert.Equal(3, adapter.Describe(new WgsContainer { Name = "TestWorld-WC" }, AbioticAdapterTests.FixtureBlob()).Members.Count);
    }

    [Fact]
    public void Adapters_in_a_sub_folder_load_and_problems_are_reported_without_stopping()
    {
        var plugins = StagePlugin("abiotic");
        Directory.CreateDirectory(Path.Combine(plugins, "empty"));

        var result = WgsAdapterLoader.LoadFromDirectories([plugins, Path.Combine(_dir.FullName, "missing"), ""]);

        Assert.Single(result.Adapters);
        var error = Assert.Single(result.Errors);
        Assert.Contains("does not exist", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Loading_the_contract_assembly_itself_or_the_same_adapter_twice_is_harmless()
    {
        var plugins = StagePlugin();
        Assert.Empty(WgsAdapterLoader.LoadFromAssembly(Path.Combine(plugins, "GamePassStorage.dll")).Adapters);

        var both = WgsAdapterLoader.LoadFromDirectories([plugins, plugins]);
        Assert.Equal(2, both.Adapters.Count);
        var registry = new WgsGameAdapterRegistry();
        var problems = both.RegisterInto(registry);
        Assert.Single(registry.Adapters);
        Assert.Contains("already registered", Assert.Single(problems), StringComparison.Ordinal);
    }

    [Fact]
    public void The_environment_variable_names_adapter_folders()
    {
        var before = Environment.GetEnvironmentVariable(WgsAdapterLoader.DirectoriesEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(WgsAdapterLoader.DirectoriesEnvironmentVariable,
                string.Join(Path.PathSeparator, "/a", " /b "));
            Assert.Equal(["/a", "/b"], WgsAdapterLoader.DirectoriesFromEnvironment());
            Environment.SetEnvironmentVariable(WgsAdapterLoader.DirectoriesEnvironmentVariable, null);
            Assert.Empty(WgsAdapterLoader.DirectoriesFromEnvironment());
        }
        finally
        {
            Environment.SetEnvironmentVariable(WgsAdapterLoader.DirectoriesEnvironmentVariable, before);
        }
    }
}
