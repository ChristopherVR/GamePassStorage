using System.Text;

namespace GamePassStorage.Tests;

/// <summary><see cref="WgsStore.TrySanitizedCopyTo"/>: structure kept, data gone.</summary>
public class WgsSanitizeTests
{
    private const string Root = "/store";

    private static WgsStoreOptions Options(MemFs fs)
        => new() { FileSystem = fs, Clock = new FixedClock(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero)) };

    [Fact]
    public void A_sanitized_copy_keeps_names_sizes_and_manifests_and_zero_fills_every_blob()
    {
        var fs = new MemFs();
        var options = Options(fs);
        WgsStore.WriteNewContainer(Root, "Secret", Encoding.UTF8.GetBytes("player name: someone"), "Test.Game_abc!App", options);
        var store = WgsStore.Open(Root, options);
        store.CreateContainer("Multi", new Dictionary<string, byte[]> { ["A"] = [9, 9], ["B"] = [8, 8, 8] });
        fs.Files[Root + "/notes.txt"] = [1];
        store = WgsStore.Open(Root, options);

        var result = store.TrySanitizedCopyTo("/clean");

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(3, result.BlobsReplaced);
        Assert.Contains(result.Skipped, s => s.StartsWith("notes.txt", StringComparison.Ordinal));
        var copy = WgsStore.Open("/clean", options);
        Assert.Equal(store.Containers.Select(c => (c.Name, c.BlobSize, c.Etag)), copy.Containers.Select(c => (c.Name, c.BlobSize, c.Etag)));
        Assert.Equal(new byte[20], copy.ReadBlob(copy.Find("Secret")!));
        Assert.Equal(new byte[3], copy.ReadBlobs(copy.Find("Multi")!)["B"]);
        Assert.DoesNotContain(fs.Files.Where(f => f.Key.StartsWith("/clean/", StringComparison.Ordinal)),
            f => Encoding.UTF8.GetString(f.Value).Contains("someone", StringComparison.Ordinal));
        foreach (var c in store.Containers)
        {
            var manifest = $"/container.{c.ContainerNumber}";
            Assert.Equal(fs.Files[Root + "/" + c.FolderName + manifest], fs.Files["/clean/" + c.FolderName + manifest]);
        }
    }

    [Fact]
    public void The_index_root_guid_is_zeroed_in_place_and_nothing_else_in_the_index_changes()
    {
        var fs = new MemFs();
        var options = Options(fs);
        WgsStore.WriteNewContainer(Root, "Slot", [1, 2, 3], "Test.Game_abc!App", options);
        var original = fs.Files[Root + "/containers.index"];

        Assert.True(WgsStore.Open(Root, options).TrySanitizedCopyTo("/clean").Succeeded);

        var clean = fs.Files["/clean/containers.index"];
        Assert.Equal(original.Length, clean.Length);
        var text = Encoding.Unicode.GetString(clean);
        Assert.Contains("00000000-0000-0000-0000-000000000000", text, StringComparison.Ordinal);
        var differing = Enumerable.Range(0, clean.Length).Where(i => clean[i] != original[i]).ToList();
        Assert.NotEmpty(differing);
        Assert.True(differing[^1] - differing[0] < 72, "only the 36-character GUID string may differ");
    }

    [Fact]
    public void Sanitize_refuses_a_non_empty_destination()
    {
        var fs = new MemFs();
        var options = Options(fs);
        WgsStore.WriteNewContainer(Root, "Slot", [1], "Test.Game_abc!App", options);
        fs.Files["/clean/x"] = [1];

        Assert.Equal(WgsOperationStatus.Refused, WgsStore.Open(Root, options).TrySanitizedCopyTo("/clean").Status);
    }
}
