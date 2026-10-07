using System.IO.Compression;
using GamePassStorage.Adapters.Catalog;

namespace GamePassStorage.Tests;

/// <summary>Payload knowledge: the GVAS header any Unreal save carries, and Palworld's .sav wrapper around one.</summary>
public class PayloadKnowledgeTests
{
    private const string PalClass = "/Script/Pal.PalWorldSaveGame";

    [Fact]
    public void A_ue5_gvas_header_gives_the_engine_version_and_class()
    {
        var h = WgsGvas.TryReadHeader(AbioticAdapterTests.Gvas(PalClass))!;

        Assert.Equal(3, h.SaveGameVersion);
        Assert.Equal(522, h.PackageVersionUe4);
        Assert.Equal(1012, h.PackageVersionUe5);
        Assert.Equal("5.4.2-0+++UE5+Release-5.4", h.EngineVersion);
        Assert.Equal(PalClass, h.SaveGameClass);
    }

    [Fact]
    public void A_ue4_header_has_no_ue5_version_and_bad_input_is_null_not_an_exception()
    {
        var ue5 = AbioticAdapterTests.Gvas("/Game/Save.Save_C");
        // Version 2 drops the UE5 package version: remove those four bytes and patch the version.
        byte[] ue4 = [.. ue5[..4], 2, 0, 0, 0, .. ue5[8..12], .. ue5[16..]];

        var h = WgsGvas.TryReadHeader(ue4)!;
        Assert.Null(h.PackageVersionUe5);
        Assert.Equal("/Game/Save.Save_C", h.SaveGameClass);

        for (var cut = 0; cut < ue5.Length - 4; cut += 7) Assert.Null(WgsGvas.TryReadHeader(ue5.AsSpan(0, cut)));
        Assert.Null(WgsGvas.TryReadHeader("GVAS\xff\xff\xff\xff"u8));
        Assert.Null(WgsGvas.TryReadHeader([1, 2, 3, 4, 5, 6]));
    }

    [Fact]
    public void The_generic_description_names_an_unreal_save_class()
    {
        var d = WgsContentDescription.Generic("Slot", AbioticAdapterTests.Gvas(PalClass));

        Assert.Contains(PalClass, d.Members[0].Type, StringComparison.Ordinal);
        Assert.Contains(d.Notes, n => n.Contains("Unreal Engine 5.4.2", StringComparison.Ordinal));
    }

    private static byte[] Zlib(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true)) z.Write(data);
        return ms.ToArray();
    }

    /// <summary>A .sav the way palworld-save-tools' <c>compress_gvas_to_sav</c> writes one, optionally with a CNK prefix.</summary>
    internal static byte[] Sav(byte[] gvas, byte saveType, bool chunked = false)
    {
        var once = Zlib(gvas);
        var body = saveType == 0x32 ? Zlib(once) : once;
        byte[] header = [.. BitConverter.GetBytes(gvas.Length), .. BitConverter.GetBytes(once.Length), .. "PlZ"u8, saveType];
        return chunked ? [.. new byte[8], .. "CNK"u8, 0, .. header, .. body] : [.. header, .. body];
    }

    [Theory]
    [InlineData(0x31, false)]
    [InlineData(0x32, false)]
    [InlineData(0x32, true)]
    public void A_palworld_sav_decodes_to_the_gvas_inside(byte saveType, bool chunked)
    {
        var gvas = AbioticAdapterTests.Gvas(PalClass);

        var result = PalworldSave.Instance.TryDecode(new WgsContainer { Name = "Level" }, Sav(gvas, saveType, chunked));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(gvas, result.Data);
    }

    [Fact]
    public void A_palworld_sav_with_wrong_lengths_or_an_unhandled_type_is_reported_not_guessed()
    {
        var sav = Sav(AbioticAdapterTests.Gvas(PalClass), 0x31);
        var c = new WgsContainer { Name = "Level" };

        sav[0]++;   // uncompressed length off by one
        Assert.Contains("header says", PalworldSave.Instance.TryDecode(c, sav).Message, StringComparison.Ordinal);
        sav[0]--;
        sav[11] = 0x30;
        Assert.Contains("0x30", PalworldSave.Instance.TryDecode(c, sav).Message, StringComparison.Ordinal);
        Assert.False(PalworldSave.Instance.TryDecode(c, [1, 2, 3]).Succeeded);
        Assert.False(PalworldSave.Instance.TryEncode(c, [1]).Succeeded);
    }

    [Fact]
    public void The_palworld_adapter_describes_its_saves_and_falls_back_for_anything_else()
    {
        var registry = new WgsGameAdapterRegistry();
        foreach (var a in GameCatalog.CreateAdapters()) registry.Register(a);
        var adapter = registry.Resolve("PocketpairInc.Palworld_ad4psfrxyesvt!AppPalShipping");
        var c = new WgsContainer { Name = "Level" };

        var d = adapter.Describe(c, Sav(AbioticAdapterTests.Gvas(PalClass), 0x32));
        Assert.Equal("palworld save", d.Kind);
        Assert.Contains(d.Notes, n => n.Contains(PalClass, StringComparison.Ordinal));
        Assert.Same(PalworldSave.Instance, adapter.Codec);

        Assert.Equal("opaque", adapter.Describe(c, [1, 2, 3]).Kind);
    }
}
