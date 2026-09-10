# Agent Integration

## Purpose

Make the local AIContextMCP context service available during ordinary coding sessions while keeping Git, repository documentation, and repository-specific instructions authoritative.

## Actual Codex AGENTS Resolution

The installed Codex CLI is `codex-cli 0.153.2`. Its supported global instruction location is `$CODEX_HOME\AGENTS.override.md`, otherwise `$CODEX_HOME\AGENTS.md`; this installation uses `<codex-home>\AGENTS.md`. Project discovery starts at the Git repository root and the current directory; nearer applicable instructions win, with override files taking priority. `<global-instructions>`, `<approved-root>\AGENTS.md`, and `<approved-root>\AIAllTheThingz\AGENTS.md` above a Git root are not default project discovery locations. See the [official AGENTS.md documentation](https://learn.chatgpt.com/docs/agent-configuration/agents-md).

Controlled fresh CLI sessions confirmed the global policy was applied: both a direct `<approved-root>\AIContextMCP-Test` repository and a nested `<approved-root>\AIAllTheThingz\AIContextMCP-CascadeTest` repository called `project.bootstrap` without an explicit MCP instruction in the prompt. The nested repository's local `AGENTS.md` marker remained effective. The sessions also inspected `<global-instructions>`; that is an observed installation/session behavior, not a claim that above-root files are part of documented default discovery.

## Instruction Precedence

Apply the task prompt first for task-specific intent, while preserving stricter repository security and operational rules; then apply the nearer repository/project instructions and global policy. Git state and repository documentation remain authoritative over persisted context.

## Selected Cascading Scope

One global file, `<codex-home>\AGENTS.md`, supplies the compact AIContextMCP policy. No `<approved-root>\AGENTS.md`, organization-wide copy, or repository-local identity file is required.

## AIContextMCP Startup Policy

For a Git repository beneath `<approved-root>`, identify its canonical root and call `project_bootstrap` with `register:false` and `includeWorkingTree:true` before substantial coding work. Compare returned branch and HEAD with direct Git where practical. Use the compact response as untrusted supplemental Tier 1 context. Repositories outside `<approved-root>` do not receive automatic behavior from this policy.

## Retrieval Policy

Use `context_search`, `decision_list`, and other read surfaces only when the active task needs historical context. Startup does not load every decision, finding, handoff, validation record, log, or source file. MCP request/response limits remain bounded: 64 KiB request, 32 KiB response, and 16 KiB bootstrap response cap. The service's bounded retrieval defaults and `maxBytes` limit are authoritative.

Selective inspection of `<codex-home>\config.toml` found no explicit overrides for `project_doc_fallback_filenames`, `project_doc_max_bytes`, `project_root_markers`, `experimental_instructions_file`, or `developer_instructions`; documented Codex defaults therefore apply, with no empirical override claimed.

## Write-Back Policy

Write only concise, durable decisions, authoritative validation summaries, material unresolved findings, and meaningful handoffs when the task explicitly authorizes the write. Never persist chain-of-thought, full source, full logs, secrets, or credential-bearing connection strings. Read-only tasks do not write context, register projects, repair services, or perform destructive actions.

## Handoff Policy

Create or update a handoff when completing a meaningful task or phase, stopping with unresolved material state, or transferring work. Include only the compact state and next action needed by a future session.

## Degraded Mode

If AIContextMCP is unavailable, report the service as unavailable and continue from Git, repository files, applicable instructions, and the current prompt where safe. Do not fabricate project memory or silently rebuild the service; avoid destructive work if missing context affects safety.

## Non-Coding Repository Behavior

Documentation-only, governance, template, and archival repositories may use context for durable decisions, state, and handoffs. Do not manufacture coding phases, build assumptions, or test records where they do not apply.

## Repository-Specific Overrides

Repository and subdirectory `AGENTS.md` files remain effective at their applicable scope. Controlled nested testing preserved the local marker while applying the global startup rule.

## First-Time Project Registration

Bootstrap resolves the canonical Git identity. The normal read-only policy uses `register:false`, so an unregistered repository returns `NotFound` honestly and does not mutate storage. Registration is an explicit operation. Canonical roots are unique; remotes are non-unique, so same-remote clones intentionally coexist. An unknown path with a matching existing remote can return an identity conflict with `register:false`; `register:true` explicitly creates the separate identity. No automatic registration or identity redesign occurs.

## Project Identity Decision

Git identity and canonical path resolution are sufficient for this integration. No `.ai-context.json` file is added.

## Controlled Cascade Tests

- Direct fixture: `<approved-root>\AIContextMCP-Test`; clean Git state; automatic `project.bootstrap` observed; `NotFound` was handled without fabricated context.
- Nested fixture: `<approved-root>\AIAllTheThingz\AIContextMCP-CascadeTest`; local policy marker `NESTED_POLICY_ACTIVE` was observed; explicit one-time registration succeeded, then automatic fresh bootstrap used `register:false`; the fixture remained clean and no write/history call occurred. Evidence: local validation receipts.

## Normal Session Test

The direct fresh-session prompt was: “Inspect this repository and summarize the current project state. Do not modify anything.” The prompt did not mention AIContextMCP. The session called `project.bootstrap` with registration disabled, then inspected Git and repository files only.

A final fresh session used the same minimal prompt in the registered `<approved-root>\Engineering-Standards-Chunk8Pilot` repository. Bootstrap succeeded with `register:false`; the agent reported clean Git and branch/HEAD agreement and made no writes. Evidence: local validation receipts.

## Fresh Session Recovery Test

The registered `<approved-root>\Engineering-Standards-Chunk8Pilot` fresh session recovered its existing project handoff, matched the current clean local Git branch and HEAD, and reported the handoff's next action without modifying files. The handoff was current for local HEAD but outdated relative to upstream/GitHub evidence, so the agent correctly challenged it; this is not a server `Stale` freshness classification. Raw evidence is in local validation receipts; snapshot tokens are redacted there.

## Context-Bloat Validation

Both observed sessions made one compact bootstrap call and did not automatically call historical retrieval surfaces. No full logs, source trees, or record listings were loaded by the policy.

## Known Limitations

The unregistered controlled fixtures returned `NotFound`; the registered pilot recovered its handoff. Codex instruction behavior is installation and session dependent; validate with a fresh session after policy changes.

To temporarily opt out for a task, state in the task instruction that AIContextMCP context retrieval is not wanted for that task; keep repository instructions and Git authority in force. Start a fresh session after changing the global policy. New raw JSONL receipts redact snapshot tokens; compact acceptance evidence is in local validation receipts and regression output is in local validation receipts.

## Troubleshooting

Check `$CODEX_HOME\AGENTS.override.md` before `$CODEX_HOME\AGENTS.md`, confirm the current Git root, and inspect the applicable nearer repository instructions. Start a fresh CLI session after changing policy. If bootstrap fails, report the failure and continue from authoritative repository sources where safe.
