# Security design

The local MCP server treats client input and repository content as untrusted. It uses SDK 2.2 stdio, strict typed schemas, bounded payloads, typed errors, and stderr diagnostics.

Core uses bounded verified Windows handles and no Git process. The operator allowlist defaults to `<approved-root>` and is configurable only by operator configuration. Repository reads enforce path containment, reparse safety, and conservative Git metadata support.

Bounds are 512 index entries, worktree files, and worktree directories; 1 MiB index, loose-object, and worktree-file reads; 8 MiB aggregate worktree reads; a 32 KiB artifact file limit; 64 KiB requests; 32 KiB responses; and 16 KiB bootstrap responses. A version-2 SHA-1 Git index and verified loose objects produce the SHA-256 working-tree fingerprint. A root `.gitattributes` file is accepted only when it is UTF-8, LF-only, at most 32 KiB, and contains `*`/`*.<extension>` rules using `text` or `text=auto` with optional `eol=lf`; nested, info, and global attributes are unsupported, and every observed worktree file must be CR-free. Unsupported attributes yield `Unknown`; linked metadata is rejected.

SQLite V4 uses parameterized SQL, explicit migrations, atomic mutation receipts, UTC timestamps, and 30-day receipt retention. Credential-shaped values are screened before persistence, and application diagnostics do not include raw request payloads. The screen is heuristic and does not guarantee detection of every secret. Snapshot tokens are process-local, expire after 15 minutes, and become invalid after restart.

The deployment assumes a trusted operator controls local configuration, repository roots, and the SQLite file. It does not isolate against a hostile process under the same Windows account. Verified handles and SQLite transactions protect their bounded operations, but do not promise an immutable filesystem or database after an observation.

The isolated final Release build and test run passed 0 warnings/errors and 92/92 tests. Client registration, fresh CLI startup, and pilot operation are evidenced; automated backup and organization-wide onboarding remain future work. The final foundation gate is PASSED: source-workspace Git/tracked-hygiene evidence is available and the configured artifact root is canonical.
