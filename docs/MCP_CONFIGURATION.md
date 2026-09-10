# MCP Configuration

AIContextMCP uses local stdio. Configure the client to run `dotnet` with the full published output directory, as shown in [config.example.toml](config.example.toml). Replace every placeholder with a local value and keep the resulting client configuration in the client configuration location, outside Git.

The environment uses the `AIContextMCP_` prefix:

- `AIContextMCP_DatabasePath`: writable SQLite database path.
- `AIContextMCP_ArtifactRoot`: writable artifact directory.
- `AIContextMCP_ApprovedRepositoryRoots__0` (and additional indexed values): approved parent directory containing repositories. The parent itself is not treated as a repository.
- `AIContextMCP_MinimumLogLevel`: optional log level, defaulting to `Information`.

Restore, build, and test use the repository defaults, which place per-project `bin`/`obj` output under the external `../build/AIContextMCP` root without extra flags. Publish separately into the runtime root; the published runtime directory is the deployment unit:

```powershell
dotnet publish .\src\AIContextMCP.Server\AIContextMCP.Server.csproj --configuration Release --output '<runtime-root>'
```

Keep source, build, published runtime, data/artifacts, and logs separate. The service command points at the runtime root's published DLL; `cwd` is the runtime root. Client configuration remains in its client configuration location outside Git. The server emits structured diagnostics to stderr and has no `LogRoot` option; capture stderr outside the checkout when operational logs are required.

The wire tools are `project.bootstrap`, `context.search`, `context.record`, `decision.list`, `decision.record`, `finding.record`, `test.record`, and `handoff.create`. Some clients display their underscore aliases: `project_bootstrap`, `context_search`, `context_record`, `decision_list`, `decision_record`, `finding_record`, `test_record`, and `handoff_create`.

For a new repository, first call `project_bootstrap` with `register=true` and a unique `requestId`. Subsequent read-only sessions call it with `register=false` and `includeWorkingTree=true`; then use targeted `context_search` requests. Existing projects can start directly with the read-only call.

Historical pilot validation covered restart persistence and the eight-tool catalog. The historical client receipts are local evidence only and are intentionally not distributed with this public configuration guide.
