# Chunk 7 review

Status: PASSED. This document records the read-only baseline, the bounded efficiency review, and the approved remediation.

## Review classification

The review recorded eight findings: three approved, four rejected, and one deferred. The full receipt is local validation receipts.

| ID / class | Affected area | Rationale, impact, validation |
|---|---|---|
| A1 approve | `AIContextApplication.cs: ResolveWriteScopeAsync` | Reuses validated project; one lookup removed; behavior/security/compatibility unchanged; Core 40/40. |
| A2 approve | `AIContextApplication.cs: FitBootstrap` | Removes idempotent assignment; output/freshness unchanged; Core 40/40. |
| A3 approve | `AIContextApplication.cs: ValidateHandoffBytes` | Reuses UTF-8 `Fits`; exact limit/error preserved; Unicode test, Core 40/40. |
| R1 reject | `src/AIContextMCP.Core/StorageContracts.cs`, `ApplicationContracts.cs` | Flattening risks compatibility and Core/SQLite separation; no change. |
| R2 reject | `src/AIContextMCP.Core/RepositoryBoundary.cs`, `MetadataReferenceValidator.cs`, `src/AIContextMCP.Storage.Sqlite/SqliteContextStorage.cs` | Distinct rules protect traversal/reparse boundaries; boundary tests retained. |
| R3 reject | `src/AIContextMCP.Storage.Sqlite/SqliteContextStorage.cs` | Generic indirection adds no benefit; Storage 32/32. |
| R4 reject | `src/AIContextMCP.Server/McpToolAdapter.cs`, Core/storage validators, `tests/*` lifecycle helpers | Layered trust boundaries and lifecycle assertions are intentional; Server 10/10. |
| D1 defer | `src/AIContextMCP.Server/McpToolAdapter.cs`, `src/AIContextMCP.Core/AIContextApplication.cs`, `RepositoryBoundary.cs` | Requires compatibility evidence or measured need; no change. |

Approved changes, all in `src/AIContextMCP.Core/AIContextApplication.cs`:

1. Reuse the first validated project in `ResolveWriteScopeAsync`, removing a duplicate lookup.
2. Remove the repeated `LatestValidation` compaction assignment.
3. Use the existing UTF-8 `Fits` serializer path for handoff aggregate validation, removing an intermediate string and the unused `System.Text` import.

The approved changes preserve validation, ownership checks, Git inspection, persistence, security, compatibility, and the eight dotted MCP tool names. The four rejected findings proposed flattening public interfaces, merging distinct path validators, generic SQL getter indirection, or removing layered trust-boundary validation/logging and fixture lifecycle safeguards. The deferred finding covers broader adapter/Core overlap, Git configuration passes, performance tuning, and public diagnostic/legacy API removal; each requires compatibility evidence or a measured need.

## Validation receipts

Fresh baseline: restore passed; Release build passed with 0 warnings/errors; Release tests passed 82/82 (Core 40, Storage 32, Server 10). The post-change focused Core test passed 40/40. Final restore, Release build, and Release tests passed with the same 82/82 result. Receipts are under local validation receipts and local validation receipts.

The handoff test now verifies that a field-length-valid escaped Unicode payload is rejected by the serialized aggregate limit with `ContentTooLarge` and the exact serialized-limit message. Existing ASCII accepted behavior remains.

## Scope and metrics

The solution has three source projects, three test projects, 22 source files, 36 lexical classes, 13 interfaces, 14 `PackageReference` elements, six unique packages across the solution, and three unique runtime packages. There are 12 test source files and 11 baseline / 12 final documentation files. No scripts, config, or schema scaffolding was added. The workspace has no Git metadata, so branch, HEAD, status, and diff receipts are unavailable; SHA-256 manifests under local validation receipts provide the source/tests/docs comparison. Build lock recovery preserved assemblies as `.chunk7-old` files in the Server output directory; live processes were not terminated.

The baseline SHA-256 manifests identify changed files, but the original source content and a no-index diff could not be certified. The attempted reconstruction at local validation receipts is unverified and must not be treated as an original backup. Source change review used the observed original and final regions; exact byte/line delta is unavailable.

## Dependencies

