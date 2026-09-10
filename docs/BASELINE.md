# Chunk 0 baseline

Scope is the direct user request to complete Chunk 0 discovery; the attached roadmap's later auto-continue language is outside this document.

Date: 2026-09-07 (America/Chicago)

This is a read-only environment inventory. No build, restore, registration, or sibling-server files were changed. `<checkout>` existed before this document and was empty; it is not a Git repository. `<approved-root>` is absent.

## Governing instructions and workspace

- `<global-instructions>` was read in full before inspection. It supplies broad engineering, security, validation, and agent-policy guidance. The sequential Chunk 0 scope, design-first roadmap, and no-sibling-change restriction come from the pasted brief, not from a claim that those exact requirements are in `<global-instructions>`.
- No `AGENTS.md` exists under the workstation project parent or `<dotnet-dev-checkout>`; the only discovered applicable file is `<global-instructions>`.
- The release brief was read before inspection; no attachment path or session identifier is part of the public record.

## Sibling .NET MCP server

Sibling: `<dotnet-dev-checkout>` (`DotNetDevMcp.sln`), a console stdio MCP server. Its README says stdout carries MCP traffic and stderr carries logs. The launchable published executable is `publish\DotNetDevMcp.Server.exe`; the README's proposed `dotnet <server.dll>` shape is explicitly described there as unverified Astra syntax.

### Project and packages

`src\DotNetDevMcp.Server\DotNetDevMcp.Server.csproj` targets `net10.0`, enables implicit usings and nullable, and is an executable. Direct packages are:

- `ModelContextProtocol` 2.2.0
- `Microsoft.Extensions.Hosting` 10.0.0
- `Microsoft.Build.Locator` 1.11.2
- compile-only/private `Microsoft.Build` 18.9.6, `Microsoft.Build.Framework` 18.9.6, `Microsoft.NET.StringTools` 18.9.6
- `Microsoft.CodeAnalysis.CSharp.Workspaces`, `Microsoft.CodeAnalysis.CSharp.Features`, and `Microsoft.CodeAnalysis.Workspaces.MSBuild` 5.9.0

The test project targets `net10.0`, enables nullable/implicit usings, treats warnings as errors, and uses Microsoft.NET.Test.Sdk 17.14.1, xunit 2.9.3, xunit.runner.visualstudio 3.0.2, ModelContextProtocol 2.2.0, and the compile-only MSBuild/StringTools packages above. The server project also sets `TreatWarningsAsErrors` in its project file.

### Runtime and lifecycle patterns

`src\DotNetDevMcp.Server\Program.cs` creates the host, clears providers, logs to stderr, registers singleton `DotNetDevelopmentService`, configures strict JSON options, then calls `AddMcpServer().WithStdioServerTransport().WithTools([typeof(DotNetTools)], options)` and runs it. `SemanticWorker` is a separate worker path. No appsettings or custom strongly typed configuration files were found. `DotNetTools.cs` exposes typed adapters; `DotNetDevelopmentService.cs` maps bounded process, path, JSON, cancellation, and domain failures to typed envelopes. Representative tests are `tests\DotNetDevMcp.Tests\PhaseOneTests.cs`, `PhaseEightValidationTests.cs`, `PhaseTenTests.cs`, and `PhaseElevenValidationTests.cs`; protocol scripts test enumeration, invalid shapes, preview no-write behavior, and fixture restore/build. These are conventions to evaluate for reuse, not code to merge.

### Testing and receipts

The sibling has xUnit tests under `tests\DotNetDevMcp.Tests` plus PowerShell protocol validators (`tests\validate-phase11-protocol.ps1`, `validate-phase10-protocol.ps1`, and `validate-protocol.ps1`). `phase11-validation-report.md` records a historical 97 passed / 0 failed / 0 skipped run, strict 14-tool/83-variant checks, and security 17/17; it is a receipt, not fresh evidence. No sibling build, restore, or test was run for this baseline.

## Environment evidence

Commands run from `<checkout>` (read-only inspection):

- `dotnet --info`: SDK 10.0.400 (MSBuild 18.9.6), also SDK 8.0.424; .NET 10.0.11 runtime installed; no `global.json` found.
- `git --version`: 2.54.0.windows.1. `git -C <dotnet-dev-checkout> status --short --branch` reports the sibling is not a Git worktree; no before/after Git diff is available there. The new directory is also not a Git worktree.
- `gh --version`: 2.95.0.
- `$PSVersionTable`: PowerShell 7.6.5, Windows 10 build 26200, x64. Windows PowerShell 5.1.26100.9168 is also installed at `<windows-system>\\System32\WindowsPowerShell\v1.0\powershell.exe`; the brief asks for this inventory, while `<global-instructions>` does not mandate a PowerShell version.
- `Get-Command sqlite3,sqlcipher,psql`: no command found. `dotnet tool list --global` shows only `dotnet-ef` 10.0.11. No SQLite runtime/tool was found under the .NET shared framework; a future project should use an explicitly selected SQLite package/provider.
- Sibling read tools were available and used: `dotnet_environment` (`sdk_info`), `dotnet_project` (`inspect_solution`), and `dotnet_project` (`inspect_project`). Their structured results confirm the SDK, two-project solution, `net10.0`, nullable/implicit usings, and package references above. PowerShell was used for source/test/configuration inspection. The NuGet cache contains SQLite provider packages under `<user-profile>\\.nuget\\packages` (Microsoft.Data.Sqlite, EF Core SQLite, and SQLitePCLRaw); these are cached packages, not a selected dependency or system CLI. Codex SQLite stores were not treated as provider evidence.

## Codex/Astra configuration observations

Safe, non-secret settings from `<codex-home>\config.toml`:

