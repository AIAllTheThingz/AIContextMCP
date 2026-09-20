# AIContextMCP

AIContextMCP is a local .NET 10 MCP server that keeps compact, Git-aware engineering context in SQLite and bounded filesystem artifacts. Git and repository instructions remain authoritative.

## Overview

The server is designed for Windows local stdio deployments and fails closed when repository paths are outside configured approved roots or use unsupported filesystem layouts.

## Problem It Solves

AI coding sessions often lose decisions, validation results, and handoffs between sessions. AIContextMCP stores that durable project state locally so a client can bootstrap current repository state and retrieve only relevant history.

## Architecture

```text
MCP client -> AIContextMCP -> Core -> SQLite + filesystem artifacts
                                  -> Git repositories remain authoritative
```

## Context Model

- Tier 1: compact bootstrap and current repository state.
- Tier 2: retrievable decisions, findings, tests, handoffs, and context.
- Tier 3: cold or large authoritative artifacts addressed by bounded references.

## Features

- Local stdio MCP transport with strict JSON contracts.
- Git branch, HEAD, and working-tree freshness checks.
- Explicit `UnbornRepository` results for initialized repositories that do not yet have a commit.
- SQLite persistence with migrations, bounded records, and restart recovery.
- Secret-shaped value rejection and bounded artifact storage, while ordinary credential-handling source files remain observable.
- Branch-scoped context retrieval, direct current-finding lookup, and explicit validation-run supersession.
- Safe degraded operation when the server is unavailable.

## MCP Tools

The server exposes exactly eight wire tools: `project.bootstrap`, `context.search`, `context.record`, `decision.list`, `decision.record`, `finding.record`, `test.record`, and `handoff.create`. Clients such as Codex may display underscore aliases: `project_bootstrap`, `context_search`, `context_record`, `decision_list`, `decision_record`, `finding_record`, `test_record`, and `handoff_create`.

## Requirements

- .NET SDK 10.0.
- Windows is required for the validated repository boundary and reparse-point checks.
- Git available on `PATH` for the documented clone and development/test workflow.
- A local writable directory for the SQLite database and artifact root.

## Clone, Restore, Build, Test, and Runtime Layout

```powershell
git clone https://github.com/AIAllTheThingz/AIContextMCP.git
Set-Location AIContextMCP
dotnet restore AIContextMCP.slnx
dotnet build AIContextMCP.slnx --configuration Release
dotnet test AIContextMCP.slnx --configuration Release
dotnet publish .\src\AIContextMCP.Server\AIContextMCP.Server.csproj --configuration Release --output '<runtime-root>'
```

Restore, build, and test default to external per-project `bin`/`obj` output below `../build/AIContextMCP`; no extra flags are required. Keep source, build output, published runtime files, data/artifacts, and logs in separate roots. A generic local layout is `<mcp-root>/{AIContextMCP,build/AIContextMCP,runtime/AIContextMCP,data/AIContextMCP,logs/AIContextMCP}`. Publish into `<runtime-root>`; that published directory is the deployment unit. Do not copy only the server DLL.

## Configuration

The server reads environment variables with the `AIContextMCP_` prefix. Copy [config.example.toml](docs/config.example.toml), replace placeholders with local values, and configure your MCP client. The server itself does not read TOML; the sample shows the client command and environment mapping.

Required settings are `AIContextMCP_DatabasePath`, `AIContextMCP_ArtifactRoot`, and one or more `AIContextMCP_ApprovedRepositoryRoots__N` values. Configure an approved parent directory containing repositories; the root itself is not treated as a repository. See [MCP_CONFIGURATION.md](docs/MCP_CONFIGURATION.md).

## MCP Client Configuration

Use a local stdio registration whose command runs `dotnet` against the published `AIContextMCP.Server.dll`, with `cwd` set to the runtime root and the environment values in the sample. Keep the client configuration in its client configuration location, outside Git, and keep database, artifacts, and logs outside Git.

