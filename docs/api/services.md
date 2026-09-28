# Injected services

## WgsStoreOptions

```csharp
public sealed class WgsStoreOptions
{
    public static WgsStoreOptions Default { get; }

    public IWgsFileSystem FileSystem { get; init; }      // PhysicalWgsFileSystem.Instance
    public IWgsClock Clock { get; init; }                // SystemWgsClock.Instance
    public IWgsLog Log { get; init; }                    // NullWgsLog.Instance
    public IWgsBlobInspector? BlobInspector { get; init; } // optional
    public IWgsWriteGate? WriteGate { get; init; }       // null means WgsWriteGates.Structural
}
```

Every member has a real default. Pass an instance to `WgsStore.Open`, `TryOpen`,
`FindOrphanedContainers`, `HasOrphanedWorldFolders` or `WriteNewContainer`.

## IWgsFileSystem

```csharp
public interface IWgsFileSystem
{
    bool FileExists(string path);
    bool DirectoryExists(string path);
    byte[] ReadAllBytes(string path);
    byte[] ReadHead(string path, int count);   // up to count bytes from the start of a file
    void WriteAllBytes(string path, byte[] bytes);
    void MoveOverwrite(string source, string destination);   // replaces the destination
    void DeleteFile(string path);
    void CreateDirectory(string path);
    IEnumerable<string> EnumerateFiles(string directory);
    IEnumerable<string> EnumerateDirectories(string directory);
    long GetFileLength(string path);
    DateTime GetLastWriteTimeUtc(string path);
}
```

The filesystem operations the container layer needs. `MoveOverwrite` should be a same-volume atomic
replace where the OS offers one, because the library writes the index and manifests through a temp
file plus this move. A complete in-memory implementation is in [Testing](/guide/testing).

### PhysicalWgsFileSystem

```csharp
public sealed class PhysicalWgsFileSystem : IWgsFileSystem
{
    public static PhysicalWgsFileSystem Instance { get; }
}
```

The real filesystem.

## IWgsClock

```csharp
public interface IWgsClock { DateTimeOffset UtcNow { get; } }

public sealed class SystemWgsClock : IWgsClock { public static SystemWgsClock Instance { get; } }
```

The source of "now" for index and entry timestamps. The index timestamp still strictly advances
even if the clock goes backwards.

## IWgsLog

```csharp
public interface IWgsLog
{
    void Info(string message);
    void Warn(string message);
}

public sealed class NullWgsLog : IWgsLog { public static NullWgsLog Instance { get; } }
```

A diagnostic sink. Implementations must never throw. `NullWgsLog` discards everything.

## IWgsBlobInspector

```csharp
public interface IWgsBlobInspector
{
    int HeadBytes { get; }
    WgsBlobDescription? Inspect(ReadOnlySpan<byte> head);
}
```

Game adapter hook that recognises a game's payload from the first bytes of a blob without decoding
it. `HeadBytes` is how many leading bytes `Inspect` needs. `Inspect` returns `null` when the
payload is not one the adapter recognises. See [Writing a game adapter](/guide/adapter).

### WgsBlobDescription

```csharp
public sealed record WgsBlobDescription(string? Label, string? SuggestedContainerName);
```

`Label` is a human-meaningful identity (for instance the world name), or null.
`SuggestedContainerName` is the container name to register it under, or null.
