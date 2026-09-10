# Chunk 6 Security Tests

## Executed matrix

| TEST | EXPECTED RESULT | IMPLEMENTED TEST LOCATION | RESULT |
|---|---|---|---|
| Repository metadata and working-tree boundary | Supported direct Git metadata is read through verified handles. Unsafe paths, reparse points, linked metadata, unsupported index/object forms, sensitive paths, and namespace changes fail closed or return Unknown. | `tests/AIContextMCP.Core.Tests/GitBoundaryTests.cs` — metadata, snapshot, fingerprint, sensitive-path, and unsupported-layout cases. | Covered by the final Core-project result: 33/33 passed. The project total is not attributed to this class alone. |
| Repository artifact boundary | A regular relative artifact is hashed; traversal and reparse targets are rejected. | `tests/AIContextMCP.Core.Tests/GitBoundaryTests.cs` — `Artifact_boundary_hashes_only_a_verified_regular_relative_file`. | Passed within the final Core-project result: 33/33. |
| SQLite migration, ownership, and keyset persistence | Schema migration preserves data; scoped observations and references reject invalid owners; legacy rows remain deterministically pageable. | `tests/AIContextMCP.Storage.Sqlite.Tests/SqliteContextStorageTests.cs` — migration, phase/context metadata, and keyset cases. | Passed within the final Storage-project result: 32/32. The project total is not attributed to this class alone. |
| Bounded lookahead and test-count limits | Internal keyset lookahead accepts 101 rows while the public page limit remains 100; three one-billion count fields form a valid failed test result with a three-billion total. | `tests/AIContextMCP.Storage.Sqlite.Tests/SqliteContextStorageTests.cs` — `McpKeysetLookaheadAllowsOneInternalRowBeyondThePublicPage` and `RejectsMalformedIdentifiersFieldsEnumsCommitsTimestampsAndOversizedQueries`. | Passed within the final Storage-project result: 32/32. |
| Durable mutation replay and concurrency | Same key and payload replay one receipt, a changed payload conflicts, failures and cancellation roll back record and receipt, and concurrent instances apply once. | `tests/AIContextMCP.Storage.Sqlite.Tests/MutationReceiptTests.cs`. | Five cases passed within the final Storage-project result: 32/32. |
| SQL literals, bounds, and credential-shaped input | SQL metacharacters remain literal, exact bounds are accepted, and synthetic credential-shaped values are rejected before persistence. | `tests/AIContextMCP.Storage.Sqlite.Tests/Chunk6AbuseTests.cs`. | Three cases passed within the final Storage-project result: 32/32. |
| MCP schemas and catalog | Strict JSON rejects unknown fields, invalid numeric shapes, and combined enum names; the catalog publishes exactly eight typed strict input and output schemas. | `tests/AIContextMCP.Server.Tests/McpContractTests.cs`. | Seven contract cases passed within the final Server-project result: 10/10. |
| MCP runtime, freshness, and raw wire guard | Malformed calls do not end the session; writes and replay survive restart; stale Git state is reported; forged reference scope/type/hash claims fail; raw duplicate, oversize, and unsafe-ID frames receive safe errors; EOF exits cleanly. | `tests/AIContextMCP.Server.Tests/McpRuntimeIntegrationTests.cs` — `Stdio_runtime_preserves_replay_and_reports_repository_freshness`. | One live process integration case passed within the final Server-project result: 10/10. |

The final Release shell run records Core 33/33, Storage 32/32, and Server 10/10: 75/75 passing, with no failures or skips. The 100-row public context-search response is rejected when its complete envelope exceeds the 32 KiB response budget; smaller authenticated pages advance with a signed filter-bound cursor. The storage lookahead test covers the 101st row without raising the public limit.

## Acceptance

PASSED — final Release shell validation is recorded in local validation receipts. Focused raw Server and Storage receipts are in local validation receipts.
