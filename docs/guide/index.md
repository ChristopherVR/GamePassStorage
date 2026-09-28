# Introduction

GamePassStorage is a .NET 10 library for reading and writing **Xbox Connected Storage ("wgs")**
save folders, the format Game Pass (Microsoft Store / Xbox app) PC titles use for their saves.
It also ships `wgs`, a command-line tool over the same library.

## What it does

A wgs store is a folder holding a `containers.index` that maps logical container names to
GUID-named folders. Each folder holds a `container.N` manifest naming a GUID-named blob file. The
library handles that container layer:

- parsing and rewriting `containers.index` and the `container.N` manifests,
- ETag and sync-state rules,
- snapshots and comparisons, to observe what a cloud sync did,
- orphan discovery and re-registration,
- write ordering and atomic file replacement.

## What it does not do

It never interprets a blob. Compression, bundles and save classes belong to the game, so your
code (or a game-specific library) handles them. The library only sees opaque bytes, plus an
optional [`IWgsBlobInspector`](/guide/adapter#recognising-payloads-iwgsblobinspector) that can label a
blob from its first bytes.

## When to use it

Use it when you are writing a save editor, backup tool, converter or research tool for a Game Pass
PC title and need to get at the save bytes safely. Reach for the `wgs` tool when you want to look
at a store, take a backup, or compare before and after a cloud sync without writing any code.

It is not a good fit if the game is a Steam title (there is no wgs folder), or if you need to
control Xbox cloud sync itself (that cannot be driven from outside the title).

## Where to go next

1. [Getting started](/guide/getting-started): open a store, read a blob, write it back.
2. [Safety model](/guide/safety): what the library enforces and what only you can do.
3. [Writing a game adapter](/guide/adapter): plug in payload recognition and extra write checks.
4. [Testing](/guide/testing): run stores against an in-memory filesystem.
5. [Troubleshooting](/guide/troubleshooting): every status value and what to do about it.