### Claude setup and first registration

Claude connects to the same local stdio server. After deploying a new published runtime, restart or reconnect the Claude MCP server so Claude refreshes `tools/list`; otherwise the client can retain an older tool catalog. The current build returns both text and structured MCP responses; the earlier empty-content/`Unknown error` behavior is fixed.

For the first local registration, call `project_bootstrap` with the repository path and a unique request ID. Mutation request IDs use `<issued-UTC-epoch-seconds>:<lowercase-D-UUID>`, for example `1789852800:01234567-89ab-cdef-0123-456789abcdef`. `repositoryPath` is required for local registration. Omit `projectId` and `remote`; both `projectId` and `repositoryId` are server-issued UUIDs returned after registration. `remote` is optional and acts as a match constraint when supplied:

```json
{
  "requestId": "<issued-UTC-epoch-seconds>:<uuid>",
  "repositoryPath": "D:\\Projects\\Example",
  "register": true
}
```

For inspection, use `register: false` (and normally `includeWorkingTree: true`). A `NotFound` response means that the canonical repository is not registered; register it explicitly, then use the returned server-issued IDs. Do not guess UUIDs. If the client still shows no tools or an empty/unknown result after deployment, restart or reconnect its MCP server and retry the same request ID.

## Usage Examples

For a new repository, call `project_bootstrap` once with `register=true` and a request ID, then use `register=false` and `includeWorkingTree=true` for read-only bootstrap. An initialized repository with no commit returns `UnbornRepository`; `PathRejected` remains reserved for unsafe, missing, or disallowed paths.

Use `context_search` with `branch` to retrieve records for one branch, or with `findingId` to retrieve the current scoped finding revision and provenance. Configurable response budgets accept 8–32 KiB; `maxBytes` defaults to 16 KiB. When a newer validation replaces an older run, pass the older run as `supersedesId` to `test_record` so the relationship is stored explicitly. Record accepted decisions, findings, and handoffs through their corresponding tools.

## Persistent Storage

SQLite stores bounded project metadata and records. Larger evidence is stored below the configured artifact root and referenced by hash. Back up the database, artifact root, and runtime configuration together; all are local runtime data and are excluded from Git. Structured diagnostics continue to stderr; client or service-manager capture belongs under the separate logs root.

## Security Model

Repository paths must be beneath configured approved roots and are checked for containment and unsafe reparse points. The server never executes client-supplied commands, rejects credential-shaped values, bounds requests and records, and writes logs to stderr. See [SECURITY.md](docs/SECURITY.md).

## Context Efficiency

Bootstrap returns a compact current-state projection. Retrieve Tier 2 records only when relevant and use Tier 3 artifacts for large authoritative evidence. Exact limits and freshness behavior are documented in [MCP_CONTRACT.md](docs/MCP_CONTRACT.md).

## Known Limitations

The server is local and uses stdio; it does not provide remote access, authentication, embeddings, vector search, or organization-wide repository migration. Working-tree inspection supports bounded tracked source screening, UTF-8 text, and a small root `.gitignore` subset (root literals and simple `*` basename globs); advanced, malformed, nested, packed-object, and over-limit layouts return `Unknown` with a warning reason. The limits are bounded safety heuristics, not universal Git compatibility. The implementation reads Git metadata directly; Git on `PATH` is needed for the documented build/test workflow and fixtures.

## Development

See [ARCHITECTURE.md](docs/ARCHITECTURE.md), [OPERATIONS.md](docs/OPERATIONS.md), and [TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md). Historical pilot and local validation summaries are retained in [PILOT_RESULTS.md](docs/PILOT_RESULTS.md) and [LOCAL_TEST_RESULTS.md](docs/LOCAL_TEST_RESULTS.md).

## License

AIContextMCP is licensed under the GNU General Public License version 3 or later: [GPL-3.0-or-later](LICENSE).
