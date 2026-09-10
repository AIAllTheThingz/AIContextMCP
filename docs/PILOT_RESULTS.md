# Pilot Results

Receipt paths are relative to `<checkout>`.

## Pilot Repository

`<approved-root>\Engineering-Standards-Chunk8Pilot`, remote `https://github.com/AIAllTheThingz/Engineering-Standards.git`, default branch `master`, current branch `feature/issue-21-governance-contract-semantics`, HEAD `45159b5c57e698190476683ac5b3f8c8c4a50de9`. The clone is clean with 0 modified, staged, or untracked files; the original dirty source was preserved.

## Why Selected

It is a real governance/code repository with 362 committed files, source, tests, workflows, documentation, and recent history. The clone was created locally from the committed HEAD with no hardlinks, alternates, shared objects, or authored commits.

## Initial Git State

Original source status is recorded in local validation receipts; its 34 dirty paths were deliberately excluded. Clone bounds are 362 files, 110 unique parent directories, 2,405,360 bytes total, maximum file 161,692 bytes, zero CR-byte files, index v2, and 234 loose objects. Additive preparation added 8,835 loose objects while preserving packs, refs, index, and source; receipts: local validation receipts, `engineering-object-preparation-addition.json`, `engineering-object-preparation-after.json`, and `engineering-unpack-receipt.json`.

## Bootstrap Result

Clean bootstrap succeeded with path, remote, branch, HEAD, and fingerprint verified. The server normalizes the remote by removing the `.git` suffix; identity otherwise matches direct Git. Project ID `4fe2a4dc-6a16-4ff0-ba20-abb11bff66b2`; repository ID `e3f7e841-4a66-446c-ae05-03adc9a4dbb3`. Receipt: local validation receipts.

## Project Identity

Fresh recovery rediscovered the same IDs and current clean state from the explicit clone path.

## Context Recorded

Two concise committed-project facts were recorded: `86be79dd-0800-4354-a63f-6ac09ea718be` (objective) and `70b9ae6f-0f3f-4133-a5b9-64287377c38f` (architecture), sourced from the clone’s `AGENTS.md`. Both were retrieved as `Current` after clean bootstrap. Narrow search `governance` returned one result; a broad search without a query filter returned both records. Receipt: local validation receipts.

## Decisions Recorded

Accepted decision `aedc0dd3-d7ce-43c7-8f8a-5fe93827763c` was recorded from `docs/migrations/ISSUE_21_CONTRACT_COMPATIBILITY_PROPOSAL.md`: implement schema version `1.2.0` with the accepted compatibility model. `decision.list` returned the decision and freshness. Rationale was verified through a read-only database check because `decision.list` omits it.

## Test Validation Recorded

One actual TestRun `1e9560a1-c9b5-4b40-ba88-af6082927ced` was recorded from `Invoke-Pester -Path tests/actions/ValidateContract.Tests.ps1 -Output Detailed -PassThru`: Pester 6.1.0, 7 passed, 0 failed, 0 skipped. Output and result are outside the clone under local validation receipts. This is local validation, not GitHub Actions evidence.

## Findings Recorded

NO PILOT FINDING RECORDED. Zero findings is not a security-audit claim.

## Handoff

One concise handoff `2f95ab00-9ffb-47e6-9ead-8f8d54deb7d8` was created with objective, completed work, active state, decision `aedc0dd3-d7ce-43c7-8f8a-5fe93827763c`, TestRun `1e9560a1-c9b5-4b40-ba88-af6082927ced`, blockers, relevant files, and next action. Receipt: local validation receipts.

## Fresh Session Recovery

Fresh task `01a0867a-c49b-7170-bd02-0161abe24fc8` recovered bootstrap, both contexts, decision, current TestRun, zero findings, and handoff as `Current` without prior conversation. Receipt: local validation receipts. Follow-up projection correction is recorded in local validation receipts.

Follow-up receipt local validation receipts verifies the corrected `validation.commit` projection equals HEAD `45159b5c57e698190476683ac5b3f8c8c4a50de9`, with current 7/0/0 validation freshness and unchanged DB/Git state.

## Context Efficiency Observation

Fresh recovery used bootstrap 2,607 bytes, context 2,043, decisions 1,093, plus initialize 171: 5,914 bytes. The 4 KiB response budget was rejected and adapted to 16 KiB. Retrieval was selective and did not require historical conversation replay.

## Current/Stale State Behavior

The clean clone produced current bootstrap and records. No artificial commit was created. Stale transitions remain covered by prior Chunk 6/7 controlled tests.

## Repository Integrity

Original source and clone integrity receipts show no unauthorized source edits. The additive loose-object preparation changed clone metadata only and preserved packs, refs, index, and source. Runtime logs and data are ignored. The workspace has no Git metadata, so tracked cleanliness is not claimed.

## MCP Coexistence

AIContextMCP’s eight-tool surface and the sibling .NET server’s fourteen-tool surface remained distinct. Sibling inspection succeeded for six projects; no client or global registration change was made.

## Observed Limitations

Observation remains bounded by 512 entries, 1 MiB files, 8 MiB aggregate reads, sensitive-path rejection, LF-only root attributes, CR-free observed files, and supported Git layouts; packed HEAD remains unsupported. The response budget 4096 was rejected and safely adapted to 16384. The helper’s validation field mapping was corrected from `commit` to `commitSha`; no server bug was found. The LF-only attribute change received independent security and Pony review.

## Failures

Earlier WindowsScriptRunner attempts retained `PathRejected`, dirty/Unknown observation, and locked default-output failures; isolated artifacts passed. The initial Engineering Standards bootstrap was Unknown because HEAD was packed; additive loose-object preparation resolved it without changing source or history. The first Engineering Standards pilot handoff path returned `PathRejected`; the dedicated corrected handoff run succeeded. Final isolated `--artifacts-path` regression passed build with 0 warnings/errors and tests 92/0/0; receipts are local validation receipts, `final-regression-build.txt`, and `final-regression-test.txt`. No live process was terminated. No GitHub Actions run or external artifact verification was claimed.

## Pilot Acceptance

PASS - READY FOR CHUNK 9
