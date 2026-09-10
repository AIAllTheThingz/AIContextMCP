# Controlled pilot plan (Completed; retained for scope)

Use exactly one actual representative local coding repository beneath the configured approved root, preferably an existing AIAllTheThingz repository. Exercise it with an isolated client harness; a safe controlled copy may be used later when approved, but a synthetic harness is not a substitute. The baseline says `<approved-root>` is currently absent; that does not authorize expanding the allowlist. Never reset a dirty user tree. Test global registration only after the isolated client harness passes.

## Steps

1. Register or resolve the project and verify canonical path, remote identity, and permissions.
2. Bootstrap and capture compact response bytes/tokens, project identity, branch, HEAD, and working-tree state.
3. Record one controlled decision and one authoritative validation summary.
4. Search for each and verify project scoping, relevance, limits, and state labels.
5. Create a handoff with objective, phase, blockers, next steps, and references.
6. Restart or use a new client context, bootstrap again, and verify recovery without historical conversation.
7. Change the harness working tree or HEAD in a controlled way, bootstrap, and verify content-fingerprint stale detection; restore the harness afterward.

## Acceptance measures

Record bootstrap size (target 1,000–4,000 tokens), retrieved context size, relevance notes, stale detection result, latency, artifact-reference behavior, and how much historical conversation was unnecessary. Pass requires no path escape, secret leakage, unbounded output, database loss, false current state, or sibling registration change. The matrix includes restart recovery, schema/backup restore, cross-project scope, replay/conflict, content fingerprints, rollback, and sibling coexistence. Failure preserves database and artifacts for diagnosis and blocks Chunk 9.

## Rollback

Stop the local server, preserve logs and database for evidence, restore the harness to its prior branch/working tree without resetting user changes, and restore a verified backup if needed. Delete only pilot records/artifacts through explicit scoped cleanup; no broad deletion is planned.

Future organization migration is a separately accepted phase: create reversible, non-destructive clones from the AIAllTheThingz organization into `<approved-root>\AIAllTheThingz`, validate deterministic identities and operator-controlled rebinding, then migrate incrementally. No moves, deletes, or organization-wide migration occurred in the pilot; see [PILOT_RESULTS.md](PILOT_RESULTS.md).

## Known baseline caveats

No `<approved-root>` root exists today. Normal Codex project prompts excluded `<global-instructions>` in the recorded configuration probes; the desktop per-call larger budget included it, while default prompt budgets truncated it. Chunk 10 must validate the supported alternative instead of assuming cascade semantics. Existing sibling validation receipts are historical, not fresh pilot evidence.
