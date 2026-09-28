namespace GamePassStorage.Tests;

internal sealed class FixedClock(DateTimeOffset now) : IWgsClock
{
    public DateTimeOffset Now { get; set; } = now;
    public DateTimeOffset UtcNow => Now;
}

/// <summary>An in-memory filesystem with an optional fault hook, keyed by '/'-normalised path.</summary>
internal sealed class MemFs : IWgsFileSystem
{
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
    private readonly HashSet<string> _dirs = new(StringComparer.Ordinal);

    /// <summary>Called before each operation with (operation, path); a returned exception is thrown.</summary>
    public Func<string, string, Exception?>? Fault { get; set; }

    private static string N(string p) => p.Replace('\\', '/').TrimEnd('/');

    private void Check(string op, string path)
    {
        if (Fault?.Invoke(op, N(path)) is { } ex) throw ex;
    }

    public Dictionary<string, string> Snapshot()
        => Files.ToDictionary(f => f.Key, f => Convert.ToHexString(f.Value));

    public bool FileExists(string path) => Files.ContainsKey(N(path));
    public bool DirectoryExists(string path) => _dirs.Contains(N(path)) || Files.Keys.Any(k => k.StartsWith(N(path) + "/", StringComparison.Ordinal));

    public byte[] ReadAllBytes(string path)
    {
        Check("ReadAllBytes", path);
        return Files.TryGetValue(N(path), out var b) ? b.ToArray() : throw new FileNotFoundException(path);
    }

    public byte[] ReadHead(string path, int count)
    {
        Check("ReadHead", path);
        return Files.TryGetValue(N(path), out var b) ? b.Take(count).ToArray() : throw new FileNotFoundException(path);
    }

    public void WriteAllBytes(string path, byte[] bytes)
    {
        Check("WriteAllBytes", path);
        Files[N(path)] = bytes.ToArray();
    }

    public void MoveOverwrite(string source, string destination)
    {
        Check("MoveOverwrite", destination);
        if (!Files.Remove(N(source), out var b)) throw new FileNotFoundException(source);
        Files[N(destination)] = b;
    }

    public void DeleteFile(string path)
    {
        Check("DeleteFile", path);
        Files.Remove(N(path));
    }

    public void CreateDirectory(string path) => _dirs.Add(N(path));

    public IEnumerable<string> EnumerateFiles(string directory)
    {
        var prefix = N(directory) + "/";
        return Files.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal) && !k[prefix.Length..].Contains('/')).ToList();
    }

    public IEnumerable<string> EnumerateDirectories(string directory)
    {
        var prefix = N(directory) + "/";
        return Files.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
            .Select(k => k[prefix.Length..])
            .Where(r => r.Contains('/'))
            .Select(r => prefix + r.Split('/')[0])
            .Distinct().ToList();
    }

    public long GetFileLength(string path)
        => Files.TryGetValue(N(path), out var b) ? b.Length : throw new FileNotFoundException(path);

    public DateTime GetLastWriteTimeUtc(string path) => new(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
}
