using System.Diagnostics;
using AIContextMCP.Core;
using AIContextMCP.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AIContextMCP.Storage.Sqlite.Tests;

public sealed class SqliteContextStorageTests
{
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task InitializeCreatesSchemaIndexesAndEnforcedForeignKeys()
    {
        await using var database = TemporaryDatabase.Create();
        await database.Storage.InitializeAsync();

        Assert.Equal(4, await database.Storage.GetSchemaVersionAsync());
        await using var connection = await OpenAsync(database.DatabasePath);
        var tables = await StringsAsync(connection, "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;");
        Assert.Subset(tables.ToHashSet(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal)
        {
            "SchemaVersion", "Projects", "Repositories", "ContextEntries", "Decisions", "Phases", "TestRuns", "Findings", "Artifacts", "Handoffs", "MutationReceipts", "McpRecordObservations", "McpRecordReferences"
        });
        var indexes = await StringsAsync(connection, "SELECT name FROM sqlite_master WHERE type = 'index';");
        Assert.Contains("IX_Decisions_SupersedesDecisionId", indexes);
        Assert.Contains("IX_Decisions_Repository_Branch_CommitSha", indexes);
        Assert.Contains("IX_Findings_Project_Status_Severity", indexes);
        Assert.Contains("IX_Findings_Repository_Branch_CommitSha", indexes);
        Assert.Contains("IX_TestRuns_Project_Status_CreatedUtc", indexes);
        Assert.Contains("IX_MutationReceipts_ExpiresUtc", indexes);
        Assert.Contains("IX_McpRecordObservations_Project_Kind_ObservedUtc_Id", indexes);
        Assert.Contains("IX_McpRecordReferences_ReferenceScope", indexes);

        await ExecuteAsync(connection, "PRAGMA foreign_keys = ON;");
        Assert.Equal(1L, await ScalarLongAsync(connection, "PRAGMA foreign_keys;"));
        Assert.NotEmpty(await StringsAsync(connection, "SELECT 'fk' FROM pragma_foreign_key_list('ContextEntries');"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "INSERT INTO Repositories (Id, ProjectId, CanonicalPath, RepositoryName, DefaultBranch, Status, CreatedUtc, UpdatedUtc) VALUES ('00000000-0000-0000-0000-000000000001', '00000000-0000-0000-0000-000000000002', 'D:/missing', 'missing', 'main', 0, '2026-01-01T00:00:00.0000000+00:00', '2026-01-01T00:00:00.0000000+00:00');"));
        Assert.Equal("ok", await ScalarStringAsync(connection, "PRAGMA integrity_check;"));
    }

    [Fact]
    public async Task InitializeIsIdempotentAndPreservesExistingData()
    {
        await using var database = TemporaryDatabase.Create();
        await database.Storage.InitializeAsync();
        var project = await database.Storage.CreateProjectAsync(Project());
        await database.Storage.InitializeAsync();

        Assert.Equal(project, await database.Storage.GetProjectAsync(project.Id));
    }

    [Fact]
    public async Task InitializeExplicitlyMigratesV2WhileOrdinaryReadsRemainReadOnly()
    {
        await using var database = TemporaryDatabase.Create();
        await database.Storage.InitializeAsync();
        var project = await database.Storage.CreateProjectAsync(Project());
        var repository = await database.Storage.CreateRepositoryAsync(Repository(project.Id));
        await using (var connection = await OpenAsync(database.DatabasePath))
        {
            await ExecuteAsync(connection, """
                DROP TABLE McpRecordReferences;
                DROP TABLE McpRecordObservations;
                DROP TABLE MutationReceipts;
                ALTER TABLE ContextEntries DROP COLUMN Tier;
                ALTER TABLE ContextEntries DROP COLUMN Objective;
                ALTER TABLE ContextEntries DROP COLUMN PhaseId;
                ALTER TABLE Decisions DROP COLUMN Version;
                ALTER TABLE Phases DROP COLUMN Version;
                ALTER TABLE Phases DROP COLUMN ContextEntryId;
                ALTER TABLE TestRuns DROP COLUMN Name;
                ALTER TABLE TestRuns DROP COLUMN Summary;
                ALTER TABLE TestRuns DROP COLUMN Evidence;
                ALTER TABLE TestRuns DROP COLUMN ObservedUtc;
                ALTER TABLE Findings DROP COLUMN Component;
                ALTER TABLE Findings DROP COLUMN Location;
                ALTER TABLE Findings DROP COLUMN Remediation;
                ALTER TABLE Handoffs DROP COLUMN PhaseId;
                UPDATE SchemaVersion SET Version = 2, MigrationId = 'v2';
                """);
        }

        var uninitialized = await Assert.ThrowsAsync<StorageException>(() => database.Storage.GetSchemaVersionAsync());
        Assert.Equal(StorageErrorCode.UnsupportedSchema, uninitialized.Code);
        await using (var beforeMigration = await OpenAsync(database.DatabasePath))
        {
            Assert.Equal(2L, await ScalarLongAsync(beforeMigration, "SELECT Version FROM SchemaVersion;"));
        }

        await database.Storage.InitializeAsync();
        Assert.Equal(4, await database.Storage.GetSchemaVersionAsync());
        Assert.Equal(project, await database.Storage.GetProjectAsync(project.Id));
        Assert.Equal(repository, await database.Storage.GetRepositoryAsync(repository.Id));
        var decision = await database.Storage.CreateDecisionAsync(new DecisionDraft(Guid.NewGuid(), project.Id, repository.Id, "architecture", "core", "v3", "store branch", "because scope", null, null, "abcdef1", DecisionStatus.Accepted, null, "main"));
        var finding = await database.Storage.CreateFindingAsync(new FindingDraft(Guid.NewGuid(), project.Id, repository.Id, FindingCategory.Security, "v3", "store finding scope", FindingSeverity.High, FindingStatus.Open, null, null, "main", "abcdef1", "notes/finding.md"));
        Assert.Equal("main", decision.Branch);
        Assert.Equal("main", finding.Branch);
        Assert.Equal("abcdef1", finding.CommitSha);
        Assert.Equal("notes/finding.md", finding.AuthoritativeReference);
    }

