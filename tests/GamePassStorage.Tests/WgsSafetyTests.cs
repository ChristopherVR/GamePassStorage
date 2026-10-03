using System.Text;
using System.Text.Json.Nodes;

namespace GamePassStorage.Tests;

public sealed class WgsSafetyTests
{
    private const string Root = "/store";

    private static WgsStore LoadFixture(out MemFs fs, out WgsStoreOptions options)
    {
        fs = new MemFs();
        var folder = Path.Combine(AppContext.BaseDirectory, "fixtures", "SyntheticMultiBlob");
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            fs.Files[Root + "/" + Path.GetRelativePath(folder, file).Replace('\\', '/')] = File.ReadAllBytes(file);
        options = new WgsStoreOptions { FileSystem = fs };
        return WgsStore.Open(Root, options);
    }

    private static Dictionary<string, string> StoreFiles(MemFs fs)
        => fs.Snapshot().Where(f => f.Key.StartsWith(Root + "/", StringComparison.Ordinal)).ToDictionary();

    [Fact]
    public void Independent_multi_blob_fixture_reads_and_round_trips_with_its_manifest_tail()
    {
        var store = LoadFixture(out var fs, out var options);
        var container = Assert.Single(store.Containers);
        var original = store.ReadBlobs(container);
        Assert.Equal(["Meta", "Body", "Thumb"], original.Keys.ToArray());
        Assert.Equal(Enumerable.Range(0, 256).Select(i => (byte)i).ToArray(), original["Body"]);
        Assert.Empty(WgsStore.ValidateStore(Root, options));

        Assert.True(store.TryWriteBlobs(container, new Dictionary<string, byte[]> { ["Meta"] = [7, 8] }).Succeeded);
        var reopened = WgsStore.Open(Root, options);
        var updated = reopened.ReadBlobs(reopened.Containers[0]);
        Assert.Equal(original["Body"], updated["Body"]);
        Assert.Equal(original["Thumb"], updated["Thumb"]);
        var manifest = fs.Files[$"{Root}/{container.FolderName}/container.{container.ContainerNumber}"];
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, manifest[^4..]);

