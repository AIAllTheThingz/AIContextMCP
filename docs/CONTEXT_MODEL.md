# Context model

## Future / Planned context behavior

The model has three tiers:

| Tier | Purpose | Typical contents |
|---|---|---|
| 1 Active/bootstrap | Compact startup context, target roughly 1,000–4,000 tokens | Project identity, repository path/remote, branch, HEAD, phase, objective, blockers, latest authoritative validation, a few applicable decisions, references |
| 2 Retrievable memory | Relevant project memory on demand | Decisions, roadmap, test summaries, findings, handoffs, troubleshooting, issue/PR references, component metadata |
| 3 Cold/authoritative | Large material represented by references | Full logs, source trees, command output, Git history, reports, generated artifacts |

Every entry has a project ID, kind, bounded content or artifact reference, source, created/observed UTC time, optional Git state, and lifecycle status. Search defaults to Tier 2 summaries and references, applies project and state filters, caps result count and bytes, and never loads the entire database. There is no content-fetch tool; large material is referenced as a server-owned artifact only.

Bootstrap validation uses one persisted TestRun: repository-scoped and project-scoped runs matching the current branch and HEAD are merged and the newest is selected; if none match, repository and project historical runs are merged and the newest is selected. Runs without an optional duration remain eligible. A historical HEAD remains available and is labeled stale after the repository changes. The bootstrap form is compact metadata (ID, status, counts, branch, commit, freshness, and created time); test output and logs stay in storage or referenced artifacts.

Record lifecycle is `active`, `superseded`, or `archived`; freshness is separately `current`, `stale`, or `unknown`. A new decision can supersede an earlier one only with an explicit relationship; historical records remain immutable. Entries tied to a branch/HEAD become stale when the repository changes. If Git cannot be observed, freshness is `unknown`, never silently current.

Retention and compaction are planned: retain current summaries and references, archive cold artifacts, and avoid storing conversational archaeology. Sensitive content is rejected or redacted at the trust boundary.

## Historical V1 schema design

The following V1 schema is retained as historical design guidance. The current implementation is SQLite V5. IDs, timestamps, and revision behavior below describe the earlier design and must not be read as the current column set.

The V5 data-only migration retires active predecessors only for exact-scope, valid supersession references and preserves observations and historical rows. Default context search excludes superseded entries; an explicit superseded status retrieves that history. Rollback to an older executable requires a database backup taken before the V5 migration.

| Table | Columns and key behavior |
|---|---|
| `SchemaVersion` | `Version` primary key, `MigrationId` unique, `AppliedUtc` |
| `Projects` | `Id` primary key, `StableProjectId` unique, `Name`, `Status`, `CreatedUtc`, `UpdatedUtc` |
| `Repositories` | `Id` primary key, `ProjectId`, `CanonicalPath` unique, `Remote`, `Organization`, `RepositoryName`, `DefaultBranch`, `LastKnownBranch`, `LastKnownCommitSha`, `Status`, timestamps; project FK |
| `ContextEntries` | `Id` primary key, `ProjectId`, optional `RepositoryId`, `Category`, `Title`, `Summary`, optional `Content`, `AuthoritativeReference`, `Branch`, `CommitSha`, `Status`, `ContentHash`, timestamps, optional `SupersededUtc`; project/repository FKs |
| `Decisions` | `Id` primary key, `ProjectId`, optional `RepositoryId`, `Category`, `Component`, `Title`, `DecisionText`, `Rationale`, `AuthoritativeReference`, `ResolutionEvidence`, `OriginatingCommitSha`, `Status`, optional supersession IDs, timestamps, optional `SupersededUtc`; project/repository/self FKs |
| `Phases` | `Id` primary key, `ProjectId`, optional `RepositoryId`, `PhaseKey`, `Objective`, `Status`, `Branch`, `CommitSha`, optional `StartedUtc`/`CompletedUtc`, timestamps; unique `(ProjectId, PhaseKey)` and one active phase per project |
| `TestRuns` | `Id` primary key, `ProjectId`, optional `RepositoryId`/`ArtifactId`, `CommandText`, `Branch`, `CommitSha`, `Passed`, `Failed`, `Skipped`, `Total`, `Status`, `ArtifactReference`, optional start/completion timestamps, `CreatedUtc` |
| `Findings` | `Id`, `Revision`, `ProjectId`, optional `RepositoryId`, `Category`, `Title`, `Description`, `Severity`, `Status`, `Resolution`, `ResolutionEvidence`, `CreatedUtc`, `UpdatedUtc`; composite `(Id, Revision)` primary key |
| `Artifacts` | `Id` primary key, `ProjectId`, optional `RepositoryId`, `ArtifactType`, `Title`, `Reference`, `ContentHash`, `SizeBytes`, `Branch`, `CommitSha`, timestamps; unique `(ProjectId, Reference, ContentHash)` |
| `Handoffs` | `Id` primary key, `ProjectId`, optional `RepositoryId`, `Branch`, `CommitSha`, bounded handoff text fields, JSON reference lists, timestamps |

