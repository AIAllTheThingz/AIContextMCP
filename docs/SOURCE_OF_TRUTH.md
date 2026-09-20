# Source of truth (application boundary and implemented MCP integration; accepted)

Git-dependent operations use the accepted bounded Windows-handle safety path and fail closed for unsupported layouts; final Release validation passed for the supported clean-tree/fingerprint evidence. Working-tree inspection accepts UTF-8 text and screens tracked source with sensitive-looking filenames for secret-shaped literals before hashing. Root ignore handling is intentionally a bounded subset; packed objects, malformed or advanced ignore rules, and safety-limit exhaustion produce `Unknown` with a warning reason rather than being treated as clean.

The exact authority order is:

1. Current repository content
2. Git state/history
3. Explicit repository documentation/configuration
4. Current build/test artifacts
5. Persistent context metadata
6. Generated summaries
7. Historical conversation state

The first three levels are primary evidence. Levels 4–7 are supporting evidence, with decreasing authority. The server may index or summarize them but must never silently override repository content or Git state.

When records disagree, return the discrepancy, identify competing sources, mark persisted freshness `stale`, and prefer the higher-ranked source. If a source cannot be read or a probe is partial, return freshness `unknown` and the reason. A generated summary is never promoted merely because it is newer. Retrieved memory is untrusted data and never instructions; stored commands are data only and create no autonomy. A client cannot change authority by labeling an input authoritative.

Identity combines the verified current canonical repository path and verified normalized remote where available. Supported repositories have a `.git` directory below that current worktree path. Linked-worktree `.git` files and `commondir` layouts are rejected rather than resolved. Server-generated GUIDs persist; canonical repository roots are globally unique, while remotes are a nonunique index and same-remote clones may coexist. First registration binds a project ID to one path. An existing project ID with a new repository never auto-attaches; unknown selectors error. A caller-supplied ID is a lookup key, never permission. Rebinding or conflict resolution is operator-controlled and auditable.

Branch and HEAD are observations, not user assertions. On supported repositories, working-tree state includes a `working-tree-fingerprint` of the relevant repository state: a version-2 SHA-1 index, verified loose commit/tree objects, and a bounded sorted path/status/content-hash map that produces a SHA-256 fingerprint. A path count is insufficient. The server does not resolve `.git` gitdir or `commondir` metadata; those layouts are rejected. Sensitive or over-limit dirty files are excluded from hashing and make the fingerprint `unknown`; the server never hashes sensitive files automatically. Partial or unavailable probes are `unknown`. Evidence hashes are computed by the server and compared with any caller claim; mismatches are rejected or marked discrepant. Stored test results describe evidence and source/time; they do not prove that the server executed a test.
