using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GamePassStorage;

/// <summary>A running process that has a package identity (an installed Store or Game Pass app).</summary>
public sealed record WgsPackagedProcess(string ProcessName, int ProcessId, string PackageFamilyName);

/// <summary>Lists running processes that belong to a package. Injectable so a package gate can be tested anywhere.</summary>
public interface IWgsPackageProcessLister
{
    /// <summary>Running packaged processes. May throw when the platform refuses; a gate treats that as "cannot tell" and refuses.</summary>
    IReadOnlyCollection<WgsPackagedProcess> GetRunningPackagedProcesses();
}

/// <summary>
/// Asks Windows which package each running process belongs to (<c>GetPackageFamilyName</c>). Game Pass titles run with
/// the package identity of the store they save to, wherever they are installed, so this finds a running game without
/// knowing its executable name. A process this user cannot open (a system or elevated one) is skipped: a game runs as
/// the user. Off Windows there are no packaged processes and the list is empty.
/// </summary>
public sealed partial class SystemWgsPackageProcessLister : IWgsPackageProcessLister
{
    public static SystemWgsPackageProcessLister Instance { get; } = new();

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ErrorSuccess = 0;
    private const int ErrorInsufficientBuffer = 122;

    public IReadOnlyCollection<WgsPackagedProcess> GetRunningPackagedProcesses()
    {
        if (!OperatingSystem.IsWindows()) return [];
        var found = new List<WgsPackagedProcess>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (FamilyOf(process.Id) is { } family) found.Add(new WgsPackagedProcess(process.ProcessName, process.Id, family));
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Exited between listing and querying.
                }
            }
        }
        return found;
    }

    private static string? FamilyOf(int processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
        if (handle == 0) return null;
        try
        {
            var buffer = new ushort[128];
            var length = (uint)buffer.Length;
            var rc = GetPackageFamilyName(handle, ref length, ref buffer[0]);
            if (rc == ErrorInsufficientBuffer && length > buffer.Length)
            {
                buffer = new ushort[length];
                rc = GetPackageFamilyName(handle, ref length, ref buffer[0]);
            }
            if (rc != ErrorSuccess || length == 0) return null;   // APPMODEL_ERROR_NO_PACKAGE for an ordinary process
            return new string(MemoryMarshal.Cast<ushort, char>(buffer.AsSpan(0, (int)length - 1)));
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    private static partial int GetPackageFamilyName(nint process, ref uint packageFamilyNameLength, ref ushort packageFamilyName);
}

public static partial class WgsWriteGates
{
    public const string PackageRunning = "package-running";

    /// <summary>
    /// A gate that refuses while any process of the store's own package is running: the game itself, whatever its
    /// executable is called, so it works for every title without an adapter. <paramref name="alsoRefuseFor"/> adds
    /// other package families (for example the Xbox app, <c>Microsoft.GamingApp_8wekyb3d8bbwe</c>). If the process
    /// list cannot be read the gate refuses, since it cannot tell. Off Windows it never refuses.
    /// </summary>
    public static IWgsWriteGate RefuseWhilePackageRuns(params string[] alsoRefuseFor)
        => RefuseWhilePackageRuns(SystemWgsPackageProcessLister.Instance, alsoRefuseFor);

    /// <summary><see cref="RefuseWhilePackageRuns(string[])"/> with an injected lister.</summary>
    public static IWgsWriteGate RefuseWhilePackageRuns(IWgsPackageProcessLister lister, params string[] alsoRefuseFor)
    {
        ArgumentNullException.ThrowIfNull(lister);
        ArgumentNullException.ThrowIfNull(alsoRefuseFor);
        return new PackageGate(lister, [.. alsoRefuseFor.Select(WgsGameAdapterRegistry.FamilyOf).Where(f => f.Length > 0)]);
    }

    private sealed class PackageGate(IWgsPackageProcessLister lister, List<string> extra) : IWgsWriteGate
    {
        public WgsWriteAssessment Assess(WgsStore store)
        {
            var families = new List<string>(extra);
            var own = WgsGameAdapterRegistry.FamilyOf(store.PackageFamilyName);
            if (own.Length > 0) families.Add(own);
            if (families.Count == 0) return WgsWriteAssessment.Clear;
            IReadOnlyCollection<WgsPackagedProcess> running;
            try
            {
                running = lister.GetRunningPackagedProcesses();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
                or UnauthorizedAccessException or NotSupportedException or PlatformNotSupportedException or DllNotFoundException
                or EntryPointNotFoundException)
            {
                return new WgsWriteAssessment([new WgsWriteConcern(ProcessCheckFailed, true,
                    $"Could not check whether {string.Join(", ", families)} is running ({ex.Message}); not writing blind.")]);
            }
            var hits = running.Where(p => families.Contains(p.PackageFamilyName, StringComparer.OrdinalIgnoreCase)).ToList();
            return hits.Count == 0
                ? WgsWriteAssessment.Clear
                : new WgsWriteAssessment([new WgsWriteConcern(PackageRunning, true,
                    $"{string.Join(", ", hits.Select(h => $"{h.ProcessName} ({h.PackageFamilyName})").Distinct())} is running. " +
                    "Close it first, or it can overwrite or discard this write.")]);
        }
    }
}
