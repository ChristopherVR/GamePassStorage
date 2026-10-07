namespace GamePassStorage.Tests;

/// <summary>The package gate: refuse while the store's own package (the game, whatever its exe) runs.</summary>
public class WgsPackageGateTests
{
    private const string Root = "/store";
    private const string Family = "Studio.SomeGame_abcdefghjkmnp";

    private sealed class FakeLister(params WgsPackagedProcess[] running) : IWgsPackageProcessLister
    {
        public Exception? Throw { get; init; }
        public IReadOnlyCollection<WgsPackagedProcess> GetRunningPackagedProcesses() => Throw is null ? running : throw Throw;
    }

    private static WgsStore Store(MemFs fs, IWgsWriteGate gate)
    {
        var options = new WgsStoreOptions { FileSystem = fs, WriteGate = gate };
        WgsStore.WriteNewContainer(Root, "Slot", [1, 2, 3], Family + "!AppGame", options);
        return WgsStore.Open(Root, options);
    }

    [Fact]
    public void The_running_game_is_found_by_package_whatever_its_process_is_called()
    {
        var gate = WgsWriteGates.RefuseWhilePackageRuns(new FakeLister(new WgsPackagedProcess("gamelaunchhelper", 42, Family)));
        var store = Store(new MemFs(), gate);

        var assessment = gate.Assess(store);

        Assert.False(assessment.CanWrite);
        var concern = Assert.Single(assessment.Concerns);
        Assert.Equal(WgsWriteGates.PackageRunning, concern.Code);
        Assert.Contains("gamelaunchhelper", concern.Message, StringComparison.Ordinal);
        Assert.Equal(WgsOperationStatus.Refused, store.TryWriteBlob(store.Containers[0], [9]).Status);
    }

    [Fact]
    public void Other_packages_do_not_block_unless_named()
    {
        var lister = new FakeLister(new WgsPackagedProcess("XboxPcApp", 7, "Microsoft.GamingApp_8wekyb3d8bbwe"));
        var store = Store(new MemFs(), WgsWriteGates.AllowAll);

        Assert.True(WgsWriteGates.RefuseWhilePackageRuns(lister).Assess(store).CanWrite);
        Assert.False(WgsWriteGates.RefuseWhilePackageRuns(lister, "Microsoft.GamingApp_8wekyb3d8bbwe!App").Assess(store).CanWrite);
    }

    [Fact]
    public void A_lister_that_cannot_read_processes_refuses_rather_than_writing_blind()
    {
        var gate = WgsWriteGates.RefuseWhilePackageRuns(new FakeLister { Throw = new UnauthorizedAccessException("denied") });
        var store = Store(new MemFs(), WgsWriteGates.AllowAll);

        var concern = Assert.Single(gate.Assess(store).Concerns);
        Assert.Equal(WgsWriteGates.ProcessCheckFailed, concern.Code);
    }

    [Fact]
    public void The_system_lister_returns_well_formed_families_and_nothing_off_windows()
    {
        var running = SystemWgsPackageProcessLister.Instance.GetRunningPackagedProcesses();

        if (!OperatingSystem.IsWindows()) Assert.Empty(running);
        Assert.All(running, p => Assert.Matches("^[^_!]+_[a-z0-9]{13}$", p.PackageFamilyName));
    }
}