    [Fact]
    public async Task ProjectsAndRepositoriesRoundTripWithStableIdentityAndUtc()
    {
        await using var database = TemporaryDatabase.Create();
        await database.Storage.InitializeAsync();
        var project = await database.Storage.CreateProjectAsync(Project());
        var updated = await database.Storage.UpdateProjectAsync(project.Id, "Updated project", ProjectStatus.Archived);
        var repository = await database.Storage.CreateRepositoryAsync(Repository(project.Id));

        Assert.Equal(TimeSpan.Zero, project.CreatedUtc.Offset);
        Assert.Equal(TimeSpan.Zero, updated.UpdatedUtc.Offset);
        Assert.Equal(updated, await database.Storage.GetProjectByStableIdAsync(project.StableProjectId));
        Assert.Equal(repository, await database.Storage.GetRepositoryAsync(repository.Id));

        var duplicateProject = await Assert.ThrowsAsync<StorageException>(() => database.Storage.CreateProjectAsync(Project(stableProjectId: project.StableProjectId)));
        Assert.Equal(StorageErrorCode.Duplicate, duplicateProject.Code);
        var duplicateRepository = await Assert.ThrowsAsync<StorageException>(() => database.Storage.CreateRepositoryAsync(Repository(project.Id, canonicalPath: repository.CanonicalPath)));
        Assert.Equal(StorageErrorCode.Duplicate, duplicateRepository.Code);

        var rolledBackProjectId = Guid.NewGuid();
        var registration = await Assert.ThrowsAsync<StorageException>(() => database.Storage.RegisterProjectAsync(
            Project(rolledBackProjectId), Repository(rolledBackProjectId, canonicalPath: repository.CanonicalPath)));
        Assert.Equal(StorageErrorCode.Duplicate, registration.Code);
        Assert.Null(await database.Storage.GetProjectAsync(rolledBackProjectId));
    }

    [Fact]
    public async Task ContextEntriesAreBoundedParameterizedAndSecretScreened()
    {
        await using var database = TemporaryDatabase.Create();
        await database.Storage.InitializeAsync();
        var project = await database.Storage.CreateProjectAsync(Project());
        var repository = await database.Storage.CreateRepositoryAsync(Repository(project.Id));
        var title = "x'); DROP TABLE Projects; --";
        var entry = await database.Storage.CreateContextEntryAsync(new ContextEntryDraft(Guid.NewGuid(), project.Id, repository.Id, "documentation", title, "safe summary", "bounded content", "docs/plan.md", "main", "0123456", ContextStatus.Active, Hash));

        Assert.Equal(title, (await database.Storage.GetContextEntryAsync(entry.Id))!.Title);
        Assert.NotNull(await database.Storage.GetProjectAsync(project.Id));
        var duplicate = await Assert.ThrowsAsync<StorageException>(() => database.Storage.CreateContextEntryAsync(new ContextEntryDraft(entry.Id, project.Id, repository.Id, "documentation", "duplicate", "summary", null, null, null, null, ContextStatus.Active, null)));
        Assert.Equal(StorageErrorCode.Duplicate, duplicate.Code);
        var oversized = await Assert.ThrowsAsync<StorageException>(() => database.Storage.CreateContextEntryAsync(new ContextEntryDraft(Guid.NewGuid(), project.Id, repository.Id, "documentation", "title", "summary", new string('x', StorageLimits.Content + 1), null, null, null, ContextStatus.Active, null)));
        Assert.Equal(StorageErrorCode.LimitExceeded, oversized.Code);

        var secretId = Guid.NewGuid();
        var secret = await Assert.ThrowsAsync<StorageException>(() => database.Storage.CreateContextEntryAsync(new ContextEntryDraft(secretId, project.Id, repository.Id, "documentation", "title", "summary", "api_key=not-a-real-key", null, null, null, ContextStatus.Active, null)));
        Assert.Equal(StorageErrorCode.SecretRejected, secret.Code);
        Assert.DoesNotContain("not-a-real-key", secret.Message, StringComparison.Ordinal);
        Assert.Null(await database.Storage.GetContextEntryAsync(secretId));

        var credentialedRemote = Repository(project.Id, canonicalPath: "D:\\Projects\\credentialed") with { Remote = "https://user:fakepass@example.invalid/repo.git" };
        var remoteError = await Assert.ThrowsAsync<StorageException>(() => database.Storage.CreateRepositoryAsync(credentialedRemote));
        Assert.Equal(StorageErrorCode.SecretRejected, remoteError.Code);
        var commandError = await Assert.ThrowsAsync<StorageException>(() => database.Storage.CreateTestRunAsync(new TestRunDraft(Guid.NewGuid(), project.Id, repository.Id, "pwd=synthetic-only", null, null, 0, 0, 0, 0, TestRunStatus.Unknown, null, null, null, null)));
        Assert.Equal(StorageErrorCode.SecretRejected, commandError.Code);
        var jsonSecret = await Assert.ThrowsAsync<StorageException>(() => database.Storage.CreateContextEntryAsync(new ContextEntryDraft(Guid.NewGuid(), project.Id, repository.Id, "documentation", "title", "summary", "{\"password\":\"synthetic-only\"}", null, null, null, ContextStatus.Active, null)));
        Assert.Equal(StorageErrorCode.SecretRejected, jsonSecret.Code);
    }

