using AIContextMCP.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AIContextMCP.Storage.Sqlite.Tests;

public sealed class Chunk6AbuseTests
{
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string Sql = "x'); DROP TABLE Projects; -- %_ [ ]";
    private const string Secret = "api_key=synthetic-only";

    [Fact]
    public async Task SqlMetacharactersRemainLiteralAcrossEntitiesAndSearch()
    {
        await using var database = TemporaryDatabase.Create();
        await database.Storage.InitializeAsync();
        var project = await database.Storage.CreateProjectAsync(new ProjectDraft(Guid.NewGuid(), Sql, Sql));
        var otherProject = await database.Storage.CreateProjectAsync(new ProjectDraft(Guid.NewGuid(), "other", "other"));
        var repository = await database.Storage.CreateRepositoryAsync(new RepositoryDraft(Guid.NewGuid(), project.Id, "D:\\Projects\\literal", Sql, Sql, Sql, "main", "main", "0123456"));
        var context = await database.Storage.CreateContextEntryAsync(new ContextEntryDraft(Guid.NewGuid(), project.Id, repository.Id, Sql, Sql, Sql, Sql, Sql, "main", "0123456", ContextStatus.Active, Hash));
        await database.Storage.CreateContextEntryAsync(new ContextEntryDraft(Guid.NewGuid(), project.Id, null, "benign", "ordinary text", "ordinary summary", null, null, null, null, ContextStatus.Active, null));
        await database.Storage.CreateContextEntryAsync(new ContextEntryDraft(Guid.NewGuid(), otherProject.Id, null, "other", Sql, "other summary", null, null, null, null, ContextStatus.Active, null));
        var decision = await database.Storage.CreateDecisionAsync(new DecisionDraft(Guid.NewGuid(), project.Id, repository.Id, Sql, Sql, Sql, Sql, Sql, Sql, Sql, "0123456", DecisionStatus.Accepted));
        var finding = await database.Storage.CreateFindingAsync(new FindingDraft(Guid.NewGuid(), project.Id, repository.Id, FindingCategory.Security, Sql, Sql, FindingSeverity.High, FindingStatus.Open, Sql, Sql, "main", "0123456", Sql));
        var artifact = await database.Storage.CreateArtifactAsync(new ArtifactDraft(Guid.NewGuid(), project.Id, repository.Id, Sql, Sql, "reports/%_literal.json", Hash, 1, "main", "0123456"));
        var handoff = await database.Storage.CreateHandoffAsync(new HandoffDraft(Guid.NewGuid(), project.Id, repository.Id, "main", "0123456", Sql, Sql, Sql, Sql, Sql, Sql, Sql, [Sql], ["reports/%_literal.json"], Sql));

        Assert.Equal(Sql, (await database.Storage.GetProjectAsync(project.Id))!.Name);
        Assert.Equal(Sql, (await database.Storage.GetRepositoryAsync(repository.Id))!.RepositoryName);
        Assert.Equal(Sql, (await database.Storage.GetContextEntryAsync(context.Id))!.Summary);
        Assert.Equal(Sql, (await database.Storage.GetDecisionAsync(decision.Id))!.Decision);
        Assert.Equal(Sql, (await database.Storage.GetFindingAsync(finding.Id))!.Description);
        Assert.Equal(Sql, (await database.Storage.GetArtifactAsync(artifact.Id))!.Title);
        Assert.Equal(Sql, (await database.Storage.GetHandoffAsync(handoff.Id))!.Objective);
        var wildcardMatches = await database.Storage.ListContextEntriesAsync(new ContextEntryQuery(project.Id, Text: "%_"));
        Assert.Single(wildcardMatches);
        Assert.Equal(context.Id, wildcardMatches[0].Id);
        Assert.NotNull(await database.Storage.GetProjectAsync(project.Id));
    }

