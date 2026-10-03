using System.Reflection;
using System.Runtime.Loader;

namespace GamePassStorage;

/// <summary>What loading adapter assemblies produced. Problems are listed, never thrown.</summary>
public sealed record WgsAdapterLoadResult(IReadOnlyList<IWgsGameAdapter> Adapters, IReadOnlyList<string> Assemblies,
    IReadOnlyList<string> Errors)
{
    public static WgsAdapterLoadResult Empty { get; } = new([], [], []);

    /// <summary>Registers every loaded adapter, recording (not throwing) a duplicate id. Returns the problems.</summary>
    public IReadOnlyList<string> RegisterInto(WgsGameAdapterRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var problems = new List<string>();
        foreach (var adapter in Adapters)
        {
            try { registry.Register(adapter); }
            catch (ArgumentException ex) { problems.Add(ex.Message); }
        }
        return problems;
    }
}

/// <summary>
/// Loads game adapters from assemblies on disk. Each assembly gets its own
/// <see cref="AssemblyLoadContext"/> so its private dependencies cannot clash with the host's or another
/// adapter's, while the <c>GamePassStorage</c> contract assembly is always the host's own copy, so the
/// adapter's <see cref="IWgsGameAdapter"/> is the same type the host uses.
///
/// <para><b>Trust model.</b> An adapter is ordinary .NET code that runs inside the host process with
/// the host's full permissions. There is no sandbox: loading an adapter is executing it. Load only
/// assemblies you built or trust, from folders only you can write to. The loader never downloads
/// anything, only reads the folders it is given, and reports a broken assembly as an error rather than
/// stopping.</para>
/// </summary>
public static class WgsAdapterLoader
{
    /// <summary>Environment variable naming adapter folders (separated by the platform path separator).</summary>
    public const string DirectoriesEnvironmentVariable = "WGS_ADAPTERS_DIR";

    /// <summary>The folders named by <see cref="DirectoriesEnvironmentVariable"/>, or none.</summary>
    public static IReadOnlyList<string> DirectoriesFromEnvironment()
    {
        var value = Environment.GetEnvironmentVariable(DirectoriesEnvironmentVariable);
        return string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>
    /// Loads adapters from every <c>*.dll</c> directly inside each folder and inside each of its immediate
    /// sub-folders (so a plugin can ship in its own folder with its dependencies). The contract assembly
    /// itself is skipped. A folder that does not exist is reported as an error.
    /// </summary>
    public static WgsAdapterLoadResult LoadFromDirectories(IEnumerable<string> directories)
    {
        ArgumentNullException.ThrowIfNull(directories);
        var adapters = new List<IWgsGameAdapter>();
        var assemblies = new List<string>();
        var errors = new List<string>();
        foreach (var directory in directories.Where(d => !string.IsNullOrWhiteSpace(d)))
        {
            if (!Directory.Exists(directory))
            {
                errors.Add($"Adapter folder '{directory}' does not exist.");
                continue;
            }
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(directory, "*.dll")
                    .Concat(Directory.EnumerateDirectories(directory).SelectMany(d => Directory.EnumerateFiles(d, "*.dll")))
                    .OrderBy(f => f, StringComparer.Ordinal).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"Adapter folder '{directory}' could not be read: {ex.Message}");
                continue;
            }
            foreach (var file in files)
            {
                var one = LoadFromAssembly(file);
                adapters.AddRange(one.Adapters);
                assemblies.AddRange(one.Assemblies);
                errors.AddRange(one.Errors);
            }
        }
        return new WgsAdapterLoadResult(adapters, assemblies, errors);
    }

    /// <summary>Loads the adapters one assembly declares: every public, non-abstract class implementing
    /// <see cref="IWgsGameAdapter"/> with a public parameterless constructor.</summary>
    public static WgsAdapterLoadResult LoadFromAssembly(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        var contract = typeof(IWgsGameAdapter).Assembly;
        if (string.Equals(Path.GetFileNameWithoutExtension(full), contract.GetName().Name, StringComparison.OrdinalIgnoreCase))
        {
            return WgsAdapterLoadResult.Empty;
        }
        var adapters = new List<IWgsGameAdapter>();
        var errors = new List<string>();
        try
        {
            var assembly = new AdapterLoadContext(full, contract).LoadManagedAssembly(full);
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t is not null).ToArray()!;
                errors.Add($"{Path.GetFileName(full)}: some types could not be loaded ({ex.LoaderExceptions.FirstOrDefault()?.Message}).");
            }
            foreach (var type in types.Where(t => t.IsClass && t.IsPublic && !t.IsAbstract
                && typeof(IWgsGameAdapter).IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) is not null))
            {
                try
                {
                    adapters.Add((IWgsGameAdapter)Activator.CreateInstance(type)!);
                }
                catch (Exception ex) when (ex is TargetInvocationException or MissingMethodException or TypeLoadException)
                {
                    errors.Add($"{Path.GetFileName(full)}: {type.Name} could not be created ({(ex.InnerException ?? ex).Message}).");
                }
            }
        }
        catch (BadImageFormatException)
        {
            // Not a managed assembly (a native dependency shipped beside the adapter): nothing to load.
            return WgsAdapterLoadResult.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileLoadException)
        {
            errors.Add($"{Path.GetFileName(full)} could not be loaded: {ex.Message}");
        }
        return new WgsAdapterLoadResult(adapters, adapters.Count > 0 ? [full] : [], errors);
    }

    private sealed class AdapterLoadContext(string mainPath, Assembly contract)
        : AssemblyLoadContext($"wgs-adapter:{Path.GetFileName(mainPath)}", isCollectible: false)
    {
        private readonly AssemblyDependencyResolver _resolver = new(mainPath);

        // Loading from a stream keeps Windows from locking plugin DLLs for the lifetime of the host.
        // The resolver still uses the original path to find the plugin's private dependencies.
        public Assembly LoadManagedAssembly(string path)
        {
            using var stream = File.OpenRead(path);
            return LoadFromStream(stream);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // The contract is shared with the host; everything else the adapter brings stays private to it.
            if (string.Equals(assemblyName.Name, contract.GetName().Name, StringComparison.OrdinalIgnoreCase))
            {
                return contract;
            }
            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadManagedAssembly(path);
        }
    }
}