    [Fact]
    public async Task DecisionsSupersedeTransactionallyAndRetainHistory()
    {
        await using var database = TemporaryDatabase.Create();
        await database.Storage.InitializeAsync();
        var project = await database.Storage.CreateProjectAsync(Project());
        var oldDecision = await database.Storage.CreateDecisionAsync(Decision(project.Id, "Use SQLite"));
        var replacement = await database.Storage.CreateDecisionAsync(Decision(project.Id, "Keep SQLite", supersedesDecisionId: oldDecision.Id, resolutionEvidence: "SQLite remains the approved local store."));

        var oldAfter = await database.Storage.GetDecisionAsync(oldDecision.Id);
        Assert.Equal(DecisionStatus.Superseded, oldAfter!.Status);
        Assert.Equal(replacement.Id, oldAfter.SupersededByDecisionId);
        Assert.NotNull(oldAfter.SupersededUtc);
        Assert.Single(await database.Storage.ListDecisionsAsync(project.Id));
        Assert.Equal(2, (await database.Storage.ListDecisionsAsync(project.Id, includeSuperseded: true)).Count);

        var selfId = Guid.NewGuid();
        var self = await Assert.ThrowsAsync<StorageException>(() => database.Storage.CreateDecisionAsync(Decision(project.Id, "bad", id: selfId, supersedesDecisionId: selfId, resolutionEvidence: "evidence")));
        Assert.Equal(StorageErrorCode.Validation, self.Code);
        var missing = await Assert.ThrowsAsync<StorageException>(() => database.Storage.CreateDecisionAsync(Decision(project.Id, "missing", supersedesDecisionId: Guid.NewGuid(), resolutionEvidence: "evidence")));
        Assert.Equal(StorageErrorCode.NotFound, missing.Code);

        var otherProject = await database.Storage.CreateProjectAsync(Project());
        var crossProject = await Assert.ThrowsAsync<StorageException>(() => database.Storage.CreateDecisionAsync(Decision(otherProject.Id, "cross", supersedesDecisionId: oldDecision.Id, resolutionEvidence: "evidence")));
        Assert.Equal(StorageErrorCode.CrossProjectReference, crossProject.Code);

        var unsuperseded = await database.Storage.CreateDecisionAsync(Decision(project.Id, "keep this"));
        var duplicateId = Guid.NewGuid();
        await database.Storage.CreateDecisionAsync(Decision(project.Id, "existing", id: duplicateId));
        var failed = await Assert.ThrowsAsync<StorageException>(() => database.Storage.CreateDecisionAsync(Decision(project.Id, "duplicate", id: duplicateId, supersedesDecisionId: unsuperseded.Id, resolutionEvidence: "evidence")));
        Assert.Equal(StorageErrorCode.Duplicate, failed.Code);
        Assert.Equal(DecisionStatus.Accepted, (await database.Storage.GetDecisionAsync(unsuperseded.Id))!.Status);
    }

    [Fact]
    public async Task PhasesArtifactsAndTestSummariesRoundTripWithinScope()
    {
        await using var database = TemporaryDatabase.Create();
        await database.Storage.InitializeAsync();
        var project = await database.Storage.CreateProjectAsync(Project());
        var repository = await database.Storage.CreateRepositoryAsync(Repository(project.Id));
        var phase = await database.Storage.CreatePhaseAsync(new PhaseDraft(Guid.NewGuid(), project.Id, repository.Id, "chunk-3", "Persist metadata", PhaseStatus.Completed, "main", "0123456", DateTimeOffset.Now.AddMinutes(-1), DateTimeOffset.Now));
        var artifact = await database.Storage.CreateArtifactAsync(new ArtifactDraft(Guid.NewGuid(), project.Id, repository.Id, "test-report", "Test report", "reports/test-summary.json", Hash, 42, "main", "0123456"));
        var testRun = await database.Storage.CreateTestRunAsync(new TestRunDraft(Guid.NewGuid(), project.Id, repository.Id, "dotnet test", "main", "0123456", 4, 0, 0, 4, TestRunStatus.Passed, artifact.Id, null, DateTimeOffset.Now.AddMinutes(-1), DateTimeOffset.Now));

        Assert.Equal(phase, await database.Storage.GetPhaseAsync(phase.Id));
        Assert.Equal(TimeSpan.Zero, phase.StartedUtc!.Value.Offset);
        Assert.Equal(artifact, await database.Storage.GetArtifactAsync(artifact.Id));
        Assert.Equal(testRun, await database.Storage.GetTestRunAsync(testRun.Id));

        var secondRepository = await database.Storage.CreateRepositoryAsync(Repository(project.Id, canonicalPath: "D:\\Projects\\other"));
        var mismatch = await Assert.ThrowsAsync<StorageException>(() => database.Storage.CreateTestRunAsync(new TestRunDraft(Guid.NewGuid(), project.Id, secondRepository.Id, "dotnet test", "main", "0123456", 1, 0, 0, 1, TestRunStatus.Passed, artifact.Id, null, null, null)));
        Assert.Equal(StorageErrorCode.CrossProjectReference, mismatch.Code);
        var duplicateArtifact = await Assert.ThrowsAsync<StorageException>(() => database.Storage.CreateArtifactAsync(new ArtifactDraft(artifact.Id, project.Id, repository.Id, "test-report", "Duplicate", "reports/duplicate.json", Hash, 1, null, null)));
        Assert.Equal(StorageErrorCode.Duplicate, duplicateArtifact.Code);
    }