    [Fact]
    public async Task SyntheticSecretsAreRejectedBeforeAnyEntityIsPersisted()
    {
        await using var database = TemporaryDatabase.Create();
        await database.Storage.InitializeAsync();
        var project = await database.Storage.CreateProjectAsync(new ProjectDraft(Guid.NewGuid(), "safe", "safe"));
        var projectId = Guid.NewGuid();
        var repositoryId = Guid.NewGuid();
        var contextId = Guid.NewGuid();
        var decisionId = Guid.NewGuid();
        var phaseId = Guid.NewGuid();
        var testRunId = Guid.NewGuid();
        var findingId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var handoffId = Guid.NewGuid();
        var cases = new (Func<Task> Create, Func<Task<bool>> Exists)[]
        {
            (() => database.Storage.CreateProjectAsync(new ProjectDraft(projectId, Secret, "safe")), async () => await database.Storage.GetProjectAsync(projectId) is not null),
            (() => database.Storage.CreateRepositoryAsync(new RepositoryDraft(repositoryId, project.Id, "D:\\Projects\\secret", null, null, Secret, "main", null, null)), async () => await database.Storage.GetRepositoryAsync(repositoryId) is not null),
            (() => database.Storage.CreateContextEntryAsync(new ContextEntryDraft(contextId, project.Id, null, "safe", "safe", Secret, null, null, null, null, ContextStatus.Active, null)), async () => await database.Storage.GetContextEntryAsync(contextId) is not null),
            (() => database.Storage.CreateDecisionAsync(new DecisionDraft(decisionId, project.Id, null, "safe", "safe", "safe", Secret, "safe", null, null, null)), async () => await database.Storage.GetDecisionAsync(decisionId) is not null),
            (() => database.Storage.CreatePhaseAsync(new PhaseDraft(phaseId, project.Id, null, "secret", Secret, PhaseStatus.Planned, null, null, null, null)), async () => await database.Storage.GetPhaseAsync(phaseId) is not null),
            (() => database.Storage.CreateTestRunAsync(new TestRunDraft(testRunId, project.Id, null, Secret, null, null, 0, 0, 0, 0, TestRunStatus.Unknown, null, null, null, null)), async () => await database.Storage.GetTestRunAsync(testRunId) is not null),
            (() => database.Storage.CreateFindingAsync(new FindingDraft(findingId, project.Id, null, FindingCategory.Security, Secret, "safe", FindingSeverity.High, FindingStatus.Open, null, null)), async () => await database.Storage.GetFindingAsync(findingId) is not null),
            (() => database.Storage.CreateArtifactAsync(new ArtifactDraft(artifactId, project.Id, null, "safe", Secret, "reports/safe.json", Hash, 0, null, null)), async () => await database.Storage.GetArtifactAsync(artifactId) is not null),
            (() => database.Storage.CreateHandoffAsync(new HandoffDraft(handoffId, project.Id, null, null, null, Secret, "safe", "safe", "safe", "safe", "safe", "safe", [], [], "safe")), async () => await database.Storage.GetHandoffAsync(handoffId) is not null)
        };

        foreach (var item in cases)
        {
            var error = await Assert.ThrowsAsync<StorageException>(item.Create);
            Assert.Equal(StorageErrorCode.SecretRejected, error.Code);
            Assert.DoesNotContain(Secret, error.Message, StringComparison.Ordinal);
            Assert.False(await item.Exists());
        }

        Assert.NotNull(await database.Storage.GetProjectAsync(project.Id));
    }

