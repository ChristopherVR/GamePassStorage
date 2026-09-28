# Testing with an in-memory filesystem

Because the filesystem, clock and log are injected, you can test code that reads and writes wgs
stores without touching the disk. The library does not ship an in-memory filesystem, but
`IWgsFileSystem` is small. This one keys files by a `/`-normalised path and has an optional fault
hook for simulating locked files or an interrupted commit.

```csharp
using GamePassStorage;

public sealed class MemFs : IWgsFileSystem
{
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
    private readonly HashSet<string> _dirs = new(StringComparer.Ordinal);

    /// <summary>Called before each operation with (operation, path); a returned exception is thrown.</summary>
    public Func<string, string, Exception?>? Fault { get; set; }

    private static string N(string p) => p.Replace('\\', '/').TrimEnd('/');
    private void Check(string op, string path) { if (Fault?.Invoke(op, N(path)) is { } ex) throw ex; }

    public bool FileExists(string path) => Files.ContainsKey(N(path));
    public bool DirectoryExists(string path)
        => _dirs.Contains(N(path)) || Files.Keys.Any(k => k.StartsWith(N(path) + "/", StringComparison.Ordinal));

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

    public void WriteAllBytes(string path, byte[] bytes) { Check("WriteAllBytes", path); Files[N(path)] = bytes.ToArray(); }

    public void MoveOverwrite(string source, string destination)
    {
        Check("MoveOverwrite", destination);
        if (!Files.Remove(N(source), out var b)) throw new FileNotFoundException(source);
        Files[N(destination)] = b;
    }

    public void DeleteFile(string path) { Check("DeleteFile", path); Files.Remove(N(path)); }
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
            .Select(k => k[prefix.Length..]).Where(r => r.Contains('/'))
            .Select(r => prefix + r.Split('/')[0]).Distinct().ToList();
    }

    public long GetFileLength(string path)
        => Files.TryGetValue(N(path), out var b) ? b.Length : throw new FileNotFoundException(path);

    public DateTime GetLastWriteTimeUtc(string path) => new(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
}
```

## Create a store in memory

`WgsStore.WriteNewContainer` builds a single-container store through whatever filesystem you
give it:

```csharp
const string Root = "/store";
var fs = new MemFs();
var clock = new FixedClock(new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero));
var options = new WgsStoreOptions { FileSystem = fs, Clock = clock };

WgsStore.WriteNewContainer(Root, "World-WC", new byte[] { 1, 2, 3 }, "Test.Game_abc!App", options);
var store = WgsStore.Open(Root, options);
```

## Test a write

```csharp
[Fact]
public void Write_advances_the_index_and_marks_the_container_created()
{
    // arrange: `store`, `fs` and `clock` as above
    var before = store.IndexFileTime;

    var commit = store.TryWriteBlob(store.Containers[0], new byte[] { 9, 9, 9 });

    Assert.Equal(WgsOperationStatus.Ok, commit.Status);
    Assert.True(store.IndexFileTime > before);
    Assert.Equal(WgsEntryState.Created, store.Containers[0].State);   // no ETag, so it stays Created
    Assert.Equal(new byte[] { 9, 9, 9 }, store.ReadBlob(store.Containers[0]));
}
```

## Inject faults

Return an exception from the `Fault` hook to check that you handle it. An
`UnauthorizedAccessException`, or an `IOException` whose HResult low word is 32 or 33 (Windows
sharing and lock violations), surfaces as `LockConflict` (see `WgsStore.IsLockConflict`). Other
IO errors surface as `Failed`:

```csharp
fs.Fault = (op, path) => op == "MoveOverwrite" && path.EndsWith("containers.index", StringComparison.Ordinal)
    ? new UnauthorizedAccessException("Access to the path is denied.")
    : null;

var commit = store.TryWriteBlob(store.Containers[0], new byte[] { 1 });
Assert.Equal(WgsOperationStatus.LockConflict, commit.Status);
```

## Simulate a concurrent change

Rewrite `containers.index` in the in-memory filesystem after opening the store, then write:

```csharp
fs.Files["/store/containers.index"] = ChangedIndexBytes;   // any different bytes
var commit = store.TryWriteBlob(store.Containers[0], new byte[] { 1 });
Assert.Equal(WgsOperationStatus.ConcurrentChange, commit.Status);
```

## What these tests prove

::: warning
In-memory tests exercise your API boundary with synthetic stores. They are not evidence that a
particular game's layout is supported. For that you need real, sanitized stores: see
[Supported titles](/supported-titles#contributing-fixtures).
:::

For a fixed clock, a list-backed log and a stub gate, the library's own tests are a good model:
[`WgsStoreTests.cs`](https://github.com/ChristopherVR/GamePassStorage/blob/main/tests/GamePassStorage.Tests/WgsStoreTests.cs).
