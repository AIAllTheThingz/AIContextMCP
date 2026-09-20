namespace AIContextMCP.Core;

public static class ApplicationLimits
{
    public const int SearchResults = 20;
    public const int BootstrapDecisions = 6;
    public const int BootstrapFindings = 6;
    public const int BootstrapBlockers = 6;
    public const int BootstrapReferences = 12;
    public const int BootstrapBytes = 16 * 1024;
    public const int MinimumBootstrapBytes = 8 * 1024;
    public const int SearchBytes = 32 * 1024;
}

public enum ApplicationErrorCode
{
    InvalidInput,
    PathRejected,
    RepositoryOutsideApprovedRoot,
    RepositoryNotFound,
    NotGitRepository,
    GitUnavailable,
    GitTimeout,
    GitCommandFailed,
    ProjectIdentityConflict,
    StaleState,
    StorageUnavailable,
    DuplicateRecord,
    InvalidTransition,
    SecretRejected,
    ContentTooLarge,
    NotFound,
    Conflict,
    UnbornRepository
}

public sealed class ApplicationException(ApplicationErrorCode code, string message, string? diagnostic = null, Exception? innerException = null) : Exception(message, innerException)
{
    public ApplicationErrorCode Code { get; } = code;
    public string? Diagnostic { get; } = diagnostic;
}

public enum WorkingTreeState { Clean, Dirty, Unknown }
public enum RecordFreshness { Current, Stale, Unknown }