    [Fact]
    public async Task StorageLimitsAcceptExactValuesAndRejectOnlyOverLimit()
    {
        await using var database = TemporaryDatabase.Create();
        await database.Storage.InitializeAsync();
        var project = await database.Storage.CreateProjectAsync(new ProjectDraft(Guid.NewGuid(), "limits", "limits"));

        var context = await database.Storage.CreateContextEntryAsync(new ContextEntryDraft(Guid.NewGuid(), project.Id, null, "category", "title", new string('s', StorageLimits.Summary), new string('c', StorageLimits.Content), null, null, null, ContextStatus.Active, null));
        Assert.Equal(StorageLimits.Summary, context.Summary.Length);
        Assert.Equal(StorageLimits.Content, context.Content!.Length);
        await AssertLimitAsync(() => database.Storage.CreateContextEntryAsync(new ContextEntryDraft(Guid.NewGuid(), project.Id, null, "category", "title", new string('s', StorageLimits.Summary + 1), null, null, null, null, ContextStatus.Active, null)));
        await AssertLimitAsync(() => database.Storage.CreateContextEntryAsync(new ContextEntryDraft(Guid.NewGuid(), project.Id, null, "category", "title", "summary", new string('c', StorageLimits.Content + 1), null, null, null, ContextStatus.Active, null)));
        var exactQueryText = new string('q', StorageLimits.Title);
        await database.Storage.CreateContextEntryAsync(new ContextEntryDraft(Guid.NewGuid(), project.Id, null, "category", exactQueryText, "summary", null, null, null, null, ContextStatus.Active, null));
        Assert.Single(await database.Storage.ListContextEntriesAsync(new ContextEntryQuery(project.Id, Text: exactQueryText)));

        var decision = await database.Storage.CreateDecisionAsync(new DecisionDraft(Guid.NewGuid(), project.Id, null, "category", "component", "title", new string('d', StorageLimits.Summary), "rationale", null, null, null));
        Assert.Equal(StorageLimits.Summary, decision.Decision.Length);
        await AssertLimitAsync(() => database.Storage.CreateDecisionAsync(new DecisionDraft(Guid.NewGuid(), project.Id, null, "category", "component", "title", new string('d', StorageLimits.Summary + 1), "rationale", null, null, null)));

        var exactHandoff = new HandoffDraft(Guid.NewGuid(), project.Id, null, null, null, new string('h', StorageLimits.HandoffField), "safe", "safe", "safe", "safe", "safe", "safe", [], [], "safe");
        Assert.Equal(StorageLimits.HandoffField, (await database.Storage.CreateHandoffAsync(exactHandoff)).Objective.Length);
        await AssertLimitAsync(() => database.Storage.CreateHandoffAsync(exactHandoff with { Id = Guid.NewGuid(), Objective = new string('h', StorageLimits.HandoffField + 1) }));
        var aggregateHandoff = exactHandoff with
        {
            Id = Guid.NewGuid(),
            Objective = new string('a', 8_192),
            CompletedWork = new string('a', 8_191),
            ActiveWork = new string('a', 8_191),
            ActiveBlockers = new string('a', 8_190),
            ImportantDecisions = "a",
            LatestTestStatus = "a",
            UnresolvedFindings = "a",
            RecommendedNextAction = "a"
        };
        Assert.Equal(StorageLimits.HandoffAggregate, (await database.Storage.CreateHandoffAsync(aggregateHandoff)).Objective.Length + aggregateHandoff.CompletedWork.Length + aggregateHandoff.ActiveWork.Length + aggregateHandoff.ActiveBlockers.Length + 4);
        await AssertLimitAsync(() => database.Storage.CreateHandoffAsync(aggregateHandoff with { Id = Guid.NewGuid(), ImportantDecisions = "aa" }));

        await AssertLimitAsync(() => database.Storage.ListContextEntriesAsync(new ContextEntryQuery(project.Id, Text: new string('q', StorageLimits.Title + 1))));
        var artifact = await database.Storage.CreateArtifactAsync(new ArtifactDraft(Guid.NewGuid(), project.Id, null, new string('t', StorageLimits.ShortText), new string('t', StorageLimits.Title), "reports/exact.json", Hash, 0, null, null));
        Assert.Equal(StorageLimits.Title, artifact.Title.Length);
        await AssertLimitAsync(() => database.Storage.CreateArtifactAsync(new ArtifactDraft(Guid.NewGuid(), project.Id, null, "type", new string('t', StorageLimits.Title + 1), "reports/over.json", Hash, 0, null, null)));

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database.DatabasePath, Pooling = false }.ConnectionString);
        await connection.OpenAsync();
        Assert.Equal(2L, await CountAsync(connection, "ContextEntries"));
        Assert.Equal(1L, await CountAsync(connection, "Decisions"));
        Assert.Equal(2L, await CountAsync(connection, "Handoffs"));
        Assert.Equal(1L, await CountAsync(connection, "Artifacts"));
    }

    private static async Task AssertLimitAsync(Func<Task> operation)
    {
        var error = await Assert.ThrowsAsync<StorageException>(operation);
        Assert.Equal(StorageErrorCode.LimitExceeded, error.Code);
    }

    private static async Task<long> CountAsync(SqliteConnection connection, string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