`Microsoft.Extensions.Hosting` 10.0.0 is directly used by Server host composition; `ModelContextProtocol` 2.2.0 is directly used by Server and Server.Tests for the stdio MCP adapter; `Microsoft.Data.Sqlite` 10.0.11 is directly used by Storage and Storage.Tests. Test-only direct packages are `Microsoft.NET.Test.Sdk` 17.14.1, `xunit` 2.9.3, and `xunit.runner.visualstudio` 3.0.2. Core has no package references. The fresh NuGet scan reported no known vulnerabilities and reported available updates; upgrades were deferred because no issue requires them. No package is removed or claimed supported solely from the advisory result. Official lifecycle reference: [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy); package references were checked against [ModelContextProtocol](https://www.nuget.org/packages/ModelContextProtocol/) and [xUnit releases](https://xunit.net/releases/).

| Package | Installed | Available in fresh NuGet scan | Required use |
|---|---|---|---|
| Microsoft.Extensions.Hosting | 10.0.0 | 10.0.12 | Server host and DI composition |
| ModelContextProtocol | 2.2.0 | No update reported | Server protocol/transport and wire tests |
| Microsoft.Data.Sqlite | 10.0.11 | 10.0.12 | SQLite provider and storage tests |
| Microsoft.NET.Test.Sdk | 17.14.1 | 18.9.0 | Test discovery and execution |
| xunit | 2.9.3 | No update reported | Assertions and test cases |
| xunit.runner.visualstudio | 3.0.2 | 4.0.0 | VSTest integration |

All six dependencies have direct code or test-infrastructure uses. Some test references could be supplied transitively, but retaining explicit dependencies avoids coupling tests to another project's package graph. No standard-library replacement supplies the SQLite provider, MCP transport, or test framework/runner; Hosting supplies the native .NET host composition used here. No package deletion is justified. Available upgrades above are deferred maintenance, not implemented efficiency changes. Exact individual-package support lifecycle was not established; the advisory scan is not support certification. Sibling package alignment does not justify changes to either server; coexistence is validated separately.

## Security and MCP checks

| Test coverage | Evidence |
|---|---|
| Roots, traversal, UNC, reparse, dirty/unknown | `tests/AIContextMCP.Core.Tests/GitBoundaryTests.cs` |
| SQL literals, secrets, content limits | `tests/AIContextMCP.Storage.Sqlite.Tests/Chunk6AbuseTests.cs` |
| Scoped ownership and migrations | `tests/AIContextMCP.Storage.Sqlite.Tests/SqliteContextStorageTests.cs` |
| Request IDs, replay, rollback | `tests/AIContextMCP.Storage.Sqlite.Tests/MutationReceiptTests.cs` |
| Strict MCP input/output | `tests/AIContextMCP.Server.Tests/McpContractTests.cs` |
| Stdio snapshots, cursors, errors, restart, freshness | `tests/AIContextMCP.Server.Tests/McpRuntimeIntegrationTests.cs` |
| Unicode serialized handoff limit and validation retrieval | `tests/AIContextMCP.Core.Tests/ApplicationCoreTests.cs` |

All rows reuse the final 82/82 result. The eight existing dotted tool names are `project.bootstrap`, `context.search`, `context.record`, `decision.list`, `decision.record`, `finding.record`, `test.record`, and `handoff.create`. The request used underscore spellings; the existing dotted wire names remain unchanged. Rejected/deferred behavior, security, compatibility, and tests are unchanged because those proposals were not implemented. The review retained the source-of-truth order, persistence and recovery boundaries, and all eight tools. Runtime logs and the production database were not committed; no pilot or client configuration was changed.

## Known limitations

The controlled repository `<approved-root>\AIContextMCP-Test` is clean at `master` / `05df5ac31797a7a5889d02358b87749675cfe74b`, but contains only its README; controlled records live in the server database, not that repository. The controlled stdio regression passed as recorded below. Historical `README.md`/`CONTEXT_MODEL.md` wording and the roadmap's premature Chunk 7 status were not rewritten. The exact historical “READY FOR CHUNK 7” marker is absent; accepted Chunk 6 receipts and the fresh 82-test baseline establish the available prerequisite evidence.

Controlled validation passed: receipt local validation receipts records two fresh stdio processes with EOF exit 0, eight strict schemas from the actual catalog, stable project `7b346600-0b9a-4689-9ae5-e9e82ce5b9d4` and repository `ce67abdb-0180-4745-84f7-288288c8a913`, clean `master` matching HEAD `05df5ac31797a7a5889d02358b87749675cfe74b` before and after, and retrieval of context, decision, finding, TestRun `bc37ba53-f4be-43c8-94f8-b31060dd5b73`, and handoff. Validation was `10/0/0 Current`; database counts were one each for context/decision/test/finding/handoff, 262144 bytes, and identical SHA-256 with no growth or repository writes. Sibling `dotnet_project` inspection succeeded while owned PID 44100 was alive; no owned processes remain and pre-existing processes were untouched. Sibling evidence is local validation receipts. Dirty/stale transitions are covered by isolated final Server/Core tests. Independent review approved the three source cuts and Unicode assertion with no blocking security findings.

PASS - READY FOR CHUNK 8

Chunk 8 has not begun.