        Assert.True(reopened.TryExportTo("/export").Succeeded);
        WgsStore.WriteNewContainer("/target", "Keep", [1], store.PackageFamilyName, options);
        var target = WgsStore.Open("/target", options);
        Assert.True(target.TryImport("/export").Succeeded);
        Assert.Equal(original["Body"], target.ReadBlobs(target.Find("SyntheticSlot")!)["Body"]);
    }

    [Theory]
    [InlineData("null-container")]
    [InlineData("null-blob")]
    [InlineData("duplicate")]
    [InlineData("duplicate-case")]
    [InlineData("empty-name")]
    [InlineData("long-name")]
    [InlineData("nul-name")]
    [InlineData("negative-size")]
    [InlineData("invalid-hash")]
    [InlineData("missing-hash")]
    [InlineData("version")]
    [InlineData("family")]
    public void Invalid_import_manifest_is_reported_without_writing(string defect)
    {
        var store = LoadFixture(out var fs, out _);
        Assert.True(store.TryExportTo("/import").Succeeded);
        var json = JsonNode.Parse(fs.Files["/import/wgs-export.json"])!;
        var containers = json["Containers"]!.AsArray();
        var blobs = containers[0]!["Blobs"]!.AsArray();
        switch (defect)
        {
            case "null-container": containers[0] = null; break;
            case "null-blob": blobs[0] = null; break;
            case "duplicate": blobs.Add(blobs[0]!.DeepClone()); break;
            case "duplicate-case":
                var duplicate = blobs[0]!.DeepClone();
                duplicate["Name"] = "meta";
                blobs.Add(duplicate);
                break;
            case "empty-name": blobs[0]!["Name"] = ""; break;
            case "long-name": blobs[0]!["Name"] = new string('x', 64); break;
            case "nul-name": blobs[0]!["Name"] = "A\0B"; break;
            case "negative-size": blobs[0]!["Size"] = -1; break;
            case "invalid-hash": blobs[0]!["Sha256"] = new string('z', 64); break;
            case "missing-hash": blobs[0]!.AsObject().Remove("Sha256"); break;
            case "version": json["Version"] = 999; break;
            case "family": json["PackageFamilyName"] = "Different.Game_000!App"; break;
        }
        fs.Files["/import/wgs-export.json"] = Encoding.UTF8.GetBytes(json.ToJsonString());
        var before = StoreFiles(fs);
        Assert.False(store.PlanImport("/import").CanApply);
        var imported = store.TryImport("/import");
        Assert.Equal(WgsOperationStatus.Failed, imported.Status);
        Assert.Empty(imported.Applied);
        Assert.Equal(before, StoreFiles(fs));
    }

    [Fact]
    public void Import_uses_the_verified_bytes_without_rereading_mutable_source_files()
    {
        var store = LoadFixture(out var fs, out _);
        Assert.True(store.TryExportTo("/import").Succeeded);
        var reads = new Dictionary<string, int>();
        fs.Fault = (op, path) =>
        {
            if (op == "ReadAllBytes" && path.StartsWith("/import/", StringComparison.Ordinal))
            {
                reads[path] = reads.GetValueOrDefault(path) + 1;
                if (reads[path] > 1) return new IOException("source changed after verification");
            }
            return null;
        };
        Assert.True(store.TryImport("/import").Succeeded);
        Assert.Equal(4, reads.Count);
        Assert.All(reads.Values, count => Assert.Equal(1, count));
    }

    [Fact]
    public void Import_verifies_the_last_container_before_writing_the_first()
    {
        var store = LoadFixture(out var fs, out _);
        fs.Files["/import/A/Data"] = [1];
        fs.Files["/import/B/Data"] = [2];
        var before = StoreFiles(fs);
        fs.Fault = (op, path) => op == "ReadAllBytes" && path == "/import/B/Data" ? new IOException("unreadable") : null;
        var result = store.TryImport("/import");
        Assert.Equal(WgsOperationStatus.Failed, result.Status);
        Assert.Empty(result.Applied);
        Assert.Equal(before, StoreFiles(fs));
    }

    [Theory]
    [InlineData("index")]
    [InlineData("manifest")]
    [InlineData("blob")]
    public void Interrupted_restore_with_colliding_ids_leaves_every_live_file_intact(string failure)
    {
        var store = LoadFixture(out var fs, out var options);
        store.CopyStoreTo("/backup");
        var c = store.Containers[0];
        var body = store.TryListBlobs(c).Blobs!.Single(b => b.Name == "Body");
        var bodyPath = $"{Root}/{c.FolderName}/{body.LocalId.ToString("N").ToUpperInvariant()}";
        fs.Files[bodyPath] = Enumerable.Repeat((byte)99, 256).ToArray();
        var before = StoreFiles(fs);
        fs.Fault = (op, path) =>
        {
            if (op != "MoveOverwrite" || !path.StartsWith(Root + "/", StringComparison.Ordinal)) return null;
            var name = Path.GetFileName(path);
            var matches = failure switch
            {
                "index" => name == "containers.index",
                "manifest" => name.StartsWith("container.", StringComparison.Ordinal),
                _ => name.Length == 32,
            };
            return matches ? new IOException("interrupted") : null;
        };
        var result = WgsStore.TryRestore(Root, "/backup", "/safety", options);
        fs.Fault = null;
        Assert.Equal(WgsOperationStatus.Failed, result.Status);
        Assert.Equal(before, StoreFiles(fs));
        Assert.Equal(fs.Files[bodyPath], WgsStore.Open(Root, options).ReadBlobs(c)["Body"]);
        Assert.DoesNotContain(fs.Files.Keys, p => p.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public void Successful_restore_uses_new_blob_ids_and_preserves_backup_bytes_and_tail()
    {
        var store = LoadFixture(out var fs, out var options);
        store.CopyStoreTo("/backup");
        var old = store.TryListBlobs(store.Containers[0]).Blobs!.Select(b => b.LocalId).ToHashSet();
        var backup = WgsStore.Open("/backup", options);
        var result = WgsStore.TryRestore(Root, "/backup", "/safety", options);
        Assert.True(result.Succeeded, result.Message);
        var restored = result.Store!;
        Assert.All(restored.TryListBlobs(restored.Containers[0]).Blobs!, b => Assert.DoesNotContain(b.LocalId, old));
        var restoredBlobs = restored.ReadBlobs(restored.Containers[0]);
        foreach (var (name, data) in backup.ReadBlobs(backup.Containers[0])) Assert.Equal(data, restoredBlobs[name]);
        var c = restored.Containers[0];
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, fs.Files[$"{Root}/{c.FolderName}/container.{c.ContainerNumber}"][^4..]);
    }

    [Fact]
    public void Restore_detects_index_changes_during_staging_and_removes_only_staged_files()
    {
        var store = LoadFixture(out var fs, out var options);
        store.CopyStoreTo("/backup");
        var changed = false;
        fs.Fault = (op, path) =>
        {
            if (!changed && op == "MoveOverwrite" && path.StartsWith(Root + "/", StringComparison.Ordinal))
            {
                changed = true;
                fs.Files[Root + "/containers.index"][0] ^= 1;
            }
            return null;
        };
        var expected = StoreFiles(fs);
        var result = WgsStore.TryRestore(Root, "/backup", "/safety", options);
        fs.Fault = null;
        expected[Root + "/containers.index"] = Convert.ToHexString(fs.Files[Root + "/containers.index"]);
        Assert.True(changed);
        Assert.Equal(WgsOperationStatus.ConcurrentChange, result.Status);
        Assert.Equal(expected, StoreFiles(fs));
    }

    [Fact]
    public void A_backup_blob_disappearing_during_restore_returns_a_failure_and_preserves_live_files()
    {
        var store = LoadFixture(out var fs, out var options);
        store.CopyStoreTo("/backup");
        var body = store.TryListBlobs(store.Containers[0]).Blobs!.Single(b => b.Name == "Body");
        var source = $"/backup/{store.Containers[0].FolderName}/{body.LocalId.ToString("N").ToUpperInvariant()}";
        var before = StoreFiles(fs);
        fs.Fault = (op, path) =>
        {
            if (op == "MoveOverwrite" && path.StartsWith(Root + "/", StringComparison.Ordinal)) fs.Files.Remove(source);
            return null;
        };
        var result = WgsStore.TryRestore(Root, "/backup", "/safety", options);
        fs.Fault = null;
        Assert.Equal(WgsOperationStatus.Failed, result.Status);
        Assert.Equal(before, StoreFiles(fs));
    }

    [Fact]
    public void Restore_with_no_free_manifest_generation_fails_without_overwriting_files()
    {
        var store = LoadFixture(out var fs, out var options);
        store.CopyStoreTo("/backup");
        var folder = $"{Root}/{store.Containers[0].FolderName}";
        for (var number = 0; number < 256; number++) fs.Files.TryAdd($"{folder}/container.{number}", [0]);
        var before = StoreFiles(fs);
        var result = WgsStore.TryRestore(Root, "/backup", "/safety", options);
        Assert.Equal(WgsOperationStatus.Failed, result.Status);
        Assert.Contains("no unused manifest", result.Message, StringComparison.Ordinal);
        Assert.Equal(before, StoreFiles(fs));
    }
}
