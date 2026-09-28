---
layout: home
title: GamePassStorage
hero:
  name: GamePassStorage
  text: Xbox Connected Storage, done carefully
  tagline: A game-agnostic .NET 10 library and the wgs tool for reading and safely writing the save folders Game Pass PC titles use.
  actions:
    - theme: brand
      text: Get started
      link: /guide/getting-started
    - theme: alt
      text: Why it is careful
      link: /guide/safety
    - theme: alt
      text: View on GitHub
      link: https://github.com/ChristopherVR/GamePassStorage
features:
  - title: Container layer only
    details: Handles containers.index, container.N manifests and GUID blobs. What is inside a blob belongs to your game and plugs in through small adapter interfaces.
    link: /guide/adapter
    linkText: Write an adapter
  - title: Built around cloud sync
    details: ETags are echoed, never minted. The index timestamp always advances. Unresolved conflicts are never hidden. Writes go blob, then manifest, then index.
    link: /guide/safety
    linkText: Read the safety model
  - title: No package dependencies
    details: Filesystem, clock and logging are injected, so a store can run against an in-memory filesystem or a fault-injecting one in your own tests.
    link: /guide/testing
    linkText: Test your code
  - title: A command-line tool
    details: List, diagnose, extract, back up, snapshot, compare and put, with a backup required before any write.
    link: /cli/
    linkText: CLI reference
  - title: Documented format
    details: Byte layouts, state and sync-flag meanings, read and write rules, sources, and an honest list of what is still unverified.
    link: /wgs-format
    linkText: Format reference
  - title: Typed results
    details: Lock conflicts, concurrent changes, in-flight syncs and unsupported layouts come back as status values you can act on, not exceptions to guess at.
    link: /guide/troubleshooting
    linkText: Every status explained
---

## Install

```console
dotnet add package GamePassStorage           # the library
dotnet tool install -g GamePassStorage.Tool  # the `wgs` command-line tool
```

## A first look

```csharp
using GamePassStorage;

var opened = WgsStore.TryOpen(storeFolder);
if (!opened.Succeeded) { Console.WriteLine(opened.Message); return; }

foreach (var c in opened.Store!.Containers)
    Console.WriteLine($"{c.Name}  {c.State}  {c.BlobSize} bytes");
```

```console
wgs list "C:\Users\me\AppData\Local\Packages\<family>\SystemAppData\wgs"
wgs diagnose <store>
```

::: warning Close the game before you write
Local file access does not give control over Xbox cloud sync. Close the game (and ideally go
offline) before writing, and let the title upload the change on its next launch.
:::

GamePassStorage was extracted from [Abiotic Editor](https://github.com/ChristopherVR/AbioticEditor).
Abiotic Factor is the only title verified so far; see [Supported titles](/supported-titles).
