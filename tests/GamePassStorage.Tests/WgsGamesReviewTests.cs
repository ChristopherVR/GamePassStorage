using GamePassStorage.Tool;

namespace GamePassStorage.Tests;

/// <summary>Reviewing the titles on a machine: save folders with and without stores, and the <c>wgs games</c> report.</summary>
public class WgsGamesReviewTests
{
    [Fact]
    public void Save_folders_are_listed_with_their_stores_including_empty_ones()
    {
        var fs = new MemFs();
        var options = new WgsStoreOptions { FileSystem = fs };
        WgsStore.WriteNewContainer("/lad/Packages/Studio.Played_abc/SystemAppData/wgs/0009_0001", "Slot", [1], "Studio.Played_abc!App", options);
        fs.CreateDirectory("/lad/Packages/Studio.CloudOnly_def/SystemAppData/wgs");
        fs.CreateDirectory("/lad/Packages/Some.App_ghi/LocalState");

        var folders = WgsStoreDiscovery.FindSaveFolders(new WgsStoreDiscoveryOptions { FileSystem = fs, LocalAppData = "/lad", DriveRoots = [] });

        Assert.Equal(["Studio.CloudOnly_def", "Studio.Played_abc"], folders.Select(f => f.PackageFamilyName).ToArray());
        Assert.Empty(folders[0].Stores);
        Assert.Single(folders[1].Stores);
    }

    [Fact]
    public void Wgs_games_runs_read_only_on_this_machine()
    {
        using var stdout = new StringWriter();

        Assert.Equal(WgsCli.Ok, WgsCli.Run(["games"], stdout, TextWriter.Null));
        Assert.Equal(WgsCli.Ok, WgsCli.Run(["games", "--json"], TextWriter.Null, TextWriter.Null));
        Assert.False(string.IsNullOrWhiteSpace(stdout.ToString()));
    }
}
