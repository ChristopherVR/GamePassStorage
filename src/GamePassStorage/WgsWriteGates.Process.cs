using System.Diagnostics;

namespace GamePassStorage;

/// <summary>Lists the names of running processes. Injectable so a process gate can be tested anywhere.</summary>
public interface IWgsProcessLister
{
    /// <summary>Names of running processes, with or without a trailing <c>.exe</c>. May throw
    /// when the platform refuses; a gate treats that as "cannot tell" and refuses.</summary>
    IReadOnlyCollection<string> GetRunningProcessNames();
}

/// <summary>Lists processes through <see cref="Process.GetProcesses()"/>.</summary>
public sealed class SystemWgsProcessLister : IWgsProcessLister
{
    public static SystemWgsProcessLister Instance { get; } = new();

    public IReadOnlyCollection<string> GetRunningProcessNames()
    {
        var names = new List<string>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try { names.Add(process.ProcessName); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Exited between listing and reading the name.
                }
            }
        }
        return names;
    }
}

public static partial class WgsWriteGates
{
    public const string ProcessRunning = "process-running";
    public const string ProcessCheckFailed = "process-check-failed";

    /// <summary>
    /// A gate that refuses while any of the named processes is running (matched case-insensitively,
    /// with or without <c>.exe</c>; a trailing <c>*</c> matches any process whose name starts with the
    /// text before it, e.g. <c>AbioticFactor*</c> for <c>AbioticFactor-Win64-Shipping</c>): a running game or Xbox app can rewrite the store underneath an
    /// edit. If the process list cannot be read the gate refuses too, since it cannot tell.
    /// Compose with the structural gate using <see cref="Combine"/>.
    /// </summary>
    public static IWgsWriteGate RefuseWhileRunning(params string[] processNames)
        => RefuseWhileRunning(SystemWgsProcessLister.Instance, processNames);

    /// <summary><see cref="RefuseWhileRunning(string[])"/> with an injected process lister.</summary>
    public static IWgsWriteGate RefuseWhileRunning(IWgsProcessLister lister, params string[] processNames)
    {
        ArgumentNullException.ThrowIfNull(lister);
        ArgumentNullException.ThrowIfNull(processNames);
        var wanted = processNames.Select(NormalizeProcessName).ToList();
        if (wanted.Count == 0) throw new ArgumentException("Name at least one process.", nameof(processNames));
        return new ProcessGate(lister, wanted);
    }

    /// <summary>
    /// Runs every gate and merges their concerns, so one refusal never hides another. For example
    /// <c>Combine(WgsWriteGates.Structural, WgsWriteGates.RefuseWhileRunning("MyGame"))</c>.
    /// </summary>
    public static IWgsWriteGate Combine(params IWgsWriteGate[] gates)
    {
        ArgumentNullException.ThrowIfNull(gates);
        return new CombinedGate([.. gates]);
    }

    private static string NormalizeProcessName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var trimmed = name.Trim();
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? trimmed[..^4] : trimmed;
    }

    private sealed class ProcessGate(IWgsProcessLister lister, List<string> wanted) : IWgsWriteGate
    {
        public WgsWriteAssessment Assess(WgsStore store)
        {
            IReadOnlyCollection<string> running;
            try
            {
                running = lister.GetRunningProcessNames();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
                or UnauthorizedAccessException or NotSupportedException or PlatformNotSupportedException)
            {
                return new WgsWriteAssessment([new WgsWriteConcern(ProcessCheckFailed, true,
                    $"Could not check whether {string.Join(", ", wanted)} is running ({ex.Message}); not writing blind.")]);
            }
            var active = running.Where(n => !string.IsNullOrWhiteSpace(n)).Select(NormalizeProcessName)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var hits = active.Where(a => wanted.Any(w => Matches(w, a))).ToList();
            return hits.Count == 0
                ? WgsWriteAssessment.Clear
                : new WgsWriteAssessment([new WgsWriteConcern(ProcessRunning, true,
                    $"{string.Join(", ", hits)} is running. Close it (and the Xbox app) first, or it can overwrite or discard this write.")]);
        }
    }

    private static bool Matches(string wanted, string actual)
        => wanted.EndsWith('*')
            ? actual.StartsWith(wanted[..^1], StringComparison.OrdinalIgnoreCase)
            : string.Equals(wanted, actual, StringComparison.OrdinalIgnoreCase);

    private sealed class CombinedGate(List<IWgsWriteGate> gates) : IWgsWriteGate
    {
        public WgsWriteAssessment Assess(WgsStore store)
            => new([.. gates.SelectMany(g => g.Assess(store).Concerns)]);
    }
}
