# API reference

Everything lives in the `GamePassStorage` namespace of the `GamePassStorage` package. These pages
are written by hand from the public types; the signatures match the source.

| Page | Types |
| --- | --- |
| [WgsStore](/api/wgs-store) | The store: opening, reading, writing, multi-blob containers, delete, restore, export/import, repair, orphans, diagnosis |
| [Containers and state](/api/containers) | `WgsContainer`, `WgsEntryState`, `WgsSyncState`, `WgsOrphanedContainer`, `WgsManifestInfo`, `WgsBlobEntry`, `WgsBlobInfo`, delete and export/import types |
| [Results and status codes](/api/results) | `WgsOpenStatus`, `WgsOperationStatus`, `WgsOpenResult`, `WgsReadResult`, `WgsCommitResult`, `WgsChangeReport`, `WgsWritePlan`, `WgsDiagnosis`, `WgsWriteRefusedException` |
| [Write gates and assessments](/api/write-gates) | `IWgsWriteGate`, `WgsWriteGates` (including the process-aware gate), `IWgsProcessLister`, `WgsWriteAssessment`, `WgsWriteConcern` |
| [Injected services](/api/services) | `WgsStoreOptions`, `IWgsFileSystem`, `IWgsClock`, `IWgsLog`, `IWgsBlobInspector`, `WgsBlobDescription` |
| [Snapshots](/api/snapshots) | `WgsSnapshot`, `WgsContainerState` |
| [Store discovery](/api/discovery) | `WgsStoreDiscovery`, `WgsStoreLocation`, `WgsStoreDiscoveryOptions` |
| [Game adapters](/api/adapters) | `IWgsGameAdapter`, `IWgsPayloadCodec`, `WgsContentDescription`, `GenericWgsAdapter`, `WgsGameAdapterRegistry`, `WgsAdapterLoader` |

For the meaning of the bytes behind these types, see the [format reference](/wgs-format). For
task-oriented explanations, start with [Getting started](/guide/getting-started).
