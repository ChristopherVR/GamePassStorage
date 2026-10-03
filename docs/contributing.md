# Contributing

Contributions are welcome: bug reports, sanitized fixtures from other titles, format research, and
pull requests.

## Build and test

Requires the .NET 10 SDK.

```console
git clone https://github.com/ChristopherVR/GamePassStorage
cd GamePassStorage
dotnet build GamePassStorage.slnx
dotnet test  GamePassStorage.slnx
```

CI runs the tests on Ubuntu and Windows and packs every package on every push to `main` and every
pull request.

## Layout

| Path | What lives there |
| --- | --- |
| `src/GamePassStorage` | The library: `WgsStore`, types, services, snapshots. No package dependencies. |
| `src/GamePassStorage.Adapters.<Game>` | One game adapter per title, its own NuGet package (today `AbioticFactor`). References only the library. |
| `src/GamePassStorage.Tool` | The `wgs` command-line tool (`WgsCli`). It references the adapters it ships with (`BuiltInAdapters.cs`). |
| `tests/GamePassStorage.Tests` | xUnit tests against an in-memory filesystem, plus CLI, adapter and plugin-loading tests. |
| `tests/fixtures` | Small sanitized fixtures. See its README for the policy. |
| `docs` | This site. |

## Ground rules

- The library stays **game-agnostic** and dependency-free. Game-specific behaviour belongs in a
  [game adapter](/guide/adapters) project (`src/GamePassStorage.Adapters.<Game>`), not in the library.
  The generic model must keep working for every title an adapter does not serve.
- Keep the [safety rules](/guide/safety): never mint ETags, never clear the unresolved-conflict
  flag, keep the blob, manifest, index write order, and keep the index timestamp strictly
  advancing.
- Prefer typed results over exceptions for anything an app might reasonably hit.
- Add a test with any behaviour change. New format claims need evidence: say where it came from.
- Do not use em dash characters in code, docs or commit messages.
- Use Conventional Commits style for commit messages (`feat:`, `fix:`, `docs:`).

## Adding a game adapter

A new title is a new project following the pattern of `GamePassStorage.Adapters.AbioticFactor`. The
short version (the [adapter guide](/guide/adapters#adding-a-new-adapter) has the detail):

1. **Project layout.** `src/GamePassStorage.Adapters.<Game>/` with a `PackageId`
   `GamePassStorage.Adapters.<Game>`, a packed `README.md`, and a reference to `GamePassStorage` only.
   Add it to `GamePassStorage.slnx`.
2. **Adapter.** A public class with a public parameterless constructor implementing `IWgsGameAdapter`,
   plus a static `Instance`. Game logic goes in small testable classes beside it. It must describe and
   gate, never write, and never throw for ordinary input.
3. **Built-in set.** To ship it inside `wgs`, add a `ProjectReference` in
   `src/GamePassStorage.Tool/GamePassStorage.Tool.csproj` and list it in `BuiltInAdapters.cs`. An
   adapter that is not accepted as a built-in still works as a plugin (`--adapters`).
4. **Pipelines.** CI and publishing pack `GamePassStorage.slnx`, so a packable adapter added to
   the solution is included automatically. Publishing requires passing tests on Windows and Linux.
5. **Tests.** Matching, classification, every parser on real or synthetic payloads (including
   truncated input), inspector, gate with an injected process lister, resolution against other
   adapters, loading the built DLL as a plugin, and `wgs inspect` output.
6. **Fixtures policy.** Prefer synthetic payloads. A real payload must be a small (under 2 MB),
   sanitized store with synthetic ids and no personal data, recorded in `tests/fixtures/README.md`.
   A fixture proves a layout for its own title only.
7. **Docs.** A row in [Supported titles](/supported-titles) with the evidence.

## Working on these docs

The site is VitePress, in `docs/`:

```console
cd docs
npm ci
npm run docs:dev       # live preview at http://localhost:5173/GamePassStorage/
npm run docs:build     # production build; fails on dead links
```

Every page has an "Edit this page on GitHub" link. The API pages are written by hand from the
public types, so update them when a public signature changes. The site deploys automatically from
`main` through `.github/workflows/pages.yml` when anything under `docs/` changes.

::: info Repository setting
GitHub Pages must be set to deploy from **GitHub Actions** (Settings, Pages, Build and deployment,
Source) for the workflow to publish the site.
:::

## Reporting a problem

Open an issue with the title, the platform (Xbox app or Microsoft Store), the output of
`wgs diagnose <store> --json`, and, if you can, a sanitized copy of the store. See
[Supported titles](/supported-titles#contributing-fixtures).
