using AIContextMCP.Storage.Sqlite;
using Xunit;

namespace AIContextMCP.Core.Tests;

public sealed class ApplicationCoreTests
{
    [Fact]
    public async Task Project_scoped_context_uses_server_hash_and_safe_operation_outcomes()
    {
        using var harness = await ApplicationHarness.CreateAsync();
        var request = new ContextRecordRequest(harness.Project.Id, null, "context", "title", "summary", "body", "notes/record.md");
        var record = await harness.Application.RecordContextAsync(request);
        Assert.Matches("^[0-9A-F]{64}$", record.ContentHash);

        var duplicate = await Assert.ThrowsAsync<ApplicationException>(() => harness.Application.RecordContextAsync(request));
        Assert.Equal(ApplicationErrorCode.DuplicateRecord, duplicate.Code);

        var claimedMismatch = await Assert.ThrowsAsync<ApplicationException>(() => harness.Application.RecordContextAsync(request with { ContentHash = new string('0', 64) }));
        Assert.Equal(ApplicationErrorCode.InvalidInput, claimedMismatch.Code);

        var oversized = await Assert.ThrowsAsync<ApplicationException>(() => harness.Application.RecordContextAsync(request with { Content = new string('x', StorageLimits.Content + 1) }));
        Assert.Equal(ApplicationErrorCode.ContentTooLarge, oversized.Code);

        var secret = await Assert.ThrowsAsync<ApplicationException>(() => harness.Application.RecordContextAsync(request with { Title = "secret", Content = "api_key=abcdefghijklmnopqrstuvwxyz" }));
        Assert.Equal(ApplicationErrorCode.SecretRejected, secret.Code);
        Assert.Contains(harness.Outcomes, outcome => outcome == ("context.record", "SecretRejected"));
        Assert.All(harness.Outcomes, outcome =>
        {
            Assert.DoesNotContain("api_key", outcome.Operation, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("api_key", outcome.Outcome, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task Project_level_phase_and_decisions_are_queryable_without_repository_scope()
    {
        using var harness = await ApplicationHarness.CreateAsync();
        var phase = await harness.Storage.CreatePhaseAsync(new PhaseDraft(Guid.NewGuid(), harness.Project.Id, null, "project-phase", "project objective", PhaseStatus.Active, null, null, DateTimeOffset.UtcNow, null));
        var accepted = await harness.Application.RecordDecisionAsync(new DecisionRecordRequest(harness.Project.Id, null, "architecture", "core", "accepted", "use records", "bounded", Status: DecisionStatus.Accepted));
        await harness.Application.RecordDecisionAsync(new DecisionRecordRequest(harness.Project.Id, null, "architecture", "core", "rejected", "use dump", "unbounded", Status: DecisionStatus.Rejected));

        var phases = await harness.Storage.ListPhasesAsync(new PhaseQuery(harness.Project.Id, null, PhaseStatus.Active, RepositoryIsNull: true));
        var decisions = await harness.Storage.ListDecisionsAsync(new DecisionQuery(harness.Project.Id, null, Status: DecisionStatus.Accepted, RepositoryIsNull: true));
        var listed = await harness.Application.ListDecisionsAsync(new DecisionListRequest(harness.Project.Id));

        Assert.Contains(phases, item => item.Id == phase.Id);
        Assert.Contains(decisions, item => item.Id == accepted.Id);
        Assert.Contains(listed.Decisions, item => item.Id == accepted.Id);
        Assert.True(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(listed).Length <= ApplicationLimits.SearchBytes);
    }

    [Fact]
    public async Task Project_scoped_handoff_keeps_safe_references_and_limits()
    {
        using var harness = await ApplicationHarness.CreateAsync();
        var handoff = await harness.Application.CreateHandoffAsync(new HandoffCreateRequest(
            harness.Project.Id, null, "objective", "completed", "active", "blockers", "decisions", "tests", "findings",
            ["src/Program.cs"], ["artifacts/result.txt"], "next"));

        Assert.Null(handoff.RepositoryId);
        Assert.Equal(["src/Program.cs"], handoff.RelevantFiles);
        Assert.Equal(["artifacts/result.txt"], handoff.RelevantArtifacts);
        var error = await Assert.ThrowsAsync<ApplicationException>(() => harness.Application.CreateHandoffAsync(new HandoffCreateRequest(
            harness.Project.Id, null, new string('\u00e9', StorageLimits.HandoffField), "completed", "active", "blockers", "decisions", "tests", "findings", [], [], "next")));
        Assert.Equal(ApplicationErrorCode.ContentTooLarge, error.Code);
        Assert.Equal("Handoff exceeds its serialized limit.", error.Message);
        Assert.Equal(ApplicationErrorCode.InvalidInput, Assert.Throws<ApplicationException>(() => new AIContextApplication(harness.Storage, new ApplicationCoreOptions { ApprovedRepositoryRoots = [@"D:\Projects"], BootstrapBytes = ApplicationLimits.MinimumBootstrapBytes - 1 })).Code);
    }

    [Fact]
    public async Task Project_scoped_lifecycle_enforces_filters_history_counts_and_revisions()
    {
        using var harness = await ApplicationHarness.CreateAsync();

        await harness.Application.RecordContextAsync(new ContextRecordRequest(harness.Project.Id, null, "scope", "first", "first summary", "first body"));
        await harness.Application.RecordContextAsync(new ContextRecordRequest(harness.Project.Id, null, "scope", "second", "second summary", "second body"));
        await harness.Application.RecordContextAsync(new ContextRecordRequest(harness.Project.Id, null, "other", "ignored", "other summary", "other body"));
        var firstSearch = await harness.Application.SearchContextAsync(new ContextSearchRequest(harness.Project.Id, Category: "scope", Status: ContextStatus.Active, MaxResults: 1));
        var repeatedSearch = await harness.Application.SearchContextAsync(new ContextSearchRequest(harness.Project.Id, Category: "scope", Status: ContextStatus.Active, MaxResults: 1));
        Assert.Single(firstSearch.Entries);
        Assert.True(firstSearch.HasMore);
        Assert.Equal("scope", firstSearch.Entries[0].Category);
        Assert.Equal(firstSearch.Entries[0].Id, repeatedSearch.Entries[0].Id);

        var original = await harness.Application.RecordDecisionAsync(new DecisionRecordRequest(harness.Project.Id, null, "architecture", "core", "original", "use A", "initial"));
        var replacement = await harness.Application.RecordDecisionAsync(new DecisionRecordRequest(harness.Project.Id, null, "architecture", "core", "replacement", "use B", "replaced", ResolutionEvidence: "replacement evidence", SupersedesDecisionId: original.Id));
        var originalAfter = await harness.Storage.GetDecisionAsync(original.Id);
        var decisionHistory = await harness.Application.ListDecisionsAsync(new DecisionListRequest(harness.Project.Id, IncludeSuperseded: true));
        Assert.NotNull(originalAfter);
        Assert.Equal(DecisionStatus.Superseded, originalAfter!.Status);
        Assert.Equal(replacement.Id, originalAfter.SupersededByDecisionId);
        Assert.Contains(decisionHistory.Decisions, decision => decision.Id == original.Id && decision.Status == DecisionStatus.Superseded);
        Assert.Contains(decisionHistory.Decisions, decision => decision.Id == replacement.Id);

        var completed = DateTimeOffset.UtcNow;
        var validTest = await harness.Application.RecordTestRunAsync(new TestRecordRequest(harness.Project.Id, null, "dotnet test", 2, 0, 0, 2, TestRunStatus.Passed, CompletedUtc: completed, RequireCurrentRepositoryState: false));
        Assert.Equal(TestRunStatus.Passed, validTest.Status);
        var invalidTest = await Assert.ThrowsAsync<ApplicationException>(() => harness.Application.RecordTestRunAsync(new TestRecordRequest(harness.Project.Id, null, "dotnet test", 1, 1, 0, 2, TestRunStatus.Passed, CompletedUtc: completed, RequireCurrentRepositoryState: false)));
        Assert.Equal(ApplicationErrorCode.InvalidInput, invalidTest.Code);

        var finding = await harness.Application.RecordFindingAsync(new FindingRecordRequest(harness.Project.Id, null, FindingCategory.Security, "boundary", "needs evidence", FindingSeverity.High, FindingStatus.Open));
        var missingEvidence = await Assert.ThrowsAsync<ApplicationException>(() => harness.Application.RecordFindingAsync(new FindingRecordRequest(harness.Project.Id, null, FindingCategory.Security, "boundary", "needs evidence", FindingSeverity.High, FindingStatus.Resolved, "fixed", FindingId: finding.Id, ExpectedRevision: finding.Revision)));
        Assert.Equal(ApplicationErrorCode.InvalidInput, missingEvidence.Code);
        var resolved = await harness.Application.RecordFindingAsync(new FindingRecordRequest(harness.Project.Id, null, FindingCategory.Security, "boundary", "resolved with evidence", FindingSeverity.High, FindingStatus.Resolved, "fixed", "test evidence", FindingId: finding.Id, ExpectedRevision: finding.Revision));
        Assert.Equal(finding.Revision + 1, resolved.Revision);
        var staleRevision = await Assert.ThrowsAsync<ApplicationException>(() => harness.Application.RecordFindingAsync(new FindingRecordRequest(harness.Project.Id, null, FindingCategory.Security, "boundary", "stale", FindingSeverity.High, FindingStatus.Resolved, "fixed", "test evidence", FindingId: finding.Id, ExpectedRevision: finding.Revision)));
        Assert.Equal(ApplicationErrorCode.Conflict, staleRevision.Code);
        var findingHistory = await harness.Storage.ListFindingHistoryAsync(finding.Id);
        Assert.Equal([resolved.Revision, finding.Revision], findingHistory.Select(item => item.Revision));
    }

    [Fact]
    public async Task Current_validation_requires_verified_clean_state_and_reports_a_stale_head()
    {
        using var harness = await ApplicationHarness.CreateAsync();
        var scope = await harness.Application.ResolveProjectAsync(new ProjectResolutionRequest(harness.RepositoryPath, Register: true));
        var recorded = await harness.Application.RecordTestRunAsync(new TestRecordRequest(
            scope.Project.Id, scope.Repository.Id, "dotnet test", 1, 0, 0, 1, TestRunStatus.Passed));
        var bootstrap = await harness.Application.BootstrapAsync(new ProjectBootstrapRequest(harness.RepositoryPath));
        Assert.Equal(recorded.Id, bootstrap.LatestValidation?.Id);
        Assert.Equal(TestRunStatus.Passed, bootstrap.LatestValidation?.Status);
        Assert.Equal(1, bootstrap.LatestValidation?.Passed);
        var newer = await harness.Application.RecordTestRunAsync(new TestRecordRequest(
            scope.Project.Id, scope.Repository.Id, "dotnet test newer", 2, 1, 3, 6, TestRunStatus.Failed, CompletedUtc: DateTimeOffset.UtcNow));
        await harness.Storage.CreateMcpObservationAsync(new McpObservationDraft(
            McpRecordKind.TestRun, newer.Id, 1, scope.Project.Id, scope.Repository.Id, "test", null,
            DateTimeOffset.UtcNow, scope.Git.CanonicalPath, scope.Git.Branch, scope.Git.HeadCommitSha,
            scope.Git.WorkingTreeFingerprint, DateTimeOffset.UtcNow, "Clean", DateTimeOffset.UtcNow));
        var latest = await harness.Application.BootstrapAsync(new ProjectBootstrapRequest(harness.RepositoryPath));
        Assert.Equal(newer.Id, latest.LatestValidation?.Id);
        Assert.Equal(TestRunStatus.Failed, latest.LatestValidation?.Status);
        Assert.Equal(2, latest.LatestValidation?.Passed);
        Assert.Equal(1, latest.LatestValidation?.Failed);
        Assert.Equal(3, latest.LatestValidation?.Skipped);
        Assert.Equal(scope.Git.Branch, latest.LatestValidation?.Branch);
        Assert.Equal(scope.Git.HeadCommitSha, latest.LatestValidation?.CommitSha);
        Assert.Equal(RecordFreshness.Current, latest.LatestValidation?.Freshness);

        File.AppendAllText(Path.Combine(harness.RepositoryPath, "README.md"), "dirty\n");
        var dirty = await Assert.ThrowsAsync<ApplicationException>(() => harness.Application.RecordTestRunAsync(new TestRecordRequest(
            scope.Project.Id, scope.Repository.Id, "dotnet test", 1, 0, 0, 1, TestRunStatus.Passed, CompletedUtc: DateTimeOffset.UtcNow)));

        harness.RunGit("add", "README.md");
        harness.RunGit("-c", "user.name=fixture", "-c", "user.email=fixture@example.invalid", "commit", "-m", "next");
        var afterCommit = await harness.Application.ResolveProjectAsync(new ProjectResolutionRequest(harness.RepositoryPath));
        var staleBootstrap = await harness.Application.BootstrapAsync(new ProjectBootstrapRequest(harness.RepositoryPath));

        Assert.Equal(TestRunStatus.Passed, recorded.Status);
        Assert.Equal(ApplicationErrorCode.StaleState, dirty.Code);
        Assert.Equal(WorkingTreeState.Clean, afterCommit.Git.WorkingTree);
        Assert.Contains(afterCommit.StaleWarnings, warning => warning.Contains("HEAD is stale", StringComparison.Ordinal));
        Assert.Equal(newer.Id, staleBootstrap.LatestValidation?.Id);
        Assert.Equal(RecordFreshness.Stale, staleBootstrap.LatestValidation?.Freshness);
    }

    private sealed class ApplicationHarness : IDisposable
    {
        private readonly ControlledGitFixture _fixture;

        private ApplicationHarness(ControlledGitFixture fixture, SqliteContextStorage storage, ProjectRecord project, AIContextApplication application, List<(string Operation, string Outcome)> outcomes)
        {
            _fixture = fixture;
            Storage = storage;
            Project = project;
            Application = application;
            Outcomes = outcomes;
        }

        public SqliteContextStorage Storage { get; }
        public ProjectRecord Project { get; }
        public AIContextApplication Application { get; }
        public List<(string Operation, string Outcome)> Outcomes { get; }
        public string RepositoryPath => _fixture.RepositoryPath;

        public string RunGit(params string[] args) => _fixture.Run(args);

        public static async Task<ApplicationHarness> CreateAsync()
        {
            var fixture = new ControlledGitFixture();
            try
            {
                var directory = Path.GetDirectoryName(fixture.RepositoryPath)!;
                var storage = new SqliteContextStorage(new SqliteStorageOptions
                {
                    DatabasePath = Path.Combine(directory, "application.db"),
                    ArtifactRoot = Path.Combine(directory, "artifacts")
                });
                await storage.InitializeAsync();
                var project = await storage.CreateProjectAsync(new ProjectDraft(Guid.NewGuid(), $"project-{Guid.NewGuid():N}", "application test"));
                var outcomes = new List<(string Operation, string Outcome)>();
                var application = new AIContextApplication(storage, new ApplicationCoreOptions { ApprovedRepositoryRoots = [@"D:\Projects"] }, (operation, outcome) => outcomes.Add((operation, outcome)));
                return new ApplicationHarness(fixture, storage, project, application, outcomes);
            }
            catch
            {
                fixture.Dispose();
                throw;
            }
        }

        public void Dispose() => _fixture.Dispose();
    }
}
