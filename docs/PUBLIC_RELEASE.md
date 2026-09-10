# Public Release

## Repository

Target repository: `https://github.com/AIAllTheThingz/AIContextMCP`.

## License

GNU General Public License version 3 or later (`GPL-3.0-or-later`). See [LICENSE](../LICENSE).

## Sanitization

Public documentation uses generic placeholders for local paths, usernames, client configuration, and runtime receipts. Keep source, build, runtime, data/artifacts, and logs roots separate. Runtime databases, artifacts, logs, build output, IDE state, local configuration, secrets, tokens, private keys, and credential material are ignored and excluded from the public source tree.

## Runtime Files Excluded

`.gitignore` excludes `bin`, `obj`, test results, coverage, logs, SQLite files and journals, local `.env` files, local appsettings, certificates, IDE state, and the `data` directory.

## Configuration Templates

[config.example.toml](config.example.toml) contains placeholders only. Replace them locally; do not commit the resulting client configuration.

## Secret Review and Git History Review

The candidate review found no known credentials and one synthetic private-key test marker. At the initial baseline there were zero commits; 74 local blobs were scanned. Independent final sanitation accepted 69 staged files with no forbidden runtime or generated files. See [PUBLIC_RELEASE_REVIEW.md](PUBLIC_RELEASE_REVIEW.md).

## Local Build/Test, Publish, and Runtime

External restore/build completed with 0 warnings and 0 errors; Release tests passed 92/0/0 (Core 50, Server 11, Storage 31). Publish and republish succeeded, and two fresh Codex CLI launches from the external runtime preserved database records and all eight tools. Source `bin`/`obj`/publish/log/data outputs were cleared after validation. Desktop GUI restart validation is not claimed.

## AIContextMCP Self-Rebuild Test and MCP Reconnection

Historical restart evidence shows persistence recovery for the completed pilot. The earlier `PathRejected` bootstrap was a configuration-boundary observation, not a migration failure. Fresh CLI launches from the external runtime recovered the expected records and eight-tool catalog. No desktop GUI restart or installed-client continuity claim is made.

## Clean Source Validation

Clean-source export validation passed: restore, build with 0 warnings/errors, 92/0/0 tests, publish, and fresh-database eight-tool stdio smoke test all passed. It used no local runtime or client configuration.

## GitHub Repository, Branch Protection, Secret Scanning, Dependabot, CodeQL, and CI

Local CI and weekly NuGet Dependabot configuration are present. GitHub visibility, default branch, rulesets, secret scanning, push protection, Dependabot alerts/security updates, and CodeQL status require remote verification after publication and are pending.

## Remote Clean Clone

Pending remote publication and clone validation.

## Known Limitations

The server is local stdio software. It does not provide remote authentication, embeddings, vector search, or arbitrary command execution. Unsupported Git layouts are handled conservatively.

## Reproduction Instructions

Follow the clone, restore, build, test, runtime-layout, and configuration instructions in [README.md](../README.md). Keep runtime data outside the checkout and verify the published directory starts with `dotnet <runtime-root>\\AIContextMCP.Server.dll`.

## Status

FAIL - PUBLIC REPOSITORY NOT READY

Release gates are intentionally marked pending until the maintainer supplies final self-hosting, remote, and clean-clone evidence.
