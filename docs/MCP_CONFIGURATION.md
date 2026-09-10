# MCP Configuration

AIContextMCP uses local stdio. Configure the client to run `dotnet` with the full published output directory, as shown in [config.example.toml](config.example.toml). Replace every placeholder with a local value and keep the resulting client configuration outside Git.

The environment uses the `AIContextMCP_` prefix:

- `AIContextMCP_DatabasePath`: writable SQLite database path.
- `AIContextMCP_ArtifactRoot`: writable artifact directory.
- `AIContextMCP_ApprovedRepositoryRoots__0` (and additional indexed values): approved parent directory containing repositories. The parent itself is not treated as a repository.
- `AIContextMCP_MinimumLogLevel`: optional log level, defaulting to `Information`.

Use a published directory as the deployment unit:

```powershell
dotnet publish .\src\AIContextMCP.Server\AIContextMCP.Server.csproj --configuration Release --output .\publish
```

The wire tools are `project.bootstrap`, `context.search`, `context.record`, `decision.list`, `decision.record`, `finding.record`, `test.record`, and `handoff.create`. Some clients display their underscore aliases: `project_bootstrap`, `context_search`, `context_record`, `decision_list`, `decision_record`, `finding_record`, `test_record`, and `handoff_create`.

For a new repository, first call `project_bootstrap` with `register=true` and a unique `requestId`. Subsequent read-only sessions call it with `register=false` and `includeWorkingTree=true`; then use targeted `context_search` requests. Existing projects can start directly with the read-only call.

Historical pilot validation covered restart persistence and the eight-tool catalog. The historical client receipts are local evidence only and are intentionally not distributed with this public configuration guide.
