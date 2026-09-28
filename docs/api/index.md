# API reference

Everything lives in the `GamePassStorage` namespace of the `GamePassStorage` package. These pages
are written by hand from the public types; the signatures match the source.

| Page | Types |
| --- | --- |
| [WgsStore](/api/wgs-store) | The store: opening, reading, writing, repair, orphans, diagnosis |
| [Containers and state](/api/containers) | `WgsContainer`, `WgsEntryState`, `WgsSyncState`, `WgsOrphanedContainer`, `WgsManifestInfo` |
| [Results and status codes](/api/results) | `WgsOpenStatus`, `WgsOperationStatus`, `WgsOpenResult`, `WgsReadResult`, `WgsCommitResult`, `WgsChangeReport`, `WgsWritePlan`, `WgsDiagnosis`, `WgsWriteRefusedException` |
| [Write gates and assessments](/api/write-gates) | `IWgsWriteGate`, `WgsWriteGates`, `WgsWriteAssessment`, `WgsWriteConcern` |
| [Injected services](/api/services) | `WgsStoreOptions`, `IWgsFileSystem`, `IWgsClock`, `IWgsLog`, `IWgsBlobInspector`, `WgsBlobDescription` |
| [Snapshots](/api/snapshots) | `WgsSnapshot`, `WgsContainerState` |

For the meaning of the bytes behind these types, see the [format reference](/wgs-format). For
task-oriented explanations, start with [Getting started](/guide/getting-started).

::: info Coming soon
Multi-blob containers, store discovery, delete, restore, export/import and a process-aware write
gate are in development. Their types and members will be documented here once they land.
:::
