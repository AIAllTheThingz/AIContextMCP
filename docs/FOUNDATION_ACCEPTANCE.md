# AIContextMCP Foundation Acceptance

## Version / Git State

The source workspace is now a Git repository at `<checkout>`, on branch `main`, with no commit yet (unborn HEAD). The working tree has 63 staged source, documentation, and test files; the unstaged diff is empty and cached diff inspection is available. Runtime data, logs, artifacts, bin, and obj remain ignored. The registered pilot is clean on feature/issue-21-governance-contract-semantics at 45159b5c57e698190476683ac5b3f8c8c4a50de9; the nested cascade fixture is clean on master at 4ad7d11b71f9b09b03b9f1c15a8b519506faf375.

## Environment

Windows local stdio deployment; .NET SDK 10.0.401 and runtime 10.0.12. The [official .NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core) lists .NET 10 as active LTS through 2028-11-14.

## Architecture

The eight-tool local MCP server, SQLite persistence, bounded artifacts, typed contracts, strict JSON, and structured stderr logging are implemented as documented in the architecture and contract documents.

## Build

Current resolution restore completed successfully (exit 0); current isolated Release build completed with 0 warnings and 0 errors (exit 0). The initial build encountered locked live DLLs; the permitted `--artifacts-path` fallback passed. Evidence: local validation receipts, local validation receipts.

## Tests

Current resolution test output is Core 50, Storage 32, Server 10: 92 passed, 0 failed, 0 skipped (exit 0); the initial command encountered locked live DLLs and the permitted isolated fallback passed. See local validation receipts.

## MCP Contract

The supported surface is project_bootstrap, context_search, context_record, decision_list, decision_record, finding_record, test_record, and handoff_create. The current session tool catalogue listed eight AIContextMCP tools and fourteen distinct sibling tools; sibling SDK inspection succeeded. No arbitrary command or filesystem tool is present.

## Client Integration

Earlier fresh CLI processes covered automatic configured-server launch for the pilot and nested fixture. Resolution validation separately launched a fresh stdio server with the saved canonical environment values and confirmed startup plus all eight tools. Existing clients started before the correction retain the old environment until restarted; a desktop-GUI lifecycle test was not performed and is not claimed.

## Persistence

The fresh pilot process recovered project 4fe2a4dc-6a16-4ff0-ba20-abb11bff66b2, repository e3f7e841-4a66-446c-ae05-03adc9a4dbb3, the current 7/0/0 validation, accepted decision aedc0dd3-d7ce-43c7-8f8a-5fe93827763c, zero findings, and handoff 2f95ab00-9ffb-47e6-9ead-8f8d54deb7d8 without writes. Current targeted reads recover the current objective and accepted decision; the controlled live-runtime test covers finding persistence. Compact sanitized summary-projection receipts, not full retained transcripts, are local validation receipts and fresh-cli-nested-sanitized.jsonl.

## Git Awareness

Fresh pilot and nested bootstrap results matched direct Git branch, HEAD, and clean state. Controlled tests cover clean, dirty, branch, HEAD, and stale/current transitions. The earlier 91/92 Git-PATH fixture result is an environment-specific runner limitation; the unchanged shell verification passed 92/92, so it is not an unresolved product defect. Source-workspace Git validation now has branch, staged-file, and diff evidence.

## Source of Truth

The documented order is repository content, Git state, explicit configuration/documentation, validation artifacts, persistent metadata, generated summaries, and conversation history. Fresh bootstrap used current Git observations and returned a validation matching the current pilot branch and HEAD; controlled tests retain stale/current coverage. Persisted records were used as supplemental state and did not override direct Git.

## Context Efficiency

Each ordinary fresh session made one compact bootstrap call automatically and did not automatically call context search, decision list, historical validation, handoff, or log retrieval. The bootstrap response contained compact current state only; exact token savings were not measured.

## Cascading Policy

The nested approved-root fixture fresh session preserved its local NESTED_POLICY_ACTIVE marker and automatically bootstrapped without an explicit MCP instruction. The pilot session supplied the same ordinary-prompt coverage for a registered repository.

## Degraded Mode

A fresh read-only CLI process used only the per-process override mcp_servers.aicontextmcp.enabled=false. It reported AIContextMCP unavailable, continued from Git and repository files, did not fabricate persisted records, and made no writes. The actual configuration remained enabled; receipt: local validation receipts

## Filesystem Security

Prior bounded inspection covered approved-root and reparse-point protections. The canonical configured deployment now uses <checkout>\data\artifacts. Both old and canonical directories exist as empty regular directories with no reparse points; no artifact migration was needed.

## Database Security

Prior tests and inspection covered parameterized SQLite access, migrations, bounded records, UTC timestamps, duplicate handling, and recovery behavior. No active database was modified.

## Secret Handling

The bounded independent review covered 204 files and 147 logs (about 1.57 MiB) and found no source defect or real credential in scope. Four expired snapshot tokens in historical testrun-retrieval receipts were redacted; JSON validity and the absence of remnants were independently verified. Sanitized fresh-session receipts redact all snapshot tokens.

## Logging

Logging is structured and bounded. Existing logs omit project ID and duration in some records; this is documented as nonblocking. Historical sensitive log material is redacted, and the new JSONL summary-projection receipts contain no credentials or snapshot tokens.

## Sibling MCP Coexistence

Sibling SDK inspection succeeded with fourteen distinct tools. The current configuration has one enabled aicontextmcp stanza and one enabled dotnetdevmcp-server stanza; neither configuration nor sibling source was changed.

## Dependencies

The implementation targets .NET 10 LTS. The verified vulnerable-package scan exited 0 with zero advisory hits across six projects (local validation receipts). Package review found four nonblocking updates; no upgrade was made (verified-packages.json and verified-outdated-packages.json).

## Repository Hygiene

The source workspace Git repository is initialized with 63 safe staged files and no commit or fabricated history. Baseline records the workspace as initially not Git; no ancestor or local alternate was found in accessible fixed-drive and known-project checks, while the named GitHub repository remained unresolved. Initialization was consistent with project intent; no author was configured, so the repository correctly remains unborn. Runtime databases, logs, artifacts, bin, and obj are ignored.

## Documentation

All required documents exist. This record distinguishes current Chunk 11 checks from historical Chunk 6-10 evidence and records the fresh CLI ordinary/degraded checks and their current remediation evidence.

## Known Limitations

Search is structured rather than semantic; there is no embeddings/vector retrieval or UI; transport is local stdio; some log fields are omitted; and no desktop-GUI-specific lifecycle test is claimed.

## Deferred Work

Organization-wide onboarding remains future work; no initial commit is manufactured. The historical Git-PATH fixture does not require product remediation.

## Final Acceptance

The source-workspace Git metadata and canonical artifact-root blockers are resolved. The fresh CLI client-start, cascade, and degraded checks are evidenced; the historical Git-PATH fixture is not a product blocker. The final result is:

PASS - FOUNDATION COMPLETE
