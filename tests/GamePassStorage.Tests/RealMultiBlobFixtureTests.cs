namespace GamePassStorage.Tests;

/// <summary>
/// A real multi-blob store (package <c>BethesdaSoftworks.ProjectTitan_3275kfvn8vcwc</c>), captured with
/// <c>wgs sanitize</c>: index and manifests byte for byte, root GUID zeroed, every blob zero-filled at its real size.
/// It pins the multi-blob manifest layout and the index size rule against a store the game itself wrote.
/// </summary>
public sealed class RealMultiBlobFixtureTests : IDisposable
{
    private const string StoreName = "0009000000000002_00000000000000000000000000000001";
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("wgs-real-");
    public void Dispose() => _dir.Delete(recursive: true);

    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "fixtures", "RealMultiBlob", StoreName);

    private string WorkingCopy()
    {
        var to = Path.Combine(_dir.FullName, StoreName);
        foreach (var file in Directory.EnumerateFiles(Fixture, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(Fixture, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        return to;
    }

    private static readonly (string Name, long Size)[] AutosaveBlobs =
    [
        ("game.details", 334), ("game.details-BACKUP", 334), ("game_duration.dat", 30_562),
        ("game_duration.dat-BACKUP", 25_415), ("game_duration.dat-BACKUP.checksum", 8), ("game_duration.dat.checksum", 8),
    ];

    [Fact]
    public void The_game_written_multi_blob_manifest_reads_as_documented()
    {
        var opened = WgsStore.TryOpen(Fixture);
        Assert.True(opened.Succeeded, opened.Message);
        var store = opened.Store!;
        Assert.Equal("BethesdaSoftworks.ProjectTitan_3275kfvn8vcwc!Game", store.PackageFamilyName);
        Assert.Equal(["GAME-AUTOSAVE1", "PROFILE"], store.Containers.Select(c => c.Name).ToArray());

        var autosave = store.Find("GAME-AUTOSAVE1")!;
        var list = store.TryListBlobs(autosave);
        Assert.True(list.Succeeded, list.Message);
        Assert.Equal(AutosaveBlobs, list.Blobs!.Select(b => (b.Name, b.Size!.Value)).ToArray());
        Assert.All(list.Blobs!, b => Assert.False(b.SyncInFlight));

        // The manifest is the 8-byte header plus one 160-byte entry per blob, and nothing after.
        var manifest = Path.Combine(Fixture, autosave.FolderName, $"container.{autosave.ContainerNumber}");
        Assert.Equal(8 + 160 * AutosaveBlobs.Length, new FileInfo(manifest).Length);

        // The index records the total of every blob for a multi-blob container (previously an assumption).
        Assert.Equal(AutosaveBlobs.Sum(b => b.Size), autosave.BlobSize);

        var read = store.TryReadBlobs(autosave);
        Assert.True(read.Succeeded, read.Message);
        Assert.False(read.UsedFallback);
        Assert.Equal(23_620, store.ReadBlob(store.Find("PROFILE")!).Length);
    }

    [Fact]
    public void Diagnose_reports_the_real_store_healthy_and_multi_blob()
    {
        var store = WgsStore.Open(Fixture);
        var d = store.Diagnose();

        Assert.Equal(["GAME-AUTOSAVE1"], d.MultiBlobContainers.ToArray());
        Assert.Empty(d.ContainersNeedingRepair);
        Assert.Empty(d.MalformedManifestContainers);
        Assert.Empty(d.Orphans);
        Assert.True(d.WriteAssessment.CanWrite);
    }

    [Fact]
    public void Replacing_one_blob_of_the_real_container_keeps_the_others_and_echoes_the_etag()
    {
        var path = WorkingCopy();
        var store = WgsStore.Open(path);
        var autosave = store.Find("GAME-AUTOSAVE1")!;
        var etag = autosave.Etag;
        var before = store.TryListBlobs(autosave).Blobs!;

        var commit = store.TryWriteNamedBlob(autosave, "game.details", [1, 2, 3]);

        Assert.True(commit.Succeeded, commit.Message);
        var reopened = WgsStore.Open(path);
        var after = reopened.Find("GAME-AUTOSAVE1")!;
        Assert.Equal(etag, after.Etag);
        Assert.Equal(WgsEntryState.Modified, after.State);
        var blobs = reopened.ReadBlobs(after);
        Assert.Equal([1, 2, 3], blobs["game.details"]);
        var listed = reopened.TryListBlobs(after).Blobs!;
        foreach (var untouched in before.Where(b => b.Name != "game.details"))
        {
            Assert.Equal(untouched.LocalId, listed.Single(b => b.Name == untouched.Name).LocalId);
        }
        Assert.Equal(AutosaveBlobs.Sum(b => b.Size) - 334 + 3, after.BlobSize);
    }

    [Fact]
    public void The_real_store_unwraps_and_wraps_back_losslessly()
    {
        var path = WorkingCopy();
        var store = WgsStore.Open(path);
        var native = Path.Combine(_dir.FullName, "native");

        var unwrap = store.TryUnwrapTo(native, WgsNativeLayouts.ContainerFolders);
        Assert.True(unwrap.Succeeded, unwrap.Message);
        Assert.Equal(7, unwrap.Files.Count);
        Assert.Equal(30_562, new FileInfo(Path.Combine(native, "GAME-AUTOSAVE1", "game_duration.dat")).Length);

        var plan = store.PlanWrap(native, WgsNativeLayouts.ContainerFolders);
        Assert.True(plan.CanApply);
        Assert.All(plan.Import.Items, i => Assert.False(i.IsNew));
    }

    [Fact]
    public void Sanitizing_the_sanitized_store_is_stable()
    {
        var store = WgsStore.Open(Fixture);
        var again = Path.Combine(_dir.FullName, "again");

        var result = store.TrySanitizedCopyTo(again);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(3, result.StructureFiles);
        Assert.Equal(7, result.BlobsReplaced);
        foreach (var file in Directory.EnumerateFiles(Fixture, "*", SearchOption.AllDirectories))
        {
            Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(Path.Combine(again, Path.GetRelativePath(Fixture, file))));
        }
    }
}
