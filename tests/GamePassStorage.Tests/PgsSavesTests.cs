using GamePassStorage.Tool;

namespace GamePassStorage.Tests;

/// <summary>Read-only PGS support against real temporary folders shaped as brodrigz/XgpSaveTools describes.</summary>
public sealed class PgsSavesTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("wgs-pgs-");
    public void Dispose()
    {
        // Remove links first: a recursive delete must not walk into (or trip over) a junction's target.
        foreach (var d in _dir.EnumerateDirectories("*", SearchOption.AllDirectories).Where(d => d.Attributes.HasFlag(FileAttributes.ReparsePoint)).ToList())
        {
            d.Delete();
        }
        _dir.Delete(recursive: true);
    }

    private string Pgs => Path.Combine(_dir.FullName, "pgs");

    /// <summary>A user root with snapshots 2 and 10 (10 newer) and a metadata file; <c>current</c> is linked when the OS allows it.</summary>
    private string UserRoot(bool linkCurrent, string? linkTarget = null)
    {
        var root = Path.Combine(Pgs, "u_2535400000000001_16D460");
        foreach (var (snap, data) in new[] { ("2", "old"), ("10", "new") })
        {
            var files = Path.Combine(root, snap, PgsSaves.ContainersRoot, "User_PROFILE");
            Directory.CreateDirectory(files);
            File.WriteAllText(Path.Combine(files, "C_ProfileData"), data);
            Directory.CreateDirectory(Path.Combine(root, snap, PgsSaves.ContainersRoot, "Slots", "Slot1"));
            File.WriteAllText(Path.Combine(root, snap, PgsSaves.ContainersRoot, "Slots", "Slot1", "save.bin"), data + data);
        }
        File.WriteAllText(Path.Combine(root, "10.json"), """{"Manifest":{}}""");
        Directory.CreateDirectory(Path.Combine(Pgs, "not-a-save-root"));
        if (linkCurrent) TryLink(Path.Combine(root, "current"), linkTarget ?? Path.Combine(root, "10"));
        return root;
    }

    /// <summary>A symbolic link where the OS allows one, else (Windows without the privilege) a junction, which needs none.</summary>
    private static bool TryLink(string link, string target)
    {
        try { Directory.CreateSymbolicLink(link, target); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!OperatingSystem.IsWindows()) return false;
            using var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            mklink.WaitForExit();
            return mklink.ExitCode == 0;
        }
    }

    [Fact]
    public void The_link_tests_are_not_silently_skipped_on_this_machine()
    {
        Assert.True(Linked(UserRoot(linkCurrent: true)), "could not create a symlink or junction for 'current'");
    }

    private static bool Linked(string root) => Directory.Exists(Path.Combine(root, "current"));

    [Fact]
    public void A_user_root_parses_its_ids_and_orders_snapshots_numerically()
    {
        var root = PgsSaves.TryOpen(UserRoot(linkCurrent: false))!;

        Assert.Equal("2535400000000001", root.Xuid);
        Assert.Equal("16D460", root.GameId);
        Assert.Equal(["2", "10"], root.Snapshots.ToArray());
        Assert.Null(root.CurrentSnapshot);
        Assert.Contains("no 'current'", root.Problem, StringComparison.Ordinal);
        Assert.Null(PgsSaves.TryOpen(Path.Combine(Pgs, "not-a-save-root")));
    }

    [Fact]
    public void Without_current_a_snapshot_must_be_named_and_is_never_guessed()
    {
        var root = PgsSaves.TryOpen(UserRoot(linkCurrent: false))!;

        Assert.Equal(WgsOperationStatus.Refused, PgsSaves.List(root).Status);
        Assert.Equal(WgsOperationStatus.Refused, PgsSaves.List(root, "7").Status);
        var listed = PgsSaves.List(root, "2");
        Assert.True(listed.Succeeded, listed.Message);
        Assert.Equal(["Slots/Slot1/save.bin", "User_PROFILE/C_ProfileData"], listed.Files.Select(f => f.RelativePath).ToArray());
    }

    [Fact]
    public void Current_resolves_through_the_link_and_extract_copies_the_files_untouched()
    {
        var path = UserRoot(linkCurrent: true);
        if (!Linked(path)) return;   // cannot create symlinks on this machine; covered where it can
        var root = PgsSaves.TryOpen(path)!;
        Assert.Equal("10", root.CurrentSnapshot);

        var outDir = Path.Combine(_dir.FullName, "out");
        var result = PgsSaves.Extract(root, outDir);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("10", result.Snapshot);
        Assert.Equal("new", File.ReadAllText(Path.Combine(outDir, "User_PROFILE", "C_ProfileData")));
        Assert.Equal("newnew", File.ReadAllText(Path.Combine(outDir, "Slots", "Slot1", "save.bin")));
        Assert.Equal(WgsOperationStatus.Refused, PgsSaves.Extract(root, outDir).Status);   // not empty
    }

    [Fact]
    public void A_current_link_that_leaves_the_save_root_is_not_trusted()
    {
        var elsewhere = Path.Combine(_dir.FullName, "99");
        Directory.CreateDirectory(elsewhere);
        var path = UserRoot(linkCurrent: true, linkTarget: elsewhere);
        if (!Linked(path)) return;

        var root = PgsSaves.TryOpen(path)!;

        Assert.Null(root.CurrentSnapshot);
        Assert.Contains("outside", root.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Backup_copies_every_snapshot_and_the_metadata_but_not_the_link()
    {
        var path = UserRoot(linkCurrent: true);
        var root = PgsSaves.TryOpen(path)!;
        var dest = Path.Combine(_dir.FullName, "backup");

        var result = PgsSaves.Backup(root, dest);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(File.Exists(Path.Combine(dest, "10.json")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(dest, "2", PgsSaves.ContainersRoot, "User_PROFILE", "C_ProfileData")));
        Assert.False(Directory.Exists(Path.Combine(dest, "current")));
    }

    [Fact]
    public void Find_filters_by_game_and_the_cli_names_known_titles()
    {
        UserRoot(linkCurrent: false);

        Assert.Single(PgsSaves.Find([Pgs]));
        Assert.Single(PgsSaves.Find([Pgs], "16d460"));
        Assert.Empty(PgsSaves.Find([Pgs], "ABCDEF"));

        using var stdout = new StringWriter();
        Assert.Equal(WgsCli.Ok, WgsCli.Run(["pgs", "find", "--pgs", Pgs], stdout, TextWriter.Null));
        Assert.Contains("game 16D460 (Forza Horizon 6)", stdout.ToString(), StringComparison.Ordinal);

        using var listed = new StringWriter();
        Assert.Equal(WgsCli.Ok, WgsCli.Run(["pgs", "list", Path.Combine(Pgs, "u_2535400000000001_16D460"), "--snapshot", "2"], listed, TextWriter.Null));
        Assert.Contains("User_PROFILE/C_ProfileData", listed.ToString(), StringComparison.Ordinal);
    }
}