- The workstation project parent and `<checkout>` are trusted projects.
- The existing MCP registration is `[mcp_servers.dotnetdevmcp-server]`, enabled, using `<dotnet-dev-checkout>\publish\DotNetDevMcp.Server.exe` with cwd `<dotnet-dev-checkout>`.
- No AIContextMCP registration exists.
- The config also contains the built-in `node_repl` MCP registration and general model/UI/plugin settings; credential-bearing values were not read or copied.

The actual current session successfully applied `<global-instructions>` because the brief required a full manual read. `<codex-home>\AGENTS.md` and `AGENTS.override.md` are absent; there is no `CODEX_HOME` environment override or global agents directory. No `.codex` directory exists along the workstation root or this project.

Coordinator verification against the official AGENTS documentation found the default 32 KiB instruction budget and directory-based discovery rules. `<global-instructions>` is 39,272 bytes. PATH CLI 0.153.2 and desktop CLI 0.153.4 `debug prompt-input` at this project both exclude root policy; the PATH probe at the sibling also excludes it. At `<windows-path>\\` the default 32 KiB prompt input ends in section 18 and excludes section 19; desktop per-call 65,536 bytes includes the final section. No settings or files were mutated by these `-c` probes. Official references: [AGENTS.md configuration](https://learn.chatgpt.com/docs/agent-configuration/agents-md) and [Codex config reference](https://learn.chatgpt.com/docs/config-file/config-reference).

| Binary/version | cwd | budget | result |
|---|---|---:|---|
| PATH CLI 0.153.2 | AIContextMCP | default | root policy excluded |
| desktop CLI 0.153.4 | AIContextMCP | default | root policy excluded |
| PATH CLI 0.153.2 | dotnet-dev | default | root policy excluded |
| desktop CLI 0.153.4 | `<windows-path>\\` | 32 KiB | ends section 18; section 19 excluded |
| desktop CLI 0.153.4 | `<windows-path>\\` | 65,536 | final section included |

Planned supported alternative for later integration: add a short global `<codex-home>\AGENTS.md` rule telling D-workspace sessions to read `<global-instructions>` in full, plus repository-local guidance. This is an instruction to read the file, not automatic inclusion; it was not applied in Chunk 0 and requires later validation.

The Codex CLI evidence is version-specific: PATH CLI is 0.153.2, the running desktop binary is 0.153.4, and the installed Appx is 26.901.6511.0. `codex mcp get dotnetdevmcp-server` reports the existing enabled stdio executable and cwd with no extra args/env overrides. Official MCP configuration uses `mcp_servers.<name>` with required command and optional args/env/cwd/enabled/timeouts; preserve the actual key `dotnetdevmcp-server`. Reference: [MCP configuration](https://learn.chatgpt.com/docs/extend/mcp?surface=cli).

## MCP package currency

The sibling's `ModelContextProtocol` 2.2.0 is current as of this inspection on the official NuGet page, and the official C# SDK repository lists v2.2.0 as the latest release. It is therefore an appropriate starting point for AIContextMCP, subject to checking compatibility when the new project is designed. Sources: [NuGet ModelContextProtocol](https://www.nuget.org/packages/ModelContextProtocol) and [official C# SDK releases](https://github.com/modelcontextprotocol/csharp-sdk/releases).

The sibling README records these existing commands, which were not run during Chunk 0: `dotnet restore .\DotNetDevMcp.sln`, `dotnet build .\DotNetDevMcp.sln --configuration Release --no-restore`, and `dotnet test .\DotNetDevMcp.sln --configuration Release --no-build`.

## Sibling .NET MCP Reuse Assessment

### REUSE

- Official `ModelContextProtocol` SDK 2.2.0 and stdio transport pattern.
- `net10.0`, nullable, implicit usings, generic-host/DI setup, and stderr structured logging conventions.
- Typed request/response contracts and a bounded MCP adapter over application services.
- Explicit preview/apply separation and validation/error-envelope discipline where relevant.
- Existing PowerShell protocol-test style and the sibling's operational documentation conventions.

### DO NOT REUSE

- Roslyn/MSBuild semantic-analysis stack, Microsoft.Build packages, and request-scoped workspace machinery: AIContextMCP is a compact persistent context service and does not need source-semantic analysis.
- The sibling's fourteen-tool domain surface, legacy phase artifacts, large analysis services, or project mutation operations.
- Its arbitrary trusted-path behavior and lack of a single repository-root requirement; AIContextMCP must independently enforce approved roots and artifact boundaries.
- Published binaries, `bin`/`obj`, historical logs, snapshots, or test receipts as application dependencies.

### KEEP INDEPENDENT

- AIContextMCP solution, storage database, schema/migrations, core services, tests, configuration, logs, and published output.
- MCP registration: preserve the existing `dotnetdevmcp-server` entry and add a separate server only after pilot validation.
- Project identity, approved filesystem roots, credentials policy, and context source-of-truth rules.

## Naming and collision notes

`AIContextMCP` is a distinct directory and is not registered in Codex. The sibling assembly/project names are `DotNetDevMcp.Server`; no same-name collision was found. Future registration should use a distinct key and executable path.

## Limitations and scope

This is Chunk 0 discovery only. The planned approved root `<approved-root>` is absent and is a later setup/pilot decision; this project directory is not an approved general content root. Instruction cascade/truncation needs explicit handling in the later integration chunk. No exhaustive security audit was performed, and no fresh sibling build, restore, or test was run.

## Chunk 0 status

PASSED — Chunk 0 discovery and documentation accepted by the orchestrator; implementation has not begun. The only created file is this baseline. Fresh tests were not required and were intentionally not run; all sibling validation artifacts mentioned above are historical until independently rerun.