public static class RepositorySnapshotFreshness
{
    public static RecordFreshness Evaluate(string? branch, string? commit, McpObservationRecord? observation, GitRepositoryState? git)
    {
        if (git is null || observation is null) return RecordFreshness.Unknown;
        if (!string.Equals(observation.CanonicalRoot, git.CanonicalPath, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(observation.Branch, git.Branch, StringComparison.Ordinal)
            || !string.Equals(observation.Head, git.HeadCommitSha, StringComparison.OrdinalIgnoreCase)
            || observation.WorkingTreeFingerprint is not null && !string.Equals(observation.WorkingTreeFingerprint, git.WorkingTreeFingerprint, StringComparison.OrdinalIgnoreCase)
            || (branch is not null && !string.Equals(branch, git.Branch, StringComparison.Ordinal))
            || (commit is not null && !string.Equals(commit, git.HeadCommitSha, StringComparison.OrdinalIgnoreCase)))
        {
            return RecordFreshness.Stale;
        }

        return observation.Completeness == "Clean" && observation.WorkingTreeFingerprint is not null && git.WorkingTree == WorkingTreeState.Clean && git.WorkingTreeFingerprint is not null
            ? RecordFreshness.Current
            : RecordFreshness.Unknown;
    }
}

public sealed class ApplicationCoreOptions
{
    public string[] ApprovedRepositoryRoots { get; init; } = ["D:\\Projects"];
    public int BootstrapBytes { get; init; } = ApplicationLimits.BootstrapBytes;
}

public sealed record GitRepositoryState(
    string CanonicalPath,
    string? Branch,
    string HeadCommitSha,
    string? Remote,
    string? Organization,
    string RepositoryName,
    WorkingTreeState WorkingTree,
    string? WorkingTreeFingerprint = null,
    string? WorkingTreeWarning = null);

public sealed record ProjectIdentity(Guid Id, string StableProjectId, string Name);
public sealed record RepositoryIdentity(Guid Id, string CanonicalPath, string? Remote, string RepositoryName);

public sealed record ProjectResolution(
    ProjectIdentity Project,
    RepositoryIdentity Repository,
    GitRepositoryState Git,
    IReadOnlyList<string> StaleWarnings);

public sealed record ProjectResolutionRequest(string RepositoryPath, bool Register = false, Guid? RebindRepositoryId = null);
public sealed record ProjectBootstrapRequest(string RepositoryPath, bool Register = false, Guid? RebindRepositoryId = null);

public sealed record ContextRecordRequest(
    Guid ProjectId,
    Guid? RepositoryId,
    string Category,
    string Title,
    string Summary,
    string? Content = null,
    string? AuthoritativeReference = null,
    ContextStatus Status = ContextStatus.Active,
    string? Branch = null,
    string? CommitSha = null,
    string? ContentHash = null,
    Guid? EntryId = null,
    int Tier = 2,
    string? Objective = null,
    Guid? PhaseId = null);

public sealed record PhaseRecordRequest(
    Guid Id,
    Guid ProjectId,
    Guid? RepositoryId,
    string PhaseKey,
    string Objective,
    PhaseStatus Status,
    string? Branch = null,
    string? CommitSha = null,
    DateTimeOffset? StartedUtc = null,
    DateTimeOffset? CompletedUtc = null,
    int? ExpectedVersion = null,
    Guid? ContextEntryId = null);

public sealed record ContextSearchRequest(
    Guid ProjectId,
    Guid? RepositoryId = null,
    string? Category = null,
    string? Branch = null,
    string? CommitSha = null,
    ContextStatus? Status = null,
    DateTimeOffset? SinceUtc = null,
    string? Text = null,
    int MaxResults = ApplicationLimits.SearchResults);

public sealed record ContextSearchItem(
    Guid Id,
    string Category,
    string Title,
    string Summary,
    string? AuthoritativeReference,
    string? Branch,
    string? CommitSha,
    ContextStatus Status,
    DateTimeOffset CreatedUtc,
    RecordFreshness Freshness);

public sealed record ContextSearchResult(IReadOnlyList<ContextSearchItem> Entries, bool HasMore, IReadOnlyList<string> Warnings);

public sealed record DecisionRecordRequest(
    Guid ProjectId,
    Guid? RepositoryId,
    string Category,
    string Component,
    string Title,
    string Decision,
    string Rationale,
    string? AuthoritativeReference = null,
    string? ResolutionEvidence = null,
    DecisionStatus Status = DecisionStatus.Accepted,
    Guid? SupersedesDecisionId = null,
    string? Branch = null,
    string? CommitSha = null);

public sealed record DecisionListRequest(
    Guid ProjectId,
    Guid? RepositoryId = null,
    string? Category = null,
    string? Component = null,
    string? Branch = null,
    string? CommitSha = null,
    DecisionStatus? Status = null,
    DateTimeOffset? SinceUtc = null,
    bool IncludeSuperseded = false,
    int MaxResults = ApplicationLimits.SearchResults);

public sealed record DecisionListItem(
    Guid Id,
    string Category,
    string Component,
    string Title,
    string Decision,
    string? AuthoritativeReference,
    DecisionStatus Status,
    Guid? SupersedesDecisionId,
    Guid? SupersededByDecisionId,
    string? Branch,
    string? CommitSha,
    RecordFreshness Freshness,
    DateTimeOffset UpdatedUtc);

public sealed record DecisionListResult(IReadOnlyList<DecisionListItem> Decisions, bool HasMore, IReadOnlyList<string> Warnings);

public sealed record TestRecordRequest(
    Guid ProjectId,
    Guid? RepositoryId,
    string Command,
    long Passed,
    long Failed,
    long Skipped,
    long Total,
    TestRunStatus Status,
    string? ArtifactReference = null,
    Guid? ArtifactId = null,
    DateTimeOffset? StartedUtc = null,
    DateTimeOffset? CompletedUtc = null,
    string? Branch = null,
    string? CommitSha = null,
    bool RequireCurrentRepositoryState = true,
    string? Name = null,
    string? Summary = null,
    string? Evidence = null,
    DateTimeOffset? ObservedUtc = null);

public sealed record FindingRecordRequest(
    Guid ProjectId,
    Guid? RepositoryId,
    FindingCategory Category,
    string Title,
    string Description,
    FindingSeverity Severity,
    FindingStatus Status,
    string? Resolution = null,
    string? ResolutionEvidence = null,
    string? Branch = null,
    string? CommitSha = null,
    string? AuthoritativeReference = null,
    Guid? FindingId = null,
    int? ExpectedRevision = null,
    string? Component = null,
    string? Location = null,
    string? Remediation = null);

public sealed record HandoffCreateRequest(
    Guid ProjectId,
    Guid? RepositoryId,
    string Objective,
    string CompletedWork,
    string ActiveWork,
    string ActiveBlockers,
    string ImportantDecisions,
    string LatestTestStatus,
    string UnresolvedFindings,
    IReadOnlyList<string> RelevantFiles,
    IReadOnlyList<string> RelevantArtifacts,
    string RecommendedNextAction,
    Guid? PhaseId = null);

public sealed record BootstrapReference(string Kind, Guid? Id, string Value);

public sealed record BootstrapItem(Guid Id, string Kind, string Title, string Summary, RecordFreshness Freshness, string? Reference = null);
public sealed record BootstrapValidation(Guid Id, TestRunStatus Status, long Passed, long Failed, long Skipped, string? Branch, string? CommitSha, RecordFreshness Freshness, DateTimeOffset CreatedUtc);

public sealed record ProjectBootstrap(
    ProjectResolution Resolution,
    BootstrapItem? CurrentPhase,
    IReadOnlyList<BootstrapItem> Blockers,
    IReadOnlyList<BootstrapItem> Findings,
    BootstrapValidation? LatestValidation,
    IReadOnlyList<BootstrapItem> Decisions,
    IReadOnlyList<BootstrapReference> References,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> AvailableCategories);

public interface IAIContextApplication
{
    Task<GitRepositoryState> ObserveRepositoryAsync(string repositoryPath, CancellationToken cancellationToken = default);
    Task<ProjectResolution> ResolveProjectAsync(ProjectResolutionRequest request, CancellationToken cancellationToken = default);
    Task<ProjectBootstrap> BootstrapAsync(ProjectBootstrapRequest request, CancellationToken cancellationToken = default);
    Task<ContextSearchResult> SearchContextAsync(ContextSearchRequest request, CancellationToken cancellationToken = default);
    Task<ContextEntryRecord> RecordContextAsync(ContextRecordRequest request, CancellationToken cancellationToken = default);
    Task<PhaseRecord> UpsertPhaseAsync(PhaseRecordRequest request, CancellationToken cancellationToken = default);
    Task<DecisionListResult> ListDecisionsAsync(DecisionListRequest request, CancellationToken cancellationToken = default);
    Task<DecisionRecord> RecordDecisionAsync(DecisionRecordRequest request, CancellationToken cancellationToken = default);
    Task<TestRunRecord> RecordTestRunAsync(TestRecordRequest request, CancellationToken cancellationToken = default);
    Task<FindingRecord> RecordFindingAsync(FindingRecordRequest request, CancellationToken cancellationToken = default);
    Task<HandoffRecord> CreateHandoffAsync(HandoffCreateRequest request, CancellationToken cancellationToken = default);
}
