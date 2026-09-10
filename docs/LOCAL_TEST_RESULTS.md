# Local Test Results

## Current Chunk 11 verification

Verified Release restore, isolated Release build, and the full test suite on 2026-09-09 local time: restore exit 0; build exit 0 with 0 warnings and 0 errors; Core 50, Storage 32, Server 10, total 92 passed, 0 failed, 0 skipped. Evidence is in local validation receipts, `verified-build-output.txt`, and `verified-test-output.txt`. The vulnerable-package scan exited 0 with zero advisory hits across six projects; package and outdated-package receipts are in the same directory.

An earlier direct MCP-runner test result was 91/92 because that runner's Git-path fixture could not start Git. The unchanged shell verification above passed 92/92, so the 91/92 result is a historical process-environment limitation, not an unresolved product defect.

## Environment

Windows 10.0.26200 x64; .NET SDK 10.0.401; .NET 10 runtime 10.0.12; no global.json. The final shell environment found Git at `C:\Program Files\Git\cmd\git.exe`, version 2.54.0.1.

## Git State

`<checkout>` is not itself a Git repository: `git status --short`, `git branch --show-current`, and `git rev-parse HEAD` each returned `fatal: not a git repository`. No clean-tree claim is made for this workspace. Controlled temporary repositories under approved test roots supply the Git coverage.

The current package review found no vulnerable packages. Outdated packages are nonblocking: Microsoft.Extensions.Hosting 10.0.12, Microsoft.Data.Sqlite 10.0.12, Microsoft.NET.Test.Sdk 18.10.0, and xunit.runner.visualstudio 4.0.0 are available.

## Historical Chunk 6 Build Validation

`dotnet restore <checkout>\AIContextMCP.slnx` passed with exit code 0. `dotnet build <checkout>\AIContextMCP.slnx --configuration Release` passed with exit code 0, six projects built, 0 warnings, and 0 errors.

The restore and build receipts are compact structured dotnet-dev results in local validation receipts and local validation receipts. The original raw dotnet-dev result text was not retained.

## Historical Chunk 6 Test Validation

`dotnet test <checkout>\AIContextMCP.slnx --configuration Release --no-build` passed in the shell fallback: Core 33/33, Storage 32/32, and Server 10/10; 75/75 total, 0 failed, 0 skipped. Raw shell output is retained in local validation receipts.

Focused raw receipts remain in local validation receipts: Server 10/10 and Storage 32/32. Historical pre-remediation evidence remains in local validation receipts and is not acceptance evidence.

## Historical Tool Environment Exception

The earlier dotnet-dev test invocation returned 74/75 because its child process could not inherit or resolve Git. `Get-Command git` in the shell found the executable above, and the unchanged shell fallback passed all 75 tests. This is recorded transparently in local validation receipts and local validation receipts; no source change was made for the tool-environment failure.

## MCP Server Startup

The SDK 2.2 stdio runtime starts after storage initialization. The Server suite passed 10/10, including a live process test that verifies clean EOF exit and restart.

## Tool Enumeration

Exactly eight tools are registered. The catalog test verifies typed strict input and output schemas for all eight.

## Project Bootstrap

The controlled live runtime fixture bootstraps an approved temporary repository, checks snapshot scope, and exercises read-only bootstrap plus explicit registration. No production repository was used.

## Persistence Across Restart

The live stdio fixture records and retrieves context, decisions, test results, findings, and handoffs before and after restart. Durable replay receipts are verified in the same Storage-project result.

## TestRun Retrieval Regression

Bootstrap validation now merges repository-scoped and project-scoped TestRuns matching the current branch and HEAD, selects the newest by `COALESCE(CompletedUtc, CreatedUtc)` (with deterministic per-scope ID ordering), and only falls back to the newest historical candidates when no exact candidates exist. Runs without `durationMs` remain eligible; stale historical HEADs remain visible with stale freshness. The compact response contains only ID, status, passed/failed/skipped counts, branch, commit, freshness, and created time.

