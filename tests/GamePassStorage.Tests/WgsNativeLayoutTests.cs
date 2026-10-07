namespace GamePassStorage.Tests;

/// <summary>Unwrapping a store to the game's plain files and wrapping them back.</summary>
public class WgsNativeLayoutTests
{
    private const string Root = "/store";
    private const string Family = "Test.Game_abc!App";

    private static WgsStoreOptions Options(MemFs fs)
        => new() { FileSystem = fs, Clock = new FixedClock(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero)) };

    private static byte[] Bytes(int length, int seed)
    {
        var b = new byte[length];
        new Random(seed).NextBytes(b);
        return b;
    }

    private static WgsStore SampleStore(MemFs fs)
    {
        var options = Options(fs);
        WgsStore.WriteNewContainer(Root, "Slot1", Bytes(64, 1), Family, options);
        var store = WgsStore.Open(Root, options);
        store.AddOrReplaceContainer("Slot2", Bytes(80, 2));
        store.CreateContainer("Multi", new Dictionary<string, byte[]> { ["Meta"] = Bytes(10, 3), ["Body"] = Bytes(90, 4) });
        return WgsStore.Open(Root, options);
    }

    [Fact]
    public void Container_folders_round_trip_through_a_second_store()
    {
        var fs = new MemFs();
        var store = SampleStore(fs);

        var unwrap = store.TryUnwrapTo("/native", WgsNativeLayouts.ContainerFolders);

        Assert.True(unwrap.Succeeded, unwrap.Message);
        Assert.Equal(Bytes(64, 1), fs.Files["/native/Slot1/Data"]);
        Assert.Equal(Bytes(90, 4), fs.Files["/native/Multi/Body"]);

        var options = Options(fs);
        WgsStore.WriteNewContainer("/other", "Keep", Bytes(5, 5), Family, options);
        var other = WgsStore.Open("/other", options);
        var result = other.TryWrap("/native", WgsNativeLayouts.ContainerFolders);

        Assert.True(result.Succeeded, result.Message);
        var reopened = WgsStore.Open("/other", options);
        Assert.Equal(Bytes(64, 1), reopened.ReadBlob(reopened.Find("Slot1")!));
        Assert.Equal(Bytes(10, 3), reopened.ReadBlobs(reopened.Find("Multi")!)["Meta"]);
        Assert.Equal(Bytes(5, 5), reopened.ReadBlob(reopened.Find("Keep")!));
        Assert.Equal(string.Empty, reopened.Find("Slot1")!.Etag);
    }

    [Fact]
    public void One_file_per_container_names_files_by_suffix_and_skips_multi_blob_containers()
    {
        var fs = new MemFs();
        var store = SampleStore(fs);

        var unwrap = store.TryUnwrapTo("/native", WgsNativeLayouts.OneFilePerContainer(".sav"));

        Assert.True(unwrap.Succeeded, unwrap.Message);
        Assert.Equal(["Slot1.sav", "Slot2.sav"], unwrap.Files.Order().ToArray());
        Assert.Contains(unwrap.Skipped, s => s.StartsWith("Multi/", StringComparison.Ordinal));
    }

    [Fact]
    public void Wrapping_over_an_existing_single_blob_container_keeps_its_blob_name()
    {
        var fs = new MemFs();
        var store = SampleStore(fs);
        var blobName = store.TryListBlobs(store.Find("Slot1")!).Blobs!.Single().Name;
        fs.Files["/edit/Slot1.sav"] = Bytes(70, 9);

        var result = store.TryWrap("/edit", WgsNativeLayouts.OneFilePerContainer(".sav"));

        Assert.True(result.Succeeded, result.Message);
        var reopened = WgsStore.Open(Root, Options(fs));
        var list = reopened.TryListBlobs(reopened.Find("Slot1")!).Blobs!;
        Assert.Equal(blobName, list.Single().Name);
        Assert.Equal(Bytes(70, 9), reopened.ReadBlob(reopened.Find("Slot1")!));
        Assert.Equal(Bytes(80, 2), reopened.ReadBlob(reopened.Find("Slot2")!));   // untouched
    }

    [Fact]
    public void Blobs_of_container_lays_out_one_container_flat_and_ignores_the_rest()
    {
        var fs = new MemFs();
        var store = SampleStore(fs);

        var unwrap = store.TryUnwrapTo("/native", WgsNativeLayouts.BlobsOfContainer("Multi"));

        Assert.Equal(["Body", "Meta"], unwrap.Files.Order().ToArray());
        Assert.Equal(Bytes(10, 3), fs.Files["/native/Meta"]);
    }

    [Fact]
    public void A_wrap_plan_reports_unmapped_files_without_blocking_and_a_dry_run_writes_nothing()
    {
        var fs = new MemFs();
        var store = SampleStore(fs);
        fs.Files["/edit/Slot1.sav"] = Bytes(70, 9);
        fs.Files["/edit/notes.txt"] = [1];
        var before = fs.Snapshot();

        var plan = store.PlanWrap("/edit", WgsNativeLayouts.OneFilePerContainer(".sav"));

        Assert.True(plan.CanApply);
        Assert.Equal(["notes.txt"], plan.Unmapped.ToArray());
        Assert.Equal(before.Where(k => !k.Key.StartsWith("/edit/", StringComparison.Ordinal)).Count(),
            fs.Snapshot().Where(k => !k.Key.StartsWith("/edit/", StringComparison.Ordinal)).Count());
        Assert.Equal(before, fs.Snapshot());
    }

    [Fact]
    public void Unwrap_refuses_a_non_empty_folder_and_a_wrap_with_nothing_mappable_fails()
    {
        var fs = new MemFs();
        var store = SampleStore(fs);
        fs.Files["/native/old.txt"] = [1];

        Assert.Equal(WgsOperationStatus.Refused, store.TryUnwrapTo("/native", WgsNativeLayouts.ContainerFolders).Status);

        fs.Files["/empty/readme.txt"] = [1];
        var result = store.TryWrap("/empty", WgsNativeLayouts.OneFilePerContainer(".sav"));
        Assert.Equal(WgsOperationStatus.Failed, result.Status);
    }

    [Fact]
    public void Layout_specs_parse_for_the_cli()
    {
        Assert.Equal("container-folders", WgsNativeLayouts.Parse("container-folders")!.Name);
        Assert.Equal("one-file:.sav", WgsNativeLayouts.Parse("one-file:.sav")!.Name);
        Assert.Equal("blobs:Level", WgsNativeLayouts.Parse("blobs:Level")!.Name);
        Assert.Null(WgsNativeLayouts.Parse("nonsense"));
    }
}