V1 created these tables and indexes in one transaction; later V2–V5 migrations added repository state, replay receipts, MCP record observations/references, current extension columns, and bounded supersession repair. Unsupported or malformed state is rejected. The current V5 implementation provides storage-backed MCP operations; automated backup remains future work.

## Planned context model

All IDs are opaque server-generated GUID text keys, foreign keys are enforced, timestamps are ISO-8601 UTC, and all tables carry `createdUtc`; mutable records also carry `updatedUtc` and `version`.

| Entity | Core fields and keys | Foreign keys, uniqueness, indexes |
|---|---|---|
| `SchemaVersion` | `version` PK, `appliedUtc`, `migrationId` | unique migration ID; one row per migration |
| `Projects` | `projectId` server-generated GUID PK, `displayName`, `canonicalRoot`, `status`, `createdUtc`, `version` | unique canonical root globally; remote is a nonunique lookup index; index status |
| `Repositories` | `repositoryId` server-generated GUID PK, `projectId`, `canonicalRoot`, `remote`, `worktreeKind`, `gitDir`, `commondir`, `createdUtc`, `version` | FK project; unique canonical root globally; nonunique remote index and project index |
| `ContextEntries` | `entryId` PK, `projectId`, `repositoryId?`, `category`, `tier`, `branch?`, `commit?`, `summary`, `content?`, `artifactId?`, `source`, `lifecycle`, `freshness`, `observedUtc`, `version` | FKs project/repository/artifact; index `(projectId,category,lifecycle,freshness,observedUtc)` |
| `Decisions` | `decisionId` PK, `projectId`, `repositoryId?`, `title`, `decision`, `rationale`, `status`, `supersedesId?`, `resolutionEvidence?`, `observedCommit?`, `version`, timestamps | FK/self-FK; index project/status; supersession is append-only and explicit |
| `Phases` | `phaseId` PK, `projectId`, `name`, `objective`, `status`, `startedUtc`, `completedUtc?`, `version` | FK project; unique `(projectId,name,version)`; index active status |
| `TestRuns` | `testRunId` PK, `projectId`, `repositoryId?`, `name`, `commandData`, `status`, `passed`, `failed`, `skipped`, `durationMs?`, `summary`, `evidenceArtifactId?`, `source`, `runSnapshot`, `observedUtc`, `version` | FKs; index project/status/observedUtc; run snapshot is historical |
| `Findings` | composite PK `(findingId,version)`, `projectId`, `repositoryId?`, `title`, `severity`, `status`, `description`, `location?`, `resolutionEvidence?`, `observedCommit?`, timestamps | FKs; index logical finding/project/status/severity; each update appends a revision and reads highest version |
| `Artifacts` | `artifactId` PK, `projectId`, `repositoryId?`, `relativePath`, `mediaType`, `byteLength`, `sha256`, `owner`, `createdUtc`, `version` | FKs; unique `(projectId,relativePath,sha256)`; index project/hash |
| `Handoffs` | `handoffId` PK, `projectId`, `repositoryId?`, `phaseId?`, `objective`, `completedWork`, `activeWork`, `decisions`, `tests`, `findings`, `relevantFiles`, `artifacts`, `nextAction`, `observedCommit?`, `createdUtc`, `version` | FKs; index project/phase/createdUtc |

`lifecycle` describes record retention (`active`, `superseded`, `archived`); `freshness` describes evidence (`current`, `stale`, `unknown`). They are separate and never inferred from one another. Phase writes map to `Phases`; bootstrap selects the active phase and latest current evidence. Decisions use `Proposed`, `Accepted`, `Rejected`, `Superseded`, or `Withdrawn`; supersession points to one prior decision and requires resolution evidence. Findings use exactly `Open`, `Resolved`, `Accepted`, or `Superseded`; every status transition is version-checked and `Resolved`/`Superseded` requires resolution evidence. Artifact ownership is the project plus optional repository; paths are relative to the controlled artifact root.

Project and repository identities are server-generated GUIDs and persist across observations. First registration binds a project ID to one canonical root. An existing project ID with a new repository is never auto-attached; unknown selectors error, and rebind is an explicit operator action. A `phase` context entry may omit `phaseId` on create (the server generates it); updates require phaseId and expectedVersion, plus `name`, `objective`, and status `Planned|Active|Completed|Blocked`. One active phase per project is enforced and the phase row plus entry write atomically. Supersession rejects self-links, cycles, cross-scope targets, and invalid target IDs atomically.

The preceding model is retained as design guidance for later application work; it is not a statement of the current V1 column set or behavior.
