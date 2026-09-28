using GamePassStorage.Tool;

namespace GamePassStorage.Tests;

/// <summary>The <c>wgs</c> tool end to end against a real temporary store on disk.</summary>
public sealed class WgsCliTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("wgs-cli-");
    private string Store => Path.Combine(_dir.FullName, "store");

    public WgsCliTests()
    {
        WgsStore.WriteNewContainer(Store, "Slot1", [1, 2, 3, 4], "Test.Game_abc!App");
    }

    public void Dispose() => _dir.Delete(recursive: true);

    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var code = WgsCli.Run(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void No_arguments_prints_usage_and_fails()
    {
        var (code, output, _) = Run();
        Assert.Equal(WgsCli.Usage, code);
        Assert.Contains("wgs list", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_command_and_missing_arguments_are_usage_errors()
    {
        Assert.Equal(WgsCli.Usage, Run("frobnicate").Code);
        Assert.Equal(WgsCli.Usage, Run("extract", Store).Code);
        Assert.Equal(WgsCli.Usage, Run("list", Store, "--bogus").Code);
    }

    [Fact]
    public void List_shows_the_container_and_its_state()
    {
        var (code, output, _) = Run("list", Store);
        Assert.Equal(WgsCli.Ok, code);
        Assert.Contains("Slot1", output, StringComparison.Ordinal);
        Assert.Contains("Created", output, StringComparison.Ordinal);
        Assert.Contains("Test.Game_abc!App", output, StringComparison.Ordinal);

        var json = Run("list", Store, "--json");
        Assert.Contains("\"Name\": \"Slot1\"", json.Out, StringComparison.Ordinal);
    }

    [Fact]
    public void A_folder_that_is_not_a_store_fails_cleanly()
    {
        var (code, _, err) = Run("list", _dir.FullName + "/missing");
        Assert.Equal(WgsCli.Failure, code);
        Assert.Contains("error", err, StringComparison.Ordinal);
    }

    [Fact]
    public void Diagnose_reports_a_healthy_store_as_writable()
    {
        var (code, output, _) = Run("diagnose", Store);
        Assert.Equal(WgsCli.Ok, code);
        Assert.Contains("writable: yes", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_writes_the_blob_bytes()
    {
        var output = Path.Combine(_dir.FullName, "slot1.bin");
        Assert.Equal(WgsCli.Ok, Run("extract", Store, "Slot1", output).Code);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(output));
        Assert.Equal(WgsCli.Failure, Run("extract", Store, "Nope", output).Code);
    }

    [Fact]
    public void Put_requires_a_backup_and_then_replaces_the_blob()
    {
        var blob = Path.Combine(_dir.FullName, "new.bin");
        File.WriteAllBytes(blob, [9, 9]);

        Assert.Equal(WgsCli.Usage, Run("put", Store, "Slot1", blob).Code);

        var dry = Run("put", Store, "Slot1", blob, "--dry-run");
        Assert.Equal(WgsCli.Ok, dry.Code);
        Assert.Contains("would write", dry.Out, StringComparison.Ordinal);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, ReadSlot());

        var backup = Path.Combine(_dir.FullName, "backup");
        Assert.Equal(WgsCli.Ok, Run("put", Store, "Slot1", blob, "--backup", backup).Code);
        Assert.Equal(new byte[] { 9, 9 }, ReadSlot());
        Assert.True(File.Exists(Path.Combine(backup, WgsStore.IndexFileName)));

        Assert.Equal(WgsCli.Failure, Run("put", Store, "Slot1", blob, "--backup", backup).Code);
    }

    [Fact]
    public void Snapshots_taken_before_and_after_a_write_compare_as_changed()
    {
        var before = Path.Combine(_dir.FullName, "before.json");
        var after = Path.Combine(_dir.FullName, "after.json");
        Assert.Equal(WgsCli.Ok, Run("snapshot", Store, "-o", before).Code);
        Assert.Contains("identical", Run("compare", before, before).Out, StringComparison.Ordinal);

        var blob = Path.Combine(_dir.FullName, "new.bin");
        File.WriteAllBytes(blob, [7]);
        Run("put", Store, "Slot1", blob, "--backup", Path.Combine(_dir.FullName, "b"));
        Assert.Equal(WgsCli.Ok, Run("snapshot", Store, "-o", after).Code);

        var diff = Run("compare", before, after);
        Assert.Contains("CHANGED   Slot1", diff.Out, StringComparison.Ordinal);
    }

    [Fact]
    public void Backup_refuses_a_non_empty_destination()
    {
        var dest = Path.Combine(_dir.FullName, "copy");
        Assert.Equal(WgsCli.Ok, Run("backup", Store, dest).Code);
        Assert.Equal(WgsCli.Failure, Run("backup", Store, dest).Code);
    }

    private byte[] ReadSlot()
    {
        var store = WgsStore.Open(Store);
        return store.ReadBlob(store.Find("Slot1")!);
    }
}
