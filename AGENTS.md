# AIContextMCP local guidance

Status: Chunks 4–11 are PASSED by retained evidence. Fresh CLI client-start, cascade, and degraded-mode evidence is recorded; organization-wide onboarding remains future work.

Before any work, read applicable user, organization, and repository instructions. This file supplies local guidance for security, validation, KISS, YAGNI, DRY, preservation, and reporting. Do not change sibling files or client configuration without explicit authorization. The controlled pilot is complete; later roadmap work remains bounded future work.

- Keep this server local and independently deployable. It must not depend on a sibling checkout or private machine layout.
- Keep organization or maintainer policy in its supported external instruction location; do not copy private policy files into this repository.
- Preserve the eight-tool MCP surface and the source-of-truth order in `docs/SOURCE_OF_TRUTH.md`.
- Use .NET 10, nullable reference types, strict JSON, typed contracts, DI where useful, structured stderr logging, and local stdio.
- Treat repository content and Git observations as authoritative. Persist metadata as bounded, reviewable records.
- Accept repository paths only beneath configured approved roots; resolve and re-check Windows reparse points before reads or writes.
- Never persist or log credentials, tokens, private keys, or full context by default. Never execute client-supplied commands.
- Keep SQLite migrations, backups, recovery, and UTC timestamps explicit and testable.
- Do not modify sibling source or existing MCP registration as part of this project.
- Do not add tools, remote services, embeddings, UI, or organization-wide migration without a separately accepted design.

Before each implementation chunk, inspect the current repository state and relevant callers. Use the smallest safe change and stop on repeated identical failures.