The focused Core regression passed 1/1 and covers two-run newest selection, matching branch/HEAD current freshness, all counts, and changed-HEAD stale freshness. The live Server integration passed 1/1 with `durationMs` omitted and covers restart retrieval. A fresh stdio process using the final default Release output retrieved TestRun `bc37ba53-f4be-43c8-94f8-b31060dd5b73` with `Passed 10/0/0`, `master`, HEAD `05df5ac31797a7a5889d02358b87749675cfe74b`, `Current`; readonly SQLite confirmed one TestRun for project `7b346600-0b9a-4689-9ae5-e9e82ce5b9d4`. Receipts are in local validation receipts.

The required default Release build and test commands were rerun after the source change. After reversible same-directory assembly replacement for the three locked server dependencies, `dotnet build AIContextMCP.slnx --configuration Release` passed with 0 warnings/errors and `dotnet test AIContextMCP.slnx --configuration Release` passed Core 40/40, Storage 32/32, and Server 10/10 (82/82 total, 0 failed, 0 skipped). No live process was terminated or reconfigured.

## Stale-State Detection

The live process test verifies clean-current state, dirty state at the same HEAD, restoration to clean state, changed-HEAD staleness, and branch-change staleness. Historical run snapshots remain historical observations.

## Filesystem Security

Verified-handle repository checks use bounded support: 512 index entries, 512 worktree files, 512 worktree directories, 1 MiB index/loose-object/worktree-file reads, 8 MiB aggregate worktree reads, and a 32 KiB artifact file limit. Unsupported packed or linked metadata, reparse paths, sensitive paths, and namespace changes return Unknown or are rejected without consuming untrusted metadata.

## Database Security

SQLite V4 migration, schema validation, atomic replay, cross-project owner checks, exact bounds, keyset pagination, and transaction rollback passed in the Storage-project 32/32 result. No production database was opened.

## Secret Handling

Credential-shaped values and unsafe persistence scopes are rejected before persistence. Artifact and evidence hashes are server-computed; raw request payloads and credential-shaped values are excluded from application diagnostics.

## Malformed Request Handling

The protocol guard bounds raw JSON lines to 64 KiB, rejects recursive duplicate properties and unsafe request IDs, sends a safe error, and continues with the next valid request. Strict tool schemas reject unknown fields, numeric or combined enums, malformed values, and oversized tool envelopes.

## Logging Validation

Runtime validation scans stderr for client secrets. Protocol guard errors use `InvalidRequest` and tool failures use typed envelopes. Response limits are 32 KiB for normal tools and 16 KiB for bootstrap. Mutation receipts are bounded before commit; live wire tests verify discovery response size.

## Known Limitations

The workspace itself has no Git metadata, so it cannot supply a workspace clean-tree claim. Supported repository observation returns Unknown for unsupported packed objects, sensitive paths, and over-limit cases; it rejects linked-worktree, commondir, and unsafe-reparse layouts rather than widening reads. Controlled pilot, client registration, and later roadmap work are not part of this accepted remediation.

## Open Findings

No accepted security or Pony findings remain for this scope. The historical dotnet-dev Git-path failure is an environment limitation with a documented successful shell validation, not an unresolved product failure.

## Chunk 6 Acceptance

PASSED — READY FOR CONTROLLED LOCAL TESTING. Final Release shell validation is 75/75 with 0 failures and 0 skips. Evidence: local validation receipts.

## Chunk 8 Regression

PASSED: direct Release restore and build exited 0; isolated-artifact Release tests passed 92/92 with 0 failures and 0 skips (Core 50, Storage 32, Server 10). Evidence: local validation receipts, `final-regression-build.txt`, and `final-regression-test.txt`.

The earlier Chunk 6 limitations describe that historical scope; the completed Chunk 8 pilot and its fresh recovery are recorded in `docs/PILOT_RESULTS.md`.

## Chunk 11 resolution

The active artifact-root override now resolves to `<checkout>\data\artifacts`. A fresh stdio process exited 0 and enumerated all eight tools; sanitized receipts are in local validation receipts. The source repository is on `main` with an unborn HEAD, 63 safe staged files, and an empty unstaged diff. The plain build/test commands encountered live-process output locks; isolated `--artifacts-path` validation passed the Release build and all 92 tests.