    [Fact]
    public async Task FindingsPreserveRevisionsAndResolutionEvidence()
    {
        await using var database = TemporaryDatabase.Create();
        await database.Storage.InitializeAsync();
        var project = await database.Storage.CreateProjectAsync(Project());
        var finding = await database.Storage.CreateFindingAsync(new FindingDraft(Guid.NewGuid(), project.Id, null, FindingCategory.Security, "Input validation", "Validate all persisted fields.", FindingSeverity.High, FindingStatus.Open, null, null));
        var resolved = await database.Storage.ReviseFindingAsync(new FindingRevision(finding.Id, finding.Revision, FindingCategory.Security, "Input validation", "Validated all persisted fields.", FindingSeverity.High, FindingStatus.Resolved, "Fixed", "Tests cover synthetic secrets."));
        var superseded = await database.Storage.ReviseFindingAsync(new FindingRevision(finding.Id, resolved.Revision, FindingCategory.Security, "Input validation", "Replaced by a newer check.", FindingSeverity.Info, FindingStatus.Superseded, "Replaced", "Superseded by current validation."));

        Assert.Equal(superseded, await database.Storage.GetFindingAsync(finding.Id));
        Assert.Equal(3, (await database.Storage.ListFindingHistoryAsync(finding.Id)).Count);
        var missingEvidence = await Assert.ThrowsAsync<StorageException>(() => database.Storage.CreateFindingAsync(new FindingDraft(Guid.NewGuid(), project.Id, null, FindingCategory.Testing, "test", "description", FindingSeverity.Low, FindingStatus.Resolved, "fixed", null)));
        Assert.Equal(StorageErrorCode.Validation, missingEvidence.Code);
        var accepted = await database.Storage.CreateFindingAsync(new FindingDraft(Guid.NewGuid(), project.Id, null, FindingCategory.Documentation, "doc", "description", FindingSeverity.Info, FindingStatus.Accepted, "accepted", null));
        Assert.Equal(FindingStatus.Accepted, accepted.Status);
    }

    [Fact]
    public async Task HandoffsStoreConciseFieldsAndEnforceAggregateLimit()
    {
        await using var database = TemporaryDatabase.Create();
        await database.Storage.InitializeAsync();
        var project = await database.Storage.CreateProjectAsync(Project());
        var handoff = await database.Storage.CreateHandoffAsync(Handoff(project.Id));

        var loaded = await database.Storage.GetHandoffAsync(handoff.Id);
        Assert.NotNull(loaded);
        Assert.Equal(handoff.Id, loaded.Id);
        Assert.Equal(handoff.Objective, loaded.Objective);
        Assert.Equal(handoff.RelevantFiles, loaded.RelevantFiles);
        Assert.Equal(handoff.RelevantArtifacts, loaded.RelevantArtifacts);
        var duplicate = await Assert.ThrowsAsync<StorageException>(() => database.Storage.CreateHandoffAsync(Handoff(project.Id) with { Id = handoff.Id }));
        Assert.Equal(StorageErrorCode.Duplicate, duplicate.Code);
        var oversized = Handoff(project.Id) with
        {
            Objective = new string('x', StorageLimits.HandoffField),
            CompletedWork = new string('x', StorageLimits.HandoffField),
            ActiveWork = new string('x', StorageLimits.HandoffField),
            ActiveBlockers = new string('x', StorageLimits.HandoffField),
            ImportantDecisions = new string('x', StorageLimits.HandoffField)
        };
        var error = await Assert.ThrowsAsync<StorageException>(() => database.Storage.CreateHandoffAsync(oversized));
        Assert.Equal(StorageErrorCode.LimitExceeded, error.Code);
    }

