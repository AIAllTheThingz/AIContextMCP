# Troubleshooting

If automatic context usage is missing, start a fresh session and check `$CODEX_HOME\AGENTS.override.md` first, then `$CODEX_HOME\AGENTS.md` (this installation uses `<codex-home>\AGENTS.md`). Confirm the session's Git root and nearer repository instructions; `<global-instructions>` and `<approved-root>\AGENTS.md` above a Git root are not default project discovery locations. A bootstrap `NotFound` for an unregistered project is an honest degraded result because normal startup uses `register=false`; report it and continue from repository-authoritative sources. To skip retrieval for one task, say so in that task's prompt and start a fresh session after policy edits. See [AGENT_INTEGRATION.md](AGENT_INTEGRATION.md).

| Required case | Test or observation coverage | Operator response |
|---|---|---|
| Missing executable | The saved configuration names `dotnet` and the canonical Server DLL; client UI behavior is untested. | Inspect the exact configured command and DLL path before reopening Codex. |
| Invalid working directory | The saved `cwd` is `<runtime-root>`; client UI behavior is untested. | Inspect the runtime root and the ordinary directory path. |
| Database unavailable | `SqliteContextStorageTests` cover storage failures. | Preserve the pilot database and capture the typed storage failure; do not recreate it. |
| Invalid repository path | `GitBoundaryTests` cover rejected repository paths. | Use an approved root and capture `PathRejected`; do not confuse it with `UnbornRepository`, which means the path is a valid initialized repository with no commit yet. |
| Malformed MCP request | `McpRuntimeIntegrationTests` cover strict wire handling. | Correct the typed request; do not assume a client UI test occurred. |
| Server startup failure | `HostTests` cover options and `HostComposition` storage initialization. | Capture stderr and the typed startup failure before changing configuration. |

Source and test coverage is not desktop GUI evidence. The final restart receipt records successful desktop recovery and persistent context readback; GUI error injection remains untested.

Historical diagnostics: the configuration receipt records a resolved locked `Server.dll`; the earlier `Unknown` MCP Git discrepancy and prior MCP runner `Win32Exception` are historical. The shell receipt is 92/0/0 while the shared-name TRX retains only 50 tests; the console receipt is complete. GUI error injection remains untested and is the documented limitation. See deployment.json (local validation receipt), release-no-build-test-output-20260909T233205Z.txt (local validation receipt), and final-restart.json (local validation receipt).

For a controlled restart, identify and stop only confirmed AIContextMCP processes. Preserve siblings, back up the database, artifacts, and runtime configuration, then verify the restarted process uses the separate build, runtime, data, and logs roots. No migration success should be claimed without fresh evidence.

No Git metadata or commits were created for this validation work.
