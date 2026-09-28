# Releasing

Pushing a version tag publishes both packages, `GamePassStorage` and `GamePassStorage.Tool`, to
nuget.org and creates a GitHub release.

```console
git tag v0.1.0
git push origin v0.1.0
```

The workflow is `.github/workflows/publish.yml` and triggers on tags matching `v*.*.*`.

## What it does

1. Resolves the version from the tag (`v0.1.0` becomes `0.1.0`) and checks that it is a semantic
   version.
2. Runs the tests in Release configuration.
3. Packs the library, the adapter packages and the tool with that version.
4. Signs in to nuget.org with trusted publishing (OIDC) and pushes `artifacts/*.nupkg` with `--skip-duplicate`.
5. Uploads the packages as a workflow artifact.
6. For tag pushes, creates a GitHub release with generated notes and the packages attached.

## Requirements

| Requirement | Detail |
| --- | --- |
| Trusted publishing policy | On nuget.org, add a trusted publishing policy for repository `ChristopherVR/GamePassStorage` and workflow file `publish.yml`. No API key is stored in GitHub: the workflow exchanges its short-lived GitHub OIDC token for a temporary nuget.org key (`NuGet/login`). |
| `NUGET_USER` (optional) | A repository variable with the nuget.org account name that owns the policy. Defaults to the repository owner. |
| A semantic version | `MAJOR.MINOR.PATCH`, optionally with a pre-release suffix such as `-beta.1`. |

## Running it by hand

The workflow can also be run from the Actions tab (`workflow_dispatch`) with a **version** input.
That publishes to nuget.org but does not create a GitHub release.

## Docs deploys are separate

Publishing packages does not deploy this site. The site deploys from `main` when `docs/` changes;
see [Contributing](/contributing#working-on-these-docs).
