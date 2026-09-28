using System.Text;

namespace GamePassStorage.Tests;

/// <summary>Multi-blob containers, discovery, delete, restore, export/import and the process gate.</summary>
public class WgsExtensionsTests
{
    private const string Root = "/store";
    private const string Family = "Test.Game_abc!App";

    private static WgsStoreOptions Options(MemFs fs, IWgsWriteGate? gate = null)
        => new() { FileSystem = fs, WriteGate = gate, Clock = new FixedClock(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero)) };

    private static byte[] Bytes(int length, int seed)
    {
        var b = new byte[length];
        new Random(seed).NextBytes(b);
        return b;
    }

    private static IOException SharingViolation() => new("in use", unchecked((int)0x80070020));

    private static Dictionary<string, byte[]> Three() => new(StringComparer.Ordinal)
    {
        ["Meta"] = Bytes(20, 1),
        ["Body"] = Bytes(300, 2),
        ["Thumb"] = Bytes(40, 3),
    };

    private static WgsStore MultiStore(MemFs fs, out WgsStoreOptions options)
    {
        options = Options(fs);
        WgsStore.WriteNewContainer(Root, "Slot", Three(), Family, options);
        return WgsStore.Open(Root, options);
    }

    private static string Blobs(MemFs fs, WgsStore store, string blob)
    {
        var info = store.TryListBlobs(store.Containers[0]).Blobs!.Single(b => b.Name == blob);
        return $"{Root}/{store.Containers[0].FolderName}/{info.LocalId.ToString("N").ToUpperInvariant()}";
    }

    // ---- 1. multi-blob ------------------------------------------------------------------

    [Fact]
    public void A_multi_blob_container_is_created_read_back_by_name_and_listed()
    {
        var fs = new MemFs();
        var store = MultiStore(fs, out _);
        var c = store.Containers[0];

        var read = store.TryReadBlobs(c);
        Assert.True(read.Succeeded);
        Assert.Equal(["Meta", "Body", "Thumb"], read.Blobs!.Keys.ToArray());
        foreach (var (name, bytes) in Three()) Assert.Equal(bytes, read.Blobs[name]);
        Assert.Equal(360, c.BlobSize);
        Assert.Equal(WgsEntryState.Created, c.State);

        var list = store.TryListBlobs(c);
        Assert.Equal([20L, 300L, 40L], list.Blobs!.Select(b => b.Size!.Value).ToArray());

        // The single-blob read keeps saying multi-blob is not its layout, and points at TryReadBlobs.
        var single = store.TryReadBlob(c);
        Assert.Equal(WgsOperationStatus.UnsupportedLayout, single.Status);
        Assert.Contains("TryReadBlobs", single.Message, StringComparison.Ordinal);

        var d = store.Diagnose();
        Assert.Equal(["Slot"], d.MultiBlobContainers);
        Assert.Empty(d.MalformedManifestContainers);
        Assert.True(d.WriteAssessment.CanWrite);
    }

    [Fact]
    public void A_single_blob_container_reads_through_TryReadBlobs_as_Data()
    {
        var fs = new MemFs();
        var options = Options(fs);
        WgsStore.WriteNewContainer(Root, "World-WC", Bytes(50, 1), Family, options);
        var store = WgsStore.Open(Root, options);
        var read = store.TryReadBlobs(store.Containers[0]);
        Assert.Equal(["Data"], read.Blobs!.Keys.ToArray());
        Assert.Equal(Bytes(50, 1), read.Blobs["Data"]);
    }

    [Fact]
    public void Replacing_one_named_blob_keeps_the_others_byte_for_byte_and_on_the_same_files()
    {
        var fs = new MemFs();
        var store = MultiStore(fs, out _);
        var metaFile = Blobs(fs, store, "Meta");
        var thumbFile = Blobs(fs, store, "Thumb");
        var bodyFile = Blobs(fs, store, "Body");

        store.WriteNamedBlob(store.Containers[0], "Body", Bytes(500, 9));

        Assert.Equal((byte)2, store.Containers[0].ContainerNumber);
        Assert.Equal(20 + 500 + 40, store.Containers[0].BlobSize);
        Assert.Equal(Bytes(20, 1), fs.Files[metaFile]);          // same file, same bytes
        Assert.Equal(Bytes(40, 3), fs.Files[thumbFile]);
        Assert.False(fs.Files.ContainsKey(bodyFile));            // superseded generation pruned
        Assert.DoesNotContain(fs.Files.Keys, k => k.EndsWith("container.1", StringComparison.Ordinal));

        var reopened = WgsStore.Open(Root, Options(fs));
        var read = reopened.ReadBlobs(reopened.Containers[0]);
        Assert.Equal(Bytes(500, 9), read["Body"]);
        Assert.Equal(Bytes(20, 1), read["Meta"]);
        Assert.Equal(["Meta", "Body", "Thumb"], read.Keys.ToArray());   // order preserved
    }

    [Fact]
    public void A_new_named_blob_is_appended_and_an_etag_container_becomes_Modified()
    {
        var fs = new MemFs();
        var store = MultiStore(fs, out _);
        store.Containers[0].Etag = "\"0x1\"";
        store.Containers[0].State = WgsEntryState.Synced;
        store.Containers[0].RawState = 1;

        Assert.Equal(WgsOperationStatus.Ok, store.TryWriteNamedBlob(store.Containers[0], "Extra", Bytes(7, 4)).Status);

        Assert.Equal(WgsEntryState.Modified, store.Containers[0].State);
        Assert.Equal("\"0x1\"", store.Containers[0].Etag);      // echoed, never minted
        Assert.Equal(4, store.ReadBlobs(store.Containers[0]).Count);
    }

    [Fact]
    public void Writing_one_blob_over_a_multi_blob_container_is_refused_rather_than_dropping_the_rest()
    {
        var fs = new MemFs();
        var store = MultiStore(fs, out _);
        var before = fs.Snapshot();

        var result = store.TryWriteBlob(store.Containers[0], Bytes(5, 1));

        Assert.Equal(WgsOperationStatus.Failed, result.Status);
        Assert.Contains("Use WriteNamedBlob", result.Message, StringComparison.Ordinal);
        Assert.Equal(before, fs.Snapshot());
    }

    [Fact]
    public void An_interrupted_multi_blob_write_leaves_the_previous_generation_and_no_stray_files()
    {
        var fs = new MemFs();
        var store = MultiStore(fs, out var options);
        var before = fs.Snapshot();

        fs.Fault = (op, path) => op == "MoveOverwrite" && path.EndsWith("containers.index", StringComparison.Ordinal)
            ? SharingViolation() : null;
        var result = store.TryWriteNamedBlob(store.Containers[0], "Body", Bytes(500, 9));
        fs.Fault = null;

        Assert.Equal(WgsOperationStatus.LockConflict, result.Status);
        Assert.Equal(before, fs.Snapshot());                                  // nothing left behind
        Assert.Equal((byte)1, store.Containers[0].ContainerNumber);           // in-memory entry restored
        var reopened = WgsStore.Open(Root, options);
        Assert.Equal(Bytes(300, 2), reopened.ReadBlobs(reopened.Containers[0])["Body"]);
    }

    [Fact]
    public void An_interrupted_manifest_write_leaves_the_previous_generation_readable()
    {
        var fs = new MemFs();
        var store = MultiStore(fs, out var options);
        fs.Fault = (op, path) => op == "MoveOverwrite" && path.EndsWith("container.2", StringComparison.Ordinal)
            ? new IOException("disk full") : null;
        Assert.Equal(WgsOperationStatus.Failed, store.TryWriteNamedBlob(store.Containers[0], "Body", Bytes(9, 9)).Status);
        fs.Fault = null;
        var reopened = WgsStore.Open(Root, options);
        Assert.Equal(Three()["Body"], reopened.ReadBlobs(reopened.Containers[0])["Body"]);
        Assert.DoesNotContain(fs.Files.Keys, k => k.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public void A_write_refuses_when_an_untouched_blob_is_missing_or_mid_sync()
    {
        var fs = new MemFs();
        var store = MultiStore(fs, out _);
        var thumb = Blobs(fs, store, "Thumb");
        var bytes = fs.Files[thumb];
        fs.Files.Remove(thumb);

        var missing = store.TryWriteNamedBlob(store.Containers[0], "Body", Bytes(5, 1));
        Assert.Equal(WgsOperationStatus.Refused, missing.Status);
        Assert.Contains("Thumb", missing.Message, StringComparison.Ordinal);

        fs.Files[thumb] = bytes;
        Assert.Equal(WgsOperationStatus.Ok, store.TryWriteNamedBlob(store.Containers[0], "Body", Bytes(5, 1)).Status);
    }

    [Fact]
    public void Blob_names_must_fit_the_manifest_and_not_collide()
    {
        var fs = new MemFs();
        var store = MultiStore(fs, out _);
        Assert.Equal(WgsOperationStatus.Failed, store.TryWriteNamedBlob(store.Containers[0], new string('x', 80), [1]).Status);
        Assert.Equal(WgsOperationStatus.Failed, store.TryWriteNamedBlob(store.Containers[0], "body", [1]).Status);  // differs only by case
        Assert.Throws<ArgumentException>(() => store.CreateContainer("Other", new Dictionary<string, byte[]>()));
        Assert.Throws<InvalidOperationException>(() => store.CreateContainer("Slot", Three()));
    }

    [Fact]
    public void A_multi_blob_container_can_be_added_to_an_existing_store()
    {
        var fs = new MemFs();
        var options = Options(fs);
        WgsStore.WriteNewContainer(Root, "World-WC", Bytes(50, 1), Family, options);
        var store = WgsStore.Open(Root, options);

        var result = store.TryCreateContainer("Slot", Three());

        Assert.Equal(WgsOperationStatus.Ok, result.Status);
        var reopened = WgsStore.Open(Root, options);
        Assert.Equal(2, reopened.Containers.Count);
        Assert.Equal(3, reopened.ReadBlobs(reopened.Find("Slot")!).Count);
        Assert.Equal(Bytes(50, 1), reopened.ReadBlob(reopened.Find("World-WC")!));
    }

    [Fact]
    public void A_failed_container_creation_leaves_no_files_and_no_entry()
    {
        var fs = new MemFs();
        var options = Options(fs);
        WgsStore.WriteNewContainer(Root, "World-WC", Bytes(50, 1), Family, options);
        var store = WgsStore.Open(Root, options);
        var before = fs.Snapshot();
        fs.Fault = (op, path) => op == "MoveOverwrite" && path.EndsWith("containers.index", StringComparison.Ordinal)
            ? SharingViolation() : null;

        Assert.Equal(WgsOperationStatus.LockConflict, store.TryCreateContainer("Slot", Three()).Status);
        fs.Fault = null;

        Assert.Single(store.Containers);
        Assert.Equal(before, fs.Snapshot());
    }

    [Fact]
    public void Repair_never_guesses_at_a_multi_blob_manifest()
    {
        var fs = new MemFs();
        var store = MultiStore(fs, out _);
        fs.Files.Remove(Blobs(fs, store, "Body"));
        Assert.DoesNotContain("Slot", store.ContainersNeedingRepair());
        Assert.Empty(store.RepairRecoveredManifests());
    }

    [Fact]
    public void A_manifest_with_a_repeated_blob_name_is_malformed_and_the_trailing_bytes_are_kept()
    {
        var fs = new MemFs();
        var store = MultiStore(fs, out var options);
        var key = $"{Root}/{store.Containers[0].FolderName}/container.1";
        var manifest = fs.Files[key].ToArray();

        // Bytes after the last entry are preserved across a rewrite.
        fs.Files[key] = [.. manifest, 0xAA, 0xBB];
        store.WriteNamedBlob(store.Containers[0], "Body", Bytes(9, 9));
        var rewritten = fs.Files[$"{Root}/{store.Containers[0].FolderName}/container.2"];
        Assert.Equal([0xAA, 0xBB], rewritten[^2..]);

        // Two entries with one name: malformed.
        var dup = manifest.ToArray();
        Array.Copy(dup, 8, dup, 8 + 160, 128);
        fs.Files[$"{Root}/{store.Containers[0].FolderName}/container.2"] = dup;
        Assert.Equal(WgsOperationStatus.UnsupportedLayout, WgsStore.Open(Root, options).TryReadBlobs(store.Containers[0]).Status);
        Assert.Equal(["Slot"], WgsStore.Open(Root, options).Diagnose().MalformedManifestContainers);
    }

    // ---- 2. discovery -------------------------------------------------------------------

    private static void Seed(MemFs fs, string storePath, string family)
        => WgsStore.WriteNewContainer(storePath, "C", [1], family, Options(fs));

    [Fact]
    public void Discovery_finds_stores_under_LocalAppData_and_XboxGames_and_filters_by_family()
    {
        var fs = new MemFs();
        Seed(fs, "/lad/Packages/PlayStack.AbioticFactor_3wcqaesafpzfy/SystemAppData/wgs/2535_0000-1111", "PlayStack.AbioticFactor_3wcqaesafpzfy!App");
        Seed(fs, "/lad/Packages/Other.Game_xyz/SystemAppData/wgs/77_ab_cd", "Other.Game_xyz!App");
        fs.Files["/lad/Packages/NoStore.App_1/SystemAppData/wgs/t/junk"] = [1];
        Seed(fs, "/d/XboxGames/GameSave/wgs/99_scid", "Drive.Title_9!AppShip");
        Seed(fs, "/d/XboxGames/GameSave/wgs/Nested.Pkg_5/12_s", "Ignored!X");
        var options = new WgsStoreDiscoveryOptions { FileSystem = fs, LocalAppData = "/lad", DriveRoots = ["/d"] };

        var all = WgsStoreDiscovery.Find(options: options);
        Assert.Equal(4, all.Count);

        var abiotic = Assert.Single(WgsStoreDiscovery.Find("abiotic", false, options));
        Assert.Equal("PlayStack.AbioticFactor_3wcqaesafpzfy", abiotic.PackageFamilyName);
        Assert.Equal("2535", abiotic.Xuid);
        Assert.Equal("0000-1111", abiotic.Scid);
        Assert.Equal(WgsStoreLocationSource.LocalAppData, abiotic.Source);
        Assert.True(WgsStore.IsContainerFolder(abiotic.StorePath, fs));

        var other = Assert.Single(WgsStoreDiscovery.Find("Other.Game_xyz", exact: true, options));
        Assert.Equal("ab_cd", other.Scid);                 // split at the first underscore only
        Assert.Empty(WgsStoreDiscovery.Find("Other.Game", exact: true, options));

        var drive = Assert.Single(WgsStoreDiscovery.Find("Drive.Title", false, options));
        Assert.Equal("Drive.Title_9", drive.PackageFamilyName);   // from the index, before the '!'
        Assert.Equal(WgsStoreLocationSource.XboxGamesDrive, drive.Source);
        Assert.Equal("Nested.Pkg_5", Assert.Single(WgsStoreDiscovery.Find("Nested", false, options)).PackageFamilyName);
    }

    [Fact]
    public void Discovery_returns_nothing_where_the_roots_do_not_exist()
    {
        var options = new WgsStoreDiscoveryOptions { FileSystem = new MemFs(), LocalAppData = "/nope", DriveRoots = ["/none"] };
        Assert.Empty(WgsStoreDiscovery.Find(options: options));
        Assert.Empty(WgsStoreDiscovery.Find(options: new WgsStoreDiscoveryOptions { LocalAppData = "", DriveRoots = [] }));
        Assert.Empty(WgsStoreDiscovery.Find("x", options: new WgsStoreDiscoveryOptions
        {
            LocalAppData = Path.Combine(Path.GetTempPath(), "wgs-missing-" + Guid.NewGuid().ToString("N")),
            DriveRoots = [],
        }));
    }

    // ---- 3. delete ----------------------------------------------------------------------

    private static WgsStore TwoContainers(MemFs fs, out WgsStoreOptions options, bool uploaded)
    {
        options = Options(fs);
        WgsStore.WriteNewContainer(Root, "A", Bytes(30, 1), Family, options);
        var store = WgsStore.Open(Root, options);
        store.AddOrReplaceContainer("B", Bytes(40, 2));
        if (uploaded)
        {
            var b = store.Find("B")!;
            b.Etag = "\"0x8DEBCCC41BE9635\"";
            b.State = WgsEntryState.Synced;
            b.RawState = 1;
            store.WriteBlob(b, Bytes(41, 3));   // now Modified, ETag echoed
        }
        return WgsStore.Open(Root, options);
    }

    [Fact]
    public void Deleting_a_never_uploaded_container_removes_it_from_the_index_and_clears_its_files()
    {
        var fs = new MemFs();
        var store = TwoContainers(fs, out var options, uploaded: false);
        var b = store.Find("B")!;
        var folder = $"{Root}/{b.FolderName}/";
        Assert.Equal(WgsDeleteAction.RemoveFromIndex, store.PlanDelete(b).Action);

        var result = store.TryDeleteContainer(b);

        Assert.Equal(WgsOperationStatus.Ok, result.Status);
        Assert.Null(result.Container);
        var reopened = WgsStore.Open(Root, options);
        Assert.Equal(["A"], reopened.Containers.Select(c => c.Name).ToArray());
        Assert.DoesNotContain(fs.Files.Keys, k => k.StartsWith(folder, StringComparison.Ordinal));
        Assert.Empty(reopened.OrphanedContainers());
    }

    [Fact]
    public void Deleting_a_container_the_cloud_knows_leaves_a_tombstone_that_keeps_its_etag()
    {
        var fs = new MemFs();
        var store = TwoContainers(fs, out var options, uploaded: true);
        var b = store.Find("B")!;
        Assert.Equal(WgsDeleteAction.MarkDeleted, store.PlanDelete(b).Action);
        var before = store.IndexFileTime;

        var result = store.TryDeleteContainer(b);

        Assert.Equal(WgsOperationStatus.Ok, result.Status);
        var reopened = WgsStore.Open(Root, options);
        var tomb = reopened.Find("B")!;
        Assert.Equal(WgsEntryState.Deleted, tomb.State);
        Assert.Equal(3u, tomb.RawState);
        Assert.Equal("\"0x8DEBCCC41BE9635\"", tomb.Etag);
        Assert.True(tomb.IsPendingDeletion);
        Assert.True(reopened.IndexFileTime > before);                     // strictly advanced
        Assert.Equal(["B"], reopened.PendingDeletionContainers);
        Assert.Empty(reopened.UnsafeStateContainers);
        Assert.Equal(WgsDeleteAction.AlreadyDeleted, reopened.PlanDelete(tomb).Action);

        // Other containers can still be written; the tombstone itself cannot, and repair does not undo it.
        Assert.Equal(WgsOperationStatus.Ok, reopened.TryWriteBlob(reopened.Find("A")!, Bytes(5, 5)).Status);
        Assert.Equal(WgsOperationStatus.Refused, reopened.TryWriteBlob(reopened.Find("B")!, Bytes(5, 5)).Status);
        Assert.Equal(WgsOperationStatus.Refused, reopened.TryAddOrReplaceContainer("B", Bytes(5, 5)).Status);
        Assert.DoesNotContain("B", reopened.ContainersNeedingRepair());
        Assert.Equal(3u, WgsStore.Open(Root, options).Find("B")!.RawState);
        // And a second delete in the same store works (the tombstone does not block the gate).
        Assert.Equal(WgsOperationStatus.Ok, reopened.TryDeleteContainer(reopened.Find("A")!).Status);
    }

    [Fact]
    public void A_tombstone_without_an_etag_is_still_damage_and_blocks_writes()
    {
        var fs = new MemFs();
        var store = TwoContainers(fs, out _, uploaded: false);
        var b = store.Find("B")!;
        b.State = WgsEntryState.Deleted;
        b.RawState = 3;
        Assert.False(b.IsPendingDeletion);
        Assert.Contains("B", store.UnsafeStateContainers);
        Assert.False(store.AssessWrite().CanWrite);
    }

    [Fact]
    public void An_interrupted_delete_changes_nothing()
    {
        var fs = new MemFs();
        var store = TwoContainers(fs, out var options, uploaded: true);
        var before = fs.Snapshot();
        fs.Fault = (op, path) => op == "MoveOverwrite" && path.EndsWith("containers.index", StringComparison.Ordinal)
            ? SharingViolation() : null;

        Assert.Equal(WgsOperationStatus.LockConflict, store.TryDeleteContainer(store.Find("B")!).Status);
        Assert.Equal(WgsOperationStatus.LockConflict, store.TryDeleteContainer(store.Find("A")!).Status);
        fs.Fault = null;

        Assert.Equal(before, fs.Snapshot());
        Assert.Equal(WgsEntryState.Modified, store.Find("B")!.State);
        Assert.Equal(2, store.Containers.Count);
        Assert.Equal(2, WgsStore.Open(Root, options).Containers.Count);
    }

    // ---- 4. restore ---------------------------------------------------------------------

    [Fact]
    public void Restoring_a_backup_puts_the_old_content_back_after_taking_a_safety_copy()
    {
        var fs = new MemFs();
        var store = TwoContainers(fs, out var options, uploaded: true);
        store.CopyStoreTo("/backup");
        var backupIndex = WgsStore.Open("/backup", options);
        store.WriteBlob(store.Find("A")!, Bytes(99, 7));
        store.WriteBlob(store.Find("B")!, Bytes(98, 8));
        store.AddOrReplaceContainer("C", Bytes(10, 1));   // a container the backup lacks
        var current = fs.Snapshot();

        var result = WgsStore.TryRestore(Root, "/backup", "/safety", options);

        Assert.True(result.Succeeded, result.Message);
        var restored = result.Store!;
        Assert.Equal(["A", "B"], restored.Containers.Select(c => c.Name).ToArray());
        Assert.Equal(Bytes(30, 1), restored.ReadBlob(restored.Find("A")!));
        Assert.Equal(Bytes(41, 3), restored.ReadBlob(restored.Find("B")!));
        Assert.True(restored.IndexFileTime > backupIndex.IndexFileTime);
        Assert.True(restored.IndexFileTime > store.IndexFileTime);
        Assert.False(restored.SyncState.HasFlag(WgsSyncState.FullyUploaded));
        Assert.Equal("\"0x8DEBCCC41BE9635\"", restored.Find("B")!.Etag);
        Assert.Equal(WgsEntryState.Modified, restored.Find("B")!.State);
        Assert.Equal(WgsEntryState.Created, restored.Find("A")!.State);
        Assert.Empty(restored.OrphanedContainers());                     // C's files were cleared
        Assert.DoesNotContain(fs.Files.Keys, k => k.StartsWith(Root + "/", StringComparison.Ordinal) && k.Contains("/container.", StringComparison.Ordinal)
            && !restored.Containers.Any(c => k.StartsWith($"{Root}/{c.FolderName}/", StringComparison.Ordinal)));

        // The safety copy is the store as it was just before the restore.
        foreach (var (key, value) in current.Where(f => f.Key.StartsWith(Root + "/", StringComparison.Ordinal)))
        {
            Assert.Equal(value, Convert.ToHexString(fs.Files["/safety" + key[Root.Length..]]));
        }
        Assert.Equal(3, WgsSnapshot.Capture(WgsStore.Open("/safety", options)).Containers.Count);
    }

    [Fact]
    public void A_backup_that_is_not_a_valid_store_is_refused_and_nothing_changes()
    {
        var fs = new MemFs();
        var store = TwoContainers(fs, out var options, uploaded: false);
        store.CopyStoreTo("/backup");
        var backupB = WgsStore.Open("/backup", options).Find("B")!;
        fs.Files.Remove(fs.Files.Keys.First(k => k.StartsWith($"/backup/{backupB.FolderName}/", StringComparison.Ordinal)
            && !k.EndsWith("container.1", StringComparison.Ordinal)));
        var before = fs.Snapshot();

        var damaged = WgsStore.TryRestore(Root, "/backup", "/safety", options);
        Assert.Equal(WgsOperationStatus.Refused, damaged.Status);
        Assert.Contains(damaged.Problems, p => p.Contains("missing", StringComparison.Ordinal));

        var notAStore = WgsStore.TryRestore(Root, "/nothing", "/safety", options);
        Assert.Equal(WgsOperationStatus.Refused, notAStore.Status);
        Assert.NotEmpty(notAStore.Problems);
        Assert.Equal(before, fs.Snapshot());
        Assert.DoesNotContain(fs.Files.Keys, k => k.StartsWith("/safety", StringComparison.Ordinal));
    }

    [Fact]
    public void Restore_refuses_a_different_title_a_gate_refusal_and_a_used_safety_folder()
    {
        var fs = new MemFs();
        var store = TwoContainers(fs, out var options, uploaded: false);
        store.CopyStoreTo("/backup");
        WgsStore.WriteNewContainer("/foreign", "A", [1], "Someone.Else_1!App", options);

        Assert.Equal(WgsOperationStatus.Refused, WgsStore.TryRestore(Root, "/foreign", "/safety", options).Status);

        fs.Files["/safety/leftover"] = [1];
        Assert.Equal(WgsOperationStatus.Refused, WgsStore.TryRestore(Root, "/backup", "/safety", options).Status);
        fs.Files.Remove("/safety/leftover");

        Assert.Equal(WgsOperationStatus.Refused, WgsStore.TryRestore(Root, "/backup", "/store/inside", options).Status);
        Assert.Equal(WgsOperationStatus.Refused, WgsStore.TryRestore(Root, Root, "/safety", options).Status);

        var gated = new WgsStoreOptions { FileSystem = fs, WriteGate = WgsWriteGates.RefuseWhileRunning(new FakeProcesses("game"), "game") };
        var refused = WgsStore.TryRestore(Root, "/backup", "/safety", gated);
        Assert.Equal(WgsOperationStatus.Refused, refused.Status);
        Assert.Contains("game is running", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(fs.Files.Keys, k => k.StartsWith("/safety", StringComparison.Ordinal));
    }

    [Fact]
    public void An_interrupted_restore_never_replaces_the_index()
    {
        var fs = new MemFs();
        var store = TwoContainers(fs, out var options, uploaded: true);
        store.CopyStoreTo("/backup");
        store.WriteBlob(store.Find("A")!, Bytes(99, 7));
        var before = fs.Snapshot();
        var expectedA = Bytes(99, 7);
        fs.Fault = (op, path) => op == "MoveOverwrite" && path.EndsWith("containers.index", StringComparison.Ordinal)
            && path.StartsWith(Root, StringComparison.Ordinal) ? SharingViolation() : null;

        var result = WgsStore.TryRestore(Root, "/backup", "/safety", options);
        fs.Fault = null;

        Assert.Equal(WgsOperationStatus.LockConflict, result.Status);
        Assert.Contains("/safety", result.SafetyCopyPath, StringComparison.Ordinal);
        var reopened = WgsStore.Open(Root, options);
        Assert.Equal(expectedA, reopened.ReadBlob(reopened.Find("A")!));   // still the pre-restore store
        Assert.Equal(before[$"{Root}/containers.index"], fs.Snapshot()[$"{Root}/containers.index"]);
        Assert.DoesNotContain(fs.Files.Keys, k => k.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public void A_restore_interrupted_while_copying_files_leaves_the_current_store_intact()
    {
        var fs = new MemFs();
        var store = TwoContainers(fs, out var options, uploaded: false);
        store.CopyStoreTo("/backup");
        store.WriteBlob(store.Find("A")!, Bytes(99, 7));
        var before = fs.Snapshot().Where(f => f.Key.StartsWith(Root + "/", StringComparison.Ordinal)).ToDictionary(f => f.Key, f => f.Value);
        fs.Fault = (op, path) => op == "MoveOverwrite" && path.StartsWith(Root, StringComparison.Ordinal)
            && path.EndsWith("container.1", StringComparison.Ordinal) ? new IOException("disk full") : null;

        var result = WgsStore.TryRestore(Root, "/backup", "/safety", options);
        fs.Fault = null;

        Assert.Equal(WgsOperationStatus.Failed, result.Status);
        var after = fs.Snapshot().Where(f => f.Key.StartsWith(Root + "/", StringComparison.Ordinal)).ToDictionary(f => f.Key, f => f.Value);
        Assert.Equal(before[$"{Root}/containers.index"], after[$"{Root}/containers.index"]);
        var reopened = WgsStore.Open(Root, options);
        Assert.Equal(Bytes(99, 7), reopened.ReadBlob(reopened.Find("A")!));
    }

    // ---- 5. export / import -------------------------------------------------------------

    [Fact]
    public void Export_then_import_round_trips_single_and_multi_blob_containers_into_another_store()
    {
        var fs = new MemFs();
        var options = Options(fs);
        WgsStore.WriteNewContainer(Root, "World-WC", Bytes(50, 1), Family, options);
        var source = WgsStore.Open(Root, options);
        source.CreateContainer("Odd/Name:*", Three());
        source.Etag("World-WC", "\"0x9\"");

        var export = source.TryExportTo("/out");

        Assert.True(export.Succeeded, export.Message);
        Assert.Equal(2, export.Manifest!.Containers.Count);
        var json = Encoding.UTF8.GetString(fs.Files["/out/wgs-export.json"]);
        Assert.Contains("0x9", json, StringComparison.Ordinal);
        Assert.Equal(Bytes(50, 1), fs.Files["/out/World-WC/Data"]);
        Assert.All(fs.Files.Keys.Where(k => k.StartsWith("/out/", StringComparison.Ordinal)),
            k => Assert.DoesNotContain("*", k, StringComparison.Ordinal));
        Assert.Equal(WgsOperationStatus.Refused, source.TryExportTo("/out").Status);     // not empty

        WgsStore.WriteNewContainer("/target", "Keep-WC", Bytes(5, 5), Family, options);
        var target = WgsStore.Open("/target", options);
        target.AddOrReplaceContainer("World-WC", Bytes(6, 6));      // will be replaced

        var plan = target.PlanImport("/out");
        Assert.True(plan.CanApply);
        Assert.Contains(plan.Items, i => i.ContainerName == "World-WC" && !i.IsNew);
        Assert.Contains(plan.Items, i => i.ContainerName == "Odd/Name:*" && i.IsNew && i.BlobNames.Count == 3);

        var result = target.TryImport("/out");

        Assert.True(result.Succeeded, result.Message);
        var reopened = WgsStore.Open("/target", options);
        Assert.Equal(Bytes(50, 1), reopened.ReadBlob(reopened.Find("World-WC")!));
        Assert.Equal(Three()["Body"], reopened.ReadBlobs(reopened.Find("Odd/Name:*")!)["Body"]);
        Assert.Equal(Bytes(5, 5), reopened.ReadBlob(reopened.Find("Keep-WC")!));
        Assert.Equal(string.Empty, reopened.Find("Odd/Name:*")!.Etag);   // an ETag in a file is never applied
        Assert.Equal(string.Empty, reopened.Find("World-WC")!.Etag);
    }

    [Fact]
    public void An_import_with_a_tampered_missing_or_escaping_file_writes_nothing()
    {
        var fs = new MemFs();
        var options = Options(fs);
        WgsStore.WriteNewContainer(Root, "World-WC", Bytes(50, 1), Family, options);
        var source = WgsStore.Open(Root, options);
        source.TryExportTo("/out");
        WgsStore.WriteNewContainer("/target", "Other", Bytes(5, 5), Family, options);
        var target = WgsStore.Open("/target", options);
        var before = fs.Snapshot();

        fs.Files["/out/World-WC/Data"] = Bytes(50, 99);   // same size, different bytes
        var tampered = target.TryImport("/out");
        Assert.Equal(WgsOperationStatus.Failed, tampered.Status);
        Assert.Contains("SHA-256", tampered.Message, StringComparison.Ordinal);

        fs.Files.Remove("/out/World-WC/Data");
        Assert.Contains("missing", target.TryImport("/out").Message, StringComparison.Ordinal);

        fs.Files["/out/World-WC/Data"] = Bytes(50, 1);
        var manifest = Encoding.UTF8.GetString(fs.Files["/out/wgs-export.json"]).Replace("World-WC/Data", "../../etc/passwd", StringComparison.Ordinal);
        fs.Files["/out/wgs-export.json"] = Encoding.UTF8.GetBytes(manifest);
        var escaping = target.PlanImport("/out");
        Assert.Contains(escaping.Problems, p => p.Contains("not a path inside", StringComparison.Ordinal));
        Assert.False(escaping.CanApply);

        fs.Files["/out/wgs-export.json"] = [1, 2, 3];
        Assert.False(target.PlanImport("/out").CanApply);

        var after = fs.Snapshot();
        foreach (var key in before.Keys.Where(k => k.StartsWith("/target/", StringComparison.Ordinal))) Assert.Equal(before[key], after[key]);
        Assert.Single(target.Containers);
    }

    [Fact]
    public void A_folder_of_blobs_without_a_manifest_imports_by_directory_layout()
    {
        var fs = new MemFs();
        var options = Options(fs);
        WgsStore.WriteNewContainer(Root, "Existing", Bytes(5, 5), Family, options);
        var store = WgsStore.Open(Root, options);
        fs.Files["/in/NewSlot/Data"] = Bytes(11, 1);
        fs.Files["/in/Multi/A"] = Bytes(3, 1);
        fs.Files["/in/Multi/B"] = Bytes(4, 2);

        Assert.True(store.TryImport("/in").Succeeded);

        var reopened = WgsStore.Open(Root, options);
        Assert.Equal(3, reopened.Containers.Count);
        Assert.Equal(2, reopened.ReadBlobs(reopened.Find("Multi")!).Count);
        Assert.Equal(WgsOperationStatus.Failed, reopened.TryImport("/empty").Status);
    }

    [Fact]
    public void An_import_stops_at_the_first_failure_and_earlier_containers_stay_fully_written()
    {
        var fs = new MemFs();
        var options = Options(fs);
        WgsStore.WriteNewContainer(Root, "Existing", Bytes(5, 5), Family, options);
        var store = WgsStore.Open(Root, options);
        fs.Files["/in/A/Data"] = Bytes(11, 1);
        fs.Files["/in/B/Data"] = Bytes(12, 2);
        var indexWrites = 0;
        fs.Fault = (op, path) => op == "MoveOverwrite" && path.EndsWith("containers.index", StringComparison.Ordinal)
            && ++indexWrites == 2 ? SharingViolation() : null;

        var result = store.TryImport("/in");
        fs.Fault = null;

        Assert.Equal(WgsOperationStatus.LockConflict, result.Status);
        Assert.Equal(["A"], result.Applied.ToArray());
        var reopened = WgsStore.Open(Root, options);
        Assert.Equal(["Existing", "A"], reopened.Containers.Select(c => c.Name).ToArray());
        Assert.Equal(Bytes(11, 1), reopened.ReadBlob(reopened.Find("A")!));
        Assert.DoesNotContain(fs.Files.Keys, k => k.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public void Import_is_refused_by_the_write_gate_and_cannot_overwrite_a_tombstone()
    {
        var fs = new MemFs();
        var store = TwoContainers(fs, out var options, uploaded: true);
        store.TryDeleteContainer(store.Find("B")!);
        fs.Files["/in/B/Data"] = [1];
        Assert.Contains(store.PlanImport("/in").Problems, p => p.Contains("deleted", StringComparison.Ordinal));

        fs.Files.Remove("/in/B/Data");
        fs.Files["/in/New/Data"] = [1];
        var gated = WgsStore.Open(Root, new WgsStoreOptions { FileSystem = fs, WriteGate = WgsWriteGates.RefuseWhileRunning(new FakeProcesses("g"), "g") });
        Assert.Equal(WgsOperationStatus.Refused, gated.TryImport("/in").Status);
        Assert.Null(WgsStore.Open(Root, options).Find("New"));
    }

    // ---- 6. process gate ----------------------------------------------------------------

    private sealed class FakeProcesses(params string[] names) : IWgsProcessLister
    {
        public IReadOnlyCollection<string> GetRunningProcessNames() => names;
    }

    private sealed class ThrowingProcesses : IWgsProcessLister
    {
        public IReadOnlyCollection<string> GetRunningProcessNames() => throw new InvalidOperationException("denied");
    }

    [Fact]
    public void The_process_gate_refuses_only_while_a_named_process_runs_and_composes_with_the_structural_gate()
    {
        var fs = new MemFs();
        var options = Options(fs);
        WgsStore.WriteNewContainer(Root, "World-WC", Bytes(50, 1), Family, options);
        var store = WgsStore.Open(Root, options);

        var running = WgsWriteGates.RefuseWhileRunning(new FakeProcesses("Explorer", "abioticfactor.EXE"), "AbioticFactor");
        var concern = Assert.Single(running.Assess(store).Concerns);
        Assert.Equal(WgsWriteGates.ProcessRunning, concern.Code);
        Assert.True(concern.Blocking);

        Assert.True(WgsWriteGates.RefuseWhileRunning(new FakeProcesses("Explorer"), "AbioticFactor.exe").Assess(store).CanWrite);
        Assert.True(WgsWriteGates.RefuseWhileRunning(new FakeProcesses(""), "x").Assess(store).CanWrite);

        var unknown = WgsWriteGates.RefuseWhileRunning(new ThrowingProcesses(), "x").Assess(store);
        Assert.False(unknown.CanWrite);
        Assert.Equal(WgsWriteGates.ProcessCheckFailed, unknown.Concerns[0].Code);

        Assert.Throws<ArgumentException>(() => WgsWriteGates.RefuseWhileRunning(new FakeProcesses()));
        Assert.Throws<ArgumentException>(() => WgsWriteGates.RefuseWhileRunning(new FakeProcesses(), " "));

        // Composed with the structural gate, both verdicts are reported and either can block a write.
        PatchConflict(fs);
        var conflicted = WgsStore.Open(Root, options);
        var both = WgsWriteGates.Combine(WgsWriteGates.Structural, running).Assess(conflicted);
        Assert.Contains(both.Concerns, c => c.Code == WgsWriteGates.UnresolvedConflict);
        Assert.Contains(both.Concerns, c => c.Code == WgsWriteGates.ProcessRunning);

        var gated = new WgsStoreOptions { FileSystem = fs, WriteGate = WgsWriteGates.Combine(WgsWriteGates.Structural, running) };
        var healthy = WgsStore.Open(Root, gated);
        var before = fs.Snapshot();
        Assert.Equal(WgsOperationStatus.Refused, healthy.TryWriteBlob(healthy.Containers[0], Bytes(3, 3)).Status);
        Assert.Equal(before, fs.Snapshot());
    }

    [Fact]
    public void The_real_process_lister_lists_this_process()
    {
        var names = SystemWgsProcessLister.Instance.GetRunningProcessNames();
        Assert.NotEmpty(names);
    }

    private static void PatchConflict(MemFs fs)
    {
        var key = $"{Root}/containers.index";
        var bytes = fs.Files[key].ToArray();
        var flagsAt = 16 + (int)BitConverter.ToUInt32(bytes, 12) * 2 + 8;
        BitConverter.GetBytes((uint)WgsSyncState.HasUnresolvedConflicts).CopyTo(bytes, flagsAt);
        fs.Files[key] = bytes;
    }
}

internal static class StoreTestExtensions
{
    /// <summary>Simulates the service having issued an ETag (the library itself never mints one).</summary>
    public static void Etag(this WgsStore store, string name, string etag)
    {
        var c = store.Find(name)!;
        c.Etag = etag;
        c.State = WgsEntryState.Synced;
        c.RawState = 1;
    }
}
