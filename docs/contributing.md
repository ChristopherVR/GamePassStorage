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

CI runs the tests on Ubuntu and Windows and packs both packages on every push to `main` and every
pull request.

## Layout

| Path | What lives there |
| --- | --- |
| `src/GamePassStorage` | The library: `WgsStore`, types, services, snapshots. No package dependencies. |
| `src/GamePassStorage.Tool` | The `wgs` command-line tool (`WgsCli`). |
| `tests/GamePassStorage.Tests` | xUnit tests against an in-memory filesystem, plus CLI tests. |
| `docs` | This site. |

## Ground rules

- The library stays **game-agnostic** and dependency-free. Game-specific behaviour belongs in an
  adapter ([`IWgsBlobInspector`](/guide/adapter), [`IWgsWriteGate`](/guide/adapter#adding-checks-iwgswritegate)),
  not in the library.
- Keep the [safety rules](/guide/safety): never mint ETags, never clear the unresolved-conflict
  flag, keep the blob, manifest, index write order, and keep the index timestamp strictly
  advancing.
- Prefer typed results over exceptions for anything an app might reasonably hit.
- Add a test with any behaviour change. New format claims need evidence: say where it came from.
- Do not use em dash characters in code, docs or commit messages.
- Use Conventional Commits style for commit messages (`feat:`, `fix:`, `docs:`).

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