    [Fact]
    public async Task RejectsUnsupportedMalformedUnavailableAndBusyDatabasesWithoutRecreation()
    {
        await using (var future = TemporaryDatabase.Create())
        {
            await future.Storage.InitializeAsync();
            await using var connection = await OpenAsync(future.DatabasePath);
            await ExecuteAsync(connection, "UPDATE SchemaVersion SET Version = 5;");
            var error = await Assert.ThrowsAsync<StorageException>(() => future.Storage.GetSchemaVersionAsync());
            Assert.Equal(StorageErrorCode.UnsupportedSchema, error.Code);
        }

        await using (var corrupt = TemporaryDatabase.Create())
        {
            await File.WriteAllTextAsync(corrupt.DatabasePath, "not a sqlite database");
            var before = await File.ReadAllTextAsync(corrupt.DatabasePath);
            var error = await Assert.ThrowsAsync<StorageException>(() => corrupt.Storage.InitializeAsync());
            Assert.Equal(StorageErrorCode.DatabaseCorrupt, error.Code);
            Assert.Equal(before, await File.ReadAllTextAsync(corrupt.DatabasePath));
        }

        await using (var malformed = TemporaryDatabase.Create())
        {
            await malformed.Storage.InitializeAsync();
            await using var connection = await OpenAsync(malformed.DatabasePath);
            await ExecuteAsync(connection, "DROP INDEX IX_Decisions_SupersedesDecisionId;");
            var error = await Assert.ThrowsAsync<StorageException>(() => malformed.Storage.GetSchemaVersionAsync());
            Assert.Equal(StorageErrorCode.DatabaseCorrupt, error.Code);
        }

        await using (var busy = TemporaryDatabase.Create())
        {
            await busy.Storage.InitializeAsync();
            await using var connection = await OpenAsync(busy.DatabasePath);
            await ExecuteAsync(connection, "BEGIN EXCLUSIVE;");
            try
            {
                var locked = new SqliteContextStorage(new SqliteStorageOptions { DatabasePath = busy.DatabasePath, ArtifactRoot = Path.Combine(busy.Directory, "artifacts"), BusyTimeoutMilliseconds = 20 });
                var error = await Assert.ThrowsAsync<StorageException>(() => locked.GetSchemaVersionAsync());
                Assert.Equal(StorageErrorCode.DatabaseBusy, error.Code);
            }
            finally
            {
                await ExecuteAsync(connection, "ROLLBACK;");
            }
        }

        await using (var blockedMigration = TemporaryDatabase.Create())
        {
            await using var connection = await OpenAsync(blockedMigration.DatabasePath);
            await ExecuteAsync(connection, "BEGIN EXCLUSIVE;");
            try
            {
                var error = await Assert.ThrowsAsync<StorageException>(() => blockedMigration.Storage.InitializeAsync());
                Assert.Equal(StorageErrorCode.DatabaseBusy, error.Code);
            }
            finally
            {
                await ExecuteAsync(connection, "ROLLBACK;");
            }

            await using var verify = await OpenAsync(blockedMigration.DatabasePath);
            Assert.Equal(0L, await ScalarLongAsync(verify, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'SchemaVersion';"));
        }

        var directory = Path.Combine(Path.GetTempPath(), "AIContextMCP.Storage.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var parentFile = Path.Combine(directory, "not-a-directory");
            await File.WriteAllTextAsync(parentFile, "x");
            var unavailable = new SqliteContextStorage(new SqliteStorageOptions { DatabasePath = Path.Combine(parentFile, "storage.db"), ArtifactRoot = directory });
            var error = await Assert.ThrowsAsync<StorageException>(() => unavailable.InitializeAsync());
            Assert.Equal(StorageErrorCode.DatabaseUnavailable, error.Code);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ProviderInitializedDatabasePassesDirectSqliteInspection()
    {
        await using var database = TemporaryDatabase.Create();
        await database.Storage.InitializeAsync();
        await database.Storage.CreateProjectAsync(Project());

        await using var connection = await OpenAsync(database.DatabasePath);
        Assert.Contains("Projects", await StringsAsync(connection, "SELECT name FROM sqlite_master WHERE type = 'table';"));
        Assert.Contains("CREATE TABLE Projects", await ScalarStringAsync(connection, "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'Projects';"), StringComparison.Ordinal);
        await ExecuteAsync(connection, "PRAGMA foreign_keys = ON;");
        Assert.Equal(1L, await ScalarLongAsync(connection, "PRAGMA foreign_keys;"));
        Assert.Equal("ok", await ScalarStringAsync(connection, "PRAGMA integrity_check;"));
        Assert.StartsWith("4|v4|", await ScalarStringAsync(connection, "SELECT Version || '|' || MigrationId || '|' || AppliedUtc FROM SchemaVersion;"), StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsUnsafeOperatorDatabasePaths()
    {
        var artifactRoot = Path.GetTempPath();
        var unc = Assert.Throws<StorageException>(() => new SqliteContextStorage(new SqliteStorageOptions { DatabasePath = "\\\\server\\share\\storage.db", ArtifactRoot = artifactRoot }));
        Assert.Equal(StorageErrorCode.Validation, unc.Code);
        var alternateStream = Assert.Throws<StorageException>(() => new SqliteContextStorage(new SqliteStorageOptions { DatabasePath = "D:\\Astra\\mcp\\AIContextMCP\\data\\storage.db:stream", ArtifactRoot = artifactRoot }));
        Assert.Equal(StorageErrorCode.Validation, alternateStream.Code);
    }

    [Fact]
    public async Task RejectsReparsePointDatabaseAncestorsAndSidecars()
    {
        await using var database = TemporaryDatabase.Create();
        var target = Path.Combine(database.Directory, "target");
        Directory.CreateDirectory(target);
        var ancestor = Path.Combine(database.Directory, "junction");
        var sidecar = database.DatabasePath + "-wal";
        try
        {
            await CreateJunctionAsync(ancestor, target);
            var ancestorStorage = new SqliteContextStorage(new SqliteStorageOptions { DatabasePath = Path.Combine(ancestor, "storage.db"), ArtifactRoot = Path.Combine(database.Directory, "artifacts") });
            Assert.Equal(StorageErrorCode.DatabaseUnavailable, (await Assert.ThrowsAsync<StorageException>(() => ancestorStorage.InitializeAsync())).Code);
            Directory.Delete(ancestor);

            await CreateJunctionAsync(sidecar, target);
            Assert.Equal(StorageErrorCode.DatabaseUnavailable, (await Assert.ThrowsAsync<StorageException>(() => database.Storage.InitializeAsync())).Code);
        }
        finally
        {
            if (Directory.Exists(ancestor))
            {
                Directory.Delete(ancestor);
            }

            if (Directory.Exists(sidecar))
            {
                Directory.Delete(sidecar);
            }
        }
    }

    [Theory]
    [InlineData("access_token=synthetic-only")]
    [InlineData("Bearer abcdefghijklmnop")]
    [InlineData("-----BEGIN PRIVATE KEY-----")]
    public async Task RejectsObviousSyntheticSecretsAcrossPersistedFields(string value)
    {
        await using var database = TemporaryDatabase.Create();
        await database.Storage.InitializeAsync();
        var project = await database.Storage.CreateProjectAsync(Project());
        var error = await Assert.ThrowsAsync<StorageException>(() => database.Storage.CreateContextEntryAsync(new ContextEntryDraft(Guid.NewGuid(), project.Id, null, "security", "title", "summary", value, null, null, null, ContextStatus.Active, null)));
        Assert.Equal(StorageErrorCode.SecretRejected, error.Code);
        Assert.DoesNotContain(value, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsMalformedIdentifiersFieldsEnumsCommitsTimestampsAndOversizedQueries()
    {
        await using var database = TemporaryDatabase.Create();
        await database.Storage.InitializeAsync();
        var project = await database.Storage.CreateProjectAsync(Project());
        var repository = await database.Storage.CreateRepositoryAsync(Repository(project.Id));

        Assert.Equal(StorageErrorCode.Validation, (await Assert.ThrowsAsync<StorageException>(() => database.Storage.CreateProjectAsync(Project(Guid.Empty)))).Code);
        Assert.Equal(StorageErrorCode.Validation, (await Assert.ThrowsAsync<StorageException>(() => database.Storage.CreateContextEntryAsync(new ContextEntryDraft(Guid.NewGuid(), project.Id, repository.Id, "category", "", "summary", null, null, null, null, ContextStatus.Active, null)))).Code);
        Assert.Equal(StorageErrorCode.Validation, (await Assert.ThrowsAsync<StorageException>(() => database.Storage.CreateContextEntryAsync(new ContextEntryDraft(Guid.NewGuid(), project.Id, repository.Id, "category", "title", "summary", null, null, null, null, (ContextStatus)99, null)))).Code);
        Assert.Equal(StorageErrorCode.Validation, (await Assert.ThrowsAsync<StorageException>(() => database.Storage.CreateFindingAsync(new FindingDraft(Guid.NewGuid(), project.Id, null, FindingCategory.Testing, "title", "description", (FindingSeverity)99, FindingStatus.Open, null, null)))).Code);
        Assert.Equal(StorageErrorCode.Validation, (await Assert.ThrowsAsync<StorageException>(() => database.Storage.CreateRepositoryAsync(Repository(project.Id) with { CanonicalPath = "D:\\Projects\\bad-commit", LastKnownCommitSha = "not-a-sha" }))).Code);
        Assert.Equal(StorageErrorCode.Validation, (await Assert.ThrowsAsync<StorageException>(() => database.Storage.CreatePhaseAsync(new PhaseDraft(Guid.NewGuid(), project.Id, repository.Id, "bad-time", "objective", PhaseStatus.Planned, null, null, default(DateTimeOffset), null)))).Code);
        Assert.Equal(StorageErrorCode.Validation, (await Assert.ThrowsAsync<StorageException>(() => database.Storage.CreateTestRunAsync(new TestRunDraft(Guid.NewGuid(), project.Id, repository.Id, "dotnet test", null, null, long.MaxValue, long.MaxValue, 3, 1, TestRunStatus.Failed, null, null, null, null)))).Code);
        var maximumCounts = await database.Storage.CreateTestRunAsync(new TestRunDraft(
            Guid.NewGuid(), project.Id, repository.Id, "dotnet test", null, null,
            1_000_000_000, 1_000_000_000, 1_000_000_000, 3_000_000_000L,
            TestRunStatus.Failed, null, null, null, null));
        Assert.Equal(3_000_000_000L, maximumCounts.Total);
        Assert.Equal(StorageErrorCode.LimitExceeded, (await Assert.ThrowsAsync<StorageException>(() => database.Storage.ListDecisionsAsync(project.Id, maxResults: StorageLimits.CollectionCount + 1))).Code);
        var finding = await database.Storage.CreateFindingAsync(new FindingDraft(Guid.NewGuid(), project.Id, null, FindingCategory.Testing, "title", "description", FindingSeverity.Low, FindingStatus.Open, null, null));
        Assert.Equal(StorageErrorCode.LimitExceeded, (await Assert.ThrowsAsync<StorageException>(() => database.Storage.ListFindingHistoryAsync(finding.Id, StorageLimits.CollectionCount + 1))).Code);
    }

    [Fact]
    public async Task PhaseContextMutationUsesExpectedVersionAndPersistsScopedObservationMetadata()
    {
        await using var database = TemporaryDatabase.Create();
        await database.Storage.InitializeAsync();
        var project = await database.Storage.CreateProjectAsync(Project());
        var repository = await database.Storage.CreateRepositoryAsync(Repository(project.Id));
        var phaseId = Guid.NewGuid();
        var entryId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var request = new MutationRequest("phase-scope", "context.record", $"{now.ToUnixTimeSeconds()}:{Guid.NewGuid():D}", Hash);

        await database.Storage.ExecuteMutationAsync(request, async (storage, token) =>
        {
            var phase = await storage.UpsertPhaseAsync(new PhaseDraft(
                phaseId, project.Id, repository.Id, "mcp-runtime", "Add the local adapter", PhaseStatus.Active, "main", "0123456", now, null), null, token);
            Assert.Equal(1, phase.Version);
            var entry = await storage.CreateContextEntryAsync(new ContextEntryDraft(
                entryId, project.Id, repository.Id, "phase", "MCP runtime", "Local adapter work", null, null, "main", "0123456", ContextStatus.Active, Hash, 2, "Add the local adapter", phaseId), token);
            await storage.UpsertPhaseAsync(new PhaseDraft(
                phaseId, project.Id, repository.Id, "mcp-runtime", "Add the local adapter", PhaseStatus.Active, "main", "0123456", now, null, 1, entryId), 1, token);
            await storage.CreateMcpObservationAsync(new McpObservationDraft(
                McpRecordKind.ContextEntry, entry.Id, 1, project.Id, repository.Id, "repository", new McpSourceReference(project.Id, null, "repository-document", null, "docs/MCP_CONTRACT.md", null, Hash), now,
                repository.CanonicalPath, "main", "0123456", Hash, now, "Clean", now), token);
            await storage.CreateMcpReferenceAsync(new McpReferenceDraft(
                McpRecordKind.ContextEntry, entry.Id, 1, McpReferenceRole.RelevantFile, 0,
                new McpSourceReference(project.Id, repository.Id, "repository-document", null, "docs/MCP_CONTRACT.md", null, Hash)), token);
            return $"{{\"entryId\":\"{entry.Id:D}\"}}";
        });

        var phase = await database.Storage.GetPhaseAsync(phaseId);
        var entry = await database.Storage.GetContextEntryAsync(entryId);
        var observation = await database.Storage.GetMcpObservationAsync(McpRecordKind.ContextEntry, entryId);
        Assert.NotNull(phase);
        Assert.NotNull(entry);
        Assert.NotNull(observation);
        Assert.Equal(entryId, phase.ContextEntryId);
        Assert.Equal(phaseId, entry.PhaseId);
        Assert.Equal(now.ToUnixTimeSeconds(), observation.SourceObservedUtc.ToUnixTimeSeconds());
        Assert.NotNull(observation.SourceReference);
        Assert.Null(observation.SourceReference.RepositoryId);
        Assert.Single(await database.Storage.ListMcpReferencesAsync(McpRecordKind.ContextEntry, entryId));
        await database.Storage.CreateMcpReferenceAsync(new McpReferenceDraft(
            McpRecordKind.ContextEntry, entryId, 1, McpReferenceRole.Artifact, 0,
            new McpSourceReference(project.Id, null, "artifact", null, "reports/result.json", null, Hash)));
        Assert.Equal(2, (await database.Storage.ListMcpReferencesAsync(McpRecordKind.ContextEntry, entryId)).Count);

        var updated = await database.Storage.UpsertPhaseAsync(new PhaseDraft(
            phaseId, project.Id, repository.Id, "mcp-runtime", "Finish adapter", PhaseStatus.Completed, "main", "0123456", now, now, 1, entryId), 2);
        Assert.Equal(3, updated.Version);
        var stale = await Assert.ThrowsAsync<StorageException>(() => database.Storage.UpsertPhaseAsync(new PhaseDraft(
            phaseId, project.Id, repository.Id, "mcp-runtime", "Stale", PhaseStatus.Completed, "main", "0123456", now, now, 1, entryId), 2));
        Assert.Equal(StorageErrorCode.Conflict, stale.Code);

        var other = await database.Storage.CreateProjectAsync(Project());
        var crossScope = await Assert.ThrowsAsync<StorageException>(() => database.Storage.CreateMcpReferenceAsync(new McpReferenceDraft(
            McpRecordKind.ContextEntry, entryId, 1, McpReferenceRole.Artifact, 0,
            new McpSourceReference(other.Id, null, "artifact", null, "reports/result.json", null, Hash))));
        Assert.Equal(StorageErrorCode.CrossProjectReference, crossScope.Code);

        var missing = await Assert.ThrowsAsync<StorageException>(() => database.Storage.CreateMcpObservationAsync(new McpObservationDraft(
            McpRecordKind.ContextEntry, Guid.NewGuid(), 1, project.Id, repository.Id, "repository", null, now,
            repository.CanonicalPath, "main", "0123456", Hash, now, "Clean", now)));
        Assert.Equal(StorageErrorCode.NotFound, missing.Code);
        var wrongRevision = await Assert.ThrowsAsync<StorageException>(() => database.Storage.CreateMcpObservationAsync(new McpObservationDraft(
            McpRecordKind.ContextEntry, entryId, 2, project.Id, repository.Id, "repository", null, now,
            repository.CanonicalPath, "main", "0123456", Hash, now, "Clean", now)));
        Assert.Equal(StorageErrorCode.NotFound, wrongRevision.Code);
        var wrongOwner = await Assert.ThrowsAsync<StorageException>(() => database.Storage.CreateMcpObservationAsync(new McpObservationDraft(
            McpRecordKind.ContextEntry, entryId, 1, other.Id, null, "repository", null, now,
            repository.CanonicalPath, "main", "0123456", Hash, now, "Clean", now)));
        Assert.Equal(StorageErrorCode.CrossProjectReference, wrongOwner.Code);
    }

    [Fact]
    public async Task FailedPhaseContextMutationLeavesNeitherRecord()
    {
        await using var database = TemporaryDatabase.Create();
        await database.Storage.InitializeAsync();
        var project = await database.Storage.CreateProjectAsync(Project());
        var repository = await database.Storage.CreateRepositoryAsync(Repository(project.Id));
        var phaseId = Guid.NewGuid();
        var entryId = Guid.NewGuid();
        var request = new MutationRequest("phase-rollback", "context.record", $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}:{Guid.NewGuid():D}", Hash);

        await Assert.ThrowsAsync<InvalidOperationException>(() => database.Storage.ExecuteMutationAsync(request, async (storage, token) =>
        {
            await storage.UpsertPhaseAsync(new PhaseDraft(phaseId, project.Id, repository.Id, "rollback", "Rollback", PhaseStatus.Active, "main", "0123456", null, null), null, token);
            await storage.CreateContextEntryAsync(new ContextEntryDraft(entryId, project.Id, repository.Id, "phase", "Rollback", "Should not persist", null, null, "main", "0123456", ContextStatus.Active, Hash, 2, "Rollback", phaseId), token);
            throw new InvalidOperationException("rollback");
        }));

        Assert.Null(await database.Storage.GetPhaseAsync(phaseId));
        Assert.Null(await database.Storage.GetContextEntryAsync(entryId));
    }

    [Fact]
    public async Task McpKeysetPagesUseObservedMetadataAndRetainLegacyRows()
    {
        await using var database = TemporaryDatabase.Create();
        await database.Storage.InitializeAsync();
        var project = await database.Storage.CreateProjectAsync(Project());
        var repository = await database.Storage.CreateRepositoryAsync(Repository(project.Id));
        var now = DateTimeOffset.UtcNow;
        var early = await database.Storage.CreateContextEntryAsync(new ContextEntryDraft(Guid.NewGuid(), project.Id, repository.Id, "context", "early", "early", null, null, "main", "0123456", ContextStatus.Active, Hash));
        var legacy = await database.Storage.CreateContextEntryAsync(new ContextEntryDraft(Guid.NewGuid(), project.Id, repository.Id, "context", "legacy", "legacy", null, null, "main", "0123456", ContextStatus.Active, Hash[..63] + "0"));
        var late = await database.Storage.CreateContextEntryAsync(new ContextEntryDraft(Guid.NewGuid(), project.Id, repository.Id, "context", "late", "late", null, null, "main", "0123456", ContextStatus.Active, "abcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcd"));
        await database.Storage.CreateMcpObservationAsync(new McpObservationDraft(McpRecordKind.ContextEntry, early.Id, 1, project.Id, repository.Id, "repository", null, now, repository.CanonicalPath, "main", "0123456", Hash, now.AddMinutes(-2), "Clean", now.AddMinutes(-2)));
        await database.Storage.CreateMcpObservationAsync(new McpObservationDraft(McpRecordKind.ContextEntry, late.Id, 1, project.Id, repository.Id, "repository", null, now, repository.CanonicalPath, "main", "0123456", Hash, now.AddMinutes(2), "Clean", now.AddMinutes(2)));

        var first = await database.Storage.ListMcpContextEntriesAsync(new McpContextPageQuery(project.Id, repository.Id, "context", MaxResults: 2));
        Assert.Equal([late.Id, legacy.Id], first.Select(item => item.Entry.Id));
        var second = await database.Storage.ListMcpContextEntriesAsync(new McpContextPageQuery(project.Id, repository.Id, "context", BeforeObservedUtc: first[^1].ObservedUtc, AfterRecordId: first[^1].Entry.Id, MaxResults: 2));
        Assert.Equal([early.Id], second.Select(item => item.Entry.Id));

        var earlyDecision = await database.Storage.CreateDecisionAsync(Decision(project.Id, "early"));
        var legacyDecision = await database.Storage.CreateDecisionAsync(Decision(project.Id, "legacy"));
        var lateDecision = await database.Storage.CreateDecisionAsync(Decision(project.Id, "late"));
        await database.Storage.CreateMcpObservationAsync(new McpObservationDraft(McpRecordKind.Decision, earlyDecision.Id, 1, project.Id, null, "repository", null, now, repository.CanonicalPath, "main", "0123456", Hash, now.AddMinutes(-2), "Clean", now.AddMinutes(-2)));
        await database.Storage.CreateMcpObservationAsync(new McpObservationDraft(McpRecordKind.Decision, lateDecision.Id, 1, project.Id, null, "repository", null, now, repository.CanonicalPath, "main", "0123456", Hash, now.AddMinutes(2), "Clean", now.AddMinutes(2)));

        var decisionFirst = await database.Storage.ListMcpDecisionsAsync(new McpDecisionPageQuery(project.Id, MaxResults: 2));
        Assert.Equal([lateDecision.Id, legacyDecision.Id], decisionFirst.Select(item => item.Decision.Id));
        var decisionSecond = await database.Storage.ListMcpDecisionsAsync(new McpDecisionPageQuery(project.Id, BeforeObservedUtc: decisionFirst[^1].ObservedUtc, AfterRecordId: decisionFirst[^1].Decision.Id, MaxResults: 2));
        Assert.Equal([earlyDecision.Id], decisionSecond.Select(item => item.Decision.Id));
    }

    [Fact]
    public async Task McpKeysetLookaheadAllowsOneInternalRowBeyondThePublicPage()
    {
        await using var database = TemporaryDatabase.Create();
        await database.Storage.InitializeAsync();
        var project = await database.Storage.CreateProjectAsync(Project());
        var repository = await database.Storage.CreateRepositoryAsync(Repository(project.Id));
        var now = DateTimeOffset.UtcNow;
        for (var index = 0; index <= StorageLimits.CollectionCount; index++)
        {
            var entry = await database.Storage.CreateContextEntryAsync(new ContextEntryDraft(
                Guid.NewGuid(), project.Id, repository.Id, "lookahead", "entry", "entry", null, null, "main", "0123456", ContextStatus.Active, Hash));
            await database.Storage.CreateMcpObservationAsync(new McpObservationDraft(
                McpRecordKind.ContextEntry, entry.Id, 1, project.Id, repository.Id, "repository", null, now,
                repository.CanonicalPath, "main", "0123456", Hash, now, "Clean", now));
        }

        var lookahead = await database.Storage.ListMcpContextEntriesAsync(new McpContextPageQuery(
            project.Id, repository.Id, "lookahead", MaxResults: StorageLimits.CollectionCount + 1));
        Assert.Equal(StorageLimits.CollectionCount + 1, lookahead.Count);
        var first = await database.Storage.ListMcpContextEntriesAsync(new McpContextPageQuery(
            project.Id, repository.Id, "lookahead", MaxResults: StorageLimits.CollectionCount));
        Assert.Equal(StorageLimits.CollectionCount, first.Count);
        var afterCursor = await database.Storage.ListMcpContextEntriesAsync(new McpContextPageQuery(
            project.Id, repository.Id, "lookahead", BeforeObservedUtc: first[^1].ObservedUtc, AfterRecordId: first[^1].Entry.Id, MaxResults: 1));
        Assert.Single(afterCursor);
        Assert.Equal(lookahead[^1].Entry.Id, afterCursor[0].Entry.Id);
    }

    private static ProjectDraft Project(Guid? id = null, string? stableProjectId = null) => new(id ?? Guid.NewGuid(), stableProjectId ?? $"project-{Guid.NewGuid():N}", "Example project");

    private static RepositoryDraft Repository(Guid projectId, string canonicalPath = "D:\\Projects\\example") => new(Guid.NewGuid(), projectId, canonicalPath, "https://example.invalid/org/repo.git", "org", "repo", "main", "main", "0123456");

    private static DecisionDraft Decision(Guid projectId, string text, Guid? id = null, Guid? supersedesDecisionId = null, string? resolutionEvidence = null) => new(id ?? Guid.NewGuid(), projectId, null, "architecture", "storage", "Storage decision", text, "Keep durable engineering state compact.", "docs/ARCHITECTURE.md", resolutionEvidence, "0123456", DecisionStatus.Accepted, supersedesDecisionId);

    private static HandoffDraft Handoff(Guid projectId) => new(Guid.NewGuid(), projectId, null, "main", "0123456", "Persist Chunk 3 metadata", "Created storage schema", "Validate storage", "None", "SQLite remains local", "Passed", "None", ["src/AIContextMCP.Storage.Sqlite/SqliteContextStorage.cs"], ["reports/test-summary.json"], "Run independent review");

    private static async Task<SqliteConnection> OpenAsync(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarLongAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<string> ScalarStringAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync())!;
    }

    private static async Task<List<string>> StringsAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static async Task CreateJunctionAsync(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "/d", "/c", "mklink", "/J", link, target }
        });
        Assert.NotNull(process);
        var error = await process!.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, error);
    }
}
