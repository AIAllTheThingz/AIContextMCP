# Implementation roadmap

This is the sequential plan. Chunks 0–11 are complete for the implemented foundation and controlled pilot. Organization-wide onboarding remains future work.

0. **Environment and sibling discovery** — inspect policy, configuration, tools, sibling conventions, roots, and runtime; record `BASELINE.md`. Complete.
1. **Design** — inspect this document set against `<global-instructions>`, approve boundaries, source-of-truth rules, security, contract, and pilot. Complete.
2. **Solution scaffold** — minimal .NET 10 solution, nullable, warnings, DI, logging, tests; run restore, build, and test. Complete as the historical scaffold, later extended by the storage, repository, and MCP chunks below.
3. **SQLite storage** — V4 implementation present: Microsoft.Data.Sqlite 10.0.11, explicit migrations, replay receipts, parameterized SQL, bounds, UTC, foreign keys, duplicate/supersession handling, failure classification, and isolated tests.
4. **Application core** — PASSED: bounded Windows-handle safety and working-tree fingerprint validation are complete; final Release evidence includes 33 passing Core tests.
5. **MCP server** — PASSED: SDK 2.2 stdio adapter exposes exactly eight tools with strict validation, typed errors, logging, and enumeration checks.
6. **Security and abuse testing** — PASSED: final Release validation records 75/75 passing tests; security and Pony review accepted the scoped remediation.
7. **Code-efficiency review** — PASSED for this remediation: Pony Ultra/KISS/YAGNI/DRY review accepted the implemented scope.
8. **Controlled pilot** — PASSED for one clean representative Engineering-Standards clone; bootstrap, bounded context/decision/test recording, search, handoff, fresh recovery, fingerprint, repository preservation, and sibling coexistence are evidenced in `PILOT_RESULTS.md`.
9. **MCP client configuration** — COMPLETE: distinct registration preserves `dotnetdevmcp-server`. Evidence: local validation receipts, local validation receipts, and local validation receipts
10. **Cascading policy design** — COMPLETE: actual AGENTS inheritance, shortest supported global/root guidance, overrides, opt-out, non-code behavior, and sibling coexistence are documented; no mass edits. Evidence: local validation receipts
11. **Chunk 11 — Final foundation validation** — PASSED: source Git metadata and tracked-hygiene evidence are available, the canonical artifact root is configured, and fresh Release, CLI automatic-start, cascade, and degraded-mode checks pass. Organization-wide onboarding remains deferred.

Gates: no implementation before Chunk 1 acceptance; no client registration before Chunk 8 acceptance; no organization-wide migration in this roadmap execution. Rollback for implementation is file/version rollback plus database backup restore; never rewrite Git history.

The eventual organization migration is a separately accepted phase after foundation acceptance: create reversible non-destructive clones from the AIAllTheThingz organization into `<approved-root>\AIAllTheThingz`, validate identity rebinding and each clone, and migrate incrementally. It does not move or delete existing repositories and never resets dirty user trees.
