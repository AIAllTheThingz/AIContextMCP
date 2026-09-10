namespace AIContextMCP.Core;

public static class StorageLimits
{
    public const int Identifier = 512;
    public const int Name = 256;
    public const int Title = 256;
    public const int ShortText = 1_024;
    public const int Summary = 8_192;
    public const int Content = 32_768;
    public const int Rationale = 16_384;
    public const int HandoffField = 8_192;
    public const int HandoffAggregate = 32_768;
    public const int Reference = 2_048;
    public const int Command = 2_048;
    public const int CollectionCount = 100;
    public const int MutationReceiptBytes = 32 * 1024;
}

public enum ProjectStatus { Active, Archived }
public enum RepositoryStatus { Active, Archived }
public enum ContextStatus { Active, Superseded, Archived }
public enum DecisionStatus { Proposed, Accepted, Rejected, Superseded, Withdrawn }
public enum PhaseStatus { Planned, Active, Completed, Blocked }
public enum TestRunStatus { Passed, Failed, Skipped, Blocked, Unknown }
public enum FindingCategory { Implementation, Testing, Security, Documentation, Architecture }
public enum FindingSeverity { Critical, High, Medium, Low, Info }
public enum FindingStatus { Open, Resolved, Accepted, Superseded }
public enum McpRecordKind { ContextEntry, Decision, TestRun, Finding, Handoff }
public enum McpReferenceRole { Source, Artifact, Supersedes, Blocker, Decision, Test, Finding, RelevantFile, RelevantArtifact }

public sealed record ProjectDraft(Guid Id, string StableProjectId, string Name, ProjectStatus Status = ProjectStatus.Active);
public sealed record ProjectRecord(Guid Id, string StableProjectId, string Name, ProjectStatus Status, DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc);

public sealed record RepositoryDraft(
    Guid Id,
    Guid ProjectId,
    string CanonicalPath,
    string? Remote,
    string? Organization,
    string RepositoryName,
    string DefaultBranch,
    string? LastKnownBranch,
    string? LastKnownCommitSha,
    RepositoryStatus Status = RepositoryStatus.Active);

public sealed record RepositoryRecord(
    Guid Id,
    Guid ProjectId,
    string CanonicalPath,
    string? Remote,
    string? Organization,
    string RepositoryName,
    string DefaultBranch,
    string? LastKnownBranch,
    string? LastKnownCommitSha,
    RepositoryStatus Status,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

public sealed record ContextEntryDraft(
    Guid Id,
    Guid ProjectId,
    Guid? RepositoryId,
    string Category,
    string Title,
    string Summary,
    string? Content,
    string? AuthoritativeReference,
    string? Branch,
    string? CommitSha,
    ContextStatus Status,
    string? ContentHash,
    int Tier = 2,
    string? Objective = null,
    Guid? PhaseId = null);

public sealed record ContextEntryRecord(
    Guid Id,
    Guid ProjectId,
    Guid? RepositoryId,
    string Category,
    string Title,
    string Summary,
    string? Content,
    string? AuthoritativeReference,
    string? Branch,
    string? CommitSha,
    ContextStatus Status,
    string? ContentHash,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    DateTimeOffset? SupersededUtc,
    int Tier = 2,
    string? Objective = null,
    Guid? PhaseId = null);

public sealed record DecisionDraft(
    Guid Id,
    Guid ProjectId,
    Guid? RepositoryId,
    string Category,
    string Component,
    string Title,
    string Decision,
    string Rationale,
    string? AuthoritativeReference,
    string? ResolutionEvidence,
    string? OriginatingCommitSha,
    DecisionStatus Status = DecisionStatus.Accepted,
    Guid? SupersedesDecisionId = null,
    string? Branch = null,
    int Version = 1);

public sealed record DecisionRecord(
    Guid Id,
    Guid ProjectId,
    Guid? RepositoryId,
    string Category,
    string Component,
    string Title,
    string Decision,
    string Rationale,
    string? AuthoritativeReference,
    string? ResolutionEvidence,
    string? OriginatingCommitSha,
    DecisionStatus Status,
    Guid? SupersedesDecisionId,
    Guid? SupersededByDecisionId,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    DateTimeOffset? SupersededUtc,
    string? Branch = null,
    int Version = 1);

public sealed record PhaseDraft(
    Guid Id,
    Guid ProjectId,
    Guid? RepositoryId,
    string PhaseKey,
    string Objective,
    PhaseStatus Status,
    string? Branch,
    string? CommitSha,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? CompletedUtc,
    int Version = 1,
    Guid? ContextEntryId = null);

public sealed record PhaseRecord(
    Guid Id,
    Guid ProjectId,
    Guid? RepositoryId,
    string PhaseKey,
    string Objective,
    PhaseStatus Status,
    string? Branch,
    string? CommitSha,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? CompletedUtc,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    int Version = 1,
    Guid? ContextEntryId = null);

public sealed record TestRunDraft(
    Guid Id,
    Guid ProjectId,
    Guid? RepositoryId,
    string Command,
    string? Branch,
    string? CommitSha,
    long Passed,
    long Failed,
    long Skipped,
    long Total,
    TestRunStatus Status,
    Guid? ArtifactId,
    string? ArtifactReference,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? CompletedUtc,
    string? Name = null,
    string? Summary = null,
    string? Evidence = null,
    DateTimeOffset? ObservedUtc = null);

public sealed record TestRunRecord(
    Guid Id,
    Guid ProjectId,
    Guid? RepositoryId,
    string Command,
    string? Branch,
    string? CommitSha,
    long Passed,
    long Failed,
    long Skipped,
    long Total,
    TestRunStatus Status,
    Guid? ArtifactId,
    string? ArtifactReference,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? CompletedUtc,
    DateTimeOffset CreatedUtc,
    string? Name = null,
    string? Summary = null,
    string? Evidence = null,
    DateTimeOffset? ObservedUtc = null);

public sealed record FindingDraft(
    Guid Id,
    Guid ProjectId,
    Guid? RepositoryId,
    FindingCategory Category,
    string Title,
    string Description,
    FindingSeverity Severity,
    FindingStatus Status,
    string? Resolution,
    string? ResolutionEvidence,
    string? Branch = null,
    string? CommitSha = null,
    string? AuthoritativeReference = null,
    string? Component = null,
    string? Location = null,
    string? Remediation = null);

public sealed record FindingRevision(
    Guid Id,
    int ExpectedRevision,
    FindingCategory Category,
    string Title,
    string Description,
    FindingSeverity Severity,
    FindingStatus Status,
    string? Resolution,
    string? ResolutionEvidence,
    string? Branch = null,
    string? CommitSha = null,
    string? AuthoritativeReference = null,
    string? Component = null,
    string? Location = null,
    string? Remediation = null);

public sealed record FindingRecord(
    Guid Id,
    int Revision,
    Guid ProjectId,
    Guid? RepositoryId,
    FindingCategory Category,
    string Title,
    string Description,
    FindingSeverity Severity,
    FindingStatus Status,
    string? Resolution,
    string? ResolutionEvidence,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    string? Branch = null,
    string? CommitSha = null,
    string? AuthoritativeReference = null,
    string? Component = null,
    string? Location = null,
    string? Remediation = null);

public sealed record ContextEntryQuery(
    Guid ProjectId,
    Guid? RepositoryId = null,
    string? Category = null,
    string? Branch = null,
    string? CommitSha = null,
    ContextStatus? Status = null,
    DateTimeOffset? SinceUtc = null,
    string? Text = null,
    bool BranchIsNull = false,
    int MaxResults = 20,
    bool RepositoryIsNull = false);

public sealed record DecisionQuery(
    Guid ProjectId,
    Guid? RepositoryId = null,
    string? Category = null,
    string? Component = null,
    string? Branch = null,
    string? CommitSha = null,
    DecisionStatus? Status = null,
    DateTimeOffset? SinceUtc = null,
    bool IncludeSuperseded = false,
    bool BranchIsNull = false,
    int MaxResults = 20,
    bool RepositoryIsNull = false);

public sealed record PhaseQuery(
    Guid ProjectId,
    Guid? RepositoryId = null,
    PhaseStatus? Status = null,
    string? Branch = null,
    string? CommitSha = null,
    bool BranchIsNull = false,
    int MaxResults = 20,
    bool RepositoryIsNull = false);

public sealed record TestRunQuery(
    Guid ProjectId,
    Guid? RepositoryId = null,
    string? Branch = null,
    string? CommitSha = null,
    TestRunStatus? Status = null,
    bool CompletedOnly = false,
    int MaxResults = 20,
    bool RepositoryIsNull = false);

public sealed record FindingQuery(
    Guid ProjectId,
    Guid? RepositoryId = null,
    string? Branch = null,
    string? CommitSha = null,
    FindingStatus? Status = null,
    bool BranchIsNull = false,
    int MaxResults = 20,
    bool RepositoryIsNull = false);

public sealed record ArtifactDraft(
    Guid Id,
    Guid ProjectId,
    Guid? RepositoryId,
    string ArtifactType,
    string Title,
    string Reference,
    string ContentHash,
    long SizeBytes,
    string? Branch,
    string? CommitSha);

public sealed record ArtifactRecord(
    Guid Id,
    Guid ProjectId,
    Guid? RepositoryId,
    string ArtifactType,
    string Title,
    string Reference,
    string ContentHash,
    long SizeBytes,
    string? Branch,
    string? CommitSha,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

public sealed record HandoffDraft(
    Guid Id,
    Guid ProjectId,
    Guid? RepositoryId,
    string? Branch,
    string? CommitSha,
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

public sealed record HandoffRecord(
    Guid Id,
    Guid ProjectId,
    Guid? RepositoryId,
    string? Branch,
    string? CommitSha,
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
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    Guid? PhaseId = null);

public sealed record McpSourceReference(
    Guid ProjectId,
    Guid? RepositoryId,
    string Kind,
    Guid? Id = null,
    string? RelativePath = null,
    string? Uri = null,
    string? Hash = null);

public sealed record McpObservationDraft(
    McpRecordKind RecordKind,
    Guid RecordId,
    int Revision,
    Guid ProjectId,
    Guid? RepositoryId,
    string SourceKind,
    McpSourceReference? SourceReference,
    DateTimeOffset SourceObservedUtc,
    string CanonicalRoot,
    string? Branch,
    string? Head,
    string? WorkingTreeFingerprint,
    DateTimeOffset SnapshotObservedUtc,
    string Completeness,
    DateTimeOffset RecordObservedUtc);

public sealed record McpObservationRecord(
    McpRecordKind RecordKind,
    Guid RecordId,
    int Revision,
    Guid ProjectId,
    Guid? RepositoryId,
    string SourceKind,
    McpSourceReference? SourceReference,
    DateTimeOffset SourceObservedUtc,
    string CanonicalRoot,
    string? Branch,
    string? Head,
    string? WorkingTreeFingerprint,
    DateTimeOffset SnapshotObservedUtc,
    string Completeness,
    DateTimeOffset RecordObservedUtc);

public sealed record McpReferenceDraft(
    McpRecordKind RecordKind,
    Guid RecordId,
    int Revision,
    McpReferenceRole Role,
    int Ordinal,
    McpSourceReference Reference);

public sealed record McpReferenceRecord(
    McpRecordKind RecordKind,
    Guid RecordId,
    int Revision,
    McpReferenceRole Role,
    int Ordinal,
    McpSourceReference Reference);

public sealed record McpContextPageQuery(
    Guid ProjectId,
    Guid? RepositoryId = null,
    string? Category = null,
    string? Branch = null,
    string? CommitSha = null,
    ContextStatus? Status = null,
    DateTimeOffset? SinceUtc = null,
    string? Text = null,
    DateTimeOffset? BeforeObservedUtc = null,
    Guid? AfterRecordId = null,
    int MaxResults = 20,
    bool RepositoryIsNull = false);

public sealed record McpDecisionPageQuery(
    Guid ProjectId,
    Guid? RepositoryId = null,
    IReadOnlyList<DecisionStatus>? Statuses = null,
    bool IncludeSuperseded = false,
    DateTimeOffset? BeforeObservedUtc = null,
    Guid? AfterRecordId = null,
    int MaxResults = 50,
    bool RepositoryIsNull = false);

public sealed record McpObservedContextEntry(ContextEntryRecord Entry, DateTimeOffset ObservedUtc);
public sealed record McpObservedDecision(DecisionRecord Decision, DateTimeOffset ObservedUtc);

public sealed record MutationRequest(
    string Scope,
    string Tool,
    string RequestId,
    string CanonicalPayloadHash);

public sealed record MutationReceipt(
    string Scope,
    string Tool,
    string RequestId,
    string CanonicalPayloadHash,
    string ReceiptJson,
    DateTimeOffset IssuedUtc,
    DateTimeOffset ExpiresUtc,
    DateTimeOffset CompletedUtc);

public interface IStorageInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<int> GetSchemaVersionAsync(CancellationToken cancellationToken = default);
}

public interface IProjectStore
{
    Task<ProjectRecord> CreateProjectAsync(ProjectDraft project, CancellationToken cancellationToken = default);
    Task<(ProjectRecord Project, RepositoryRecord Repository)> RegisterProjectAsync(ProjectDraft project, RepositoryDraft repository, CancellationToken cancellationToken = default);
    Task<ProjectRecord?> GetProjectAsync(Guid id, CancellationToken cancellationToken = default);
    Task<ProjectRecord?> GetProjectByStableIdAsync(string stableProjectId, CancellationToken cancellationToken = default);
    Task<ProjectRecord> UpdateProjectAsync(Guid id, string name, ProjectStatus status, CancellationToken cancellationToken = default);
    Task<RepositoryRecord> CreateRepositoryAsync(RepositoryDraft repository, CancellationToken cancellationToken = default);
    Task<RepositoryRecord?> GetRepositoryAsync(Guid id, CancellationToken cancellationToken = default);
    Task<RepositoryRecord?> GetRepositoryByCanonicalPathAsync(string canonicalPath, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RepositoryRecord>> ListRepositoriesByProjectAsync(Guid projectId, int maxResults = 20, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RepositoryRecord>> ListRepositoriesByRemoteAsync(string remote, int maxResults = 20, CancellationToken cancellationToken = default);
    Task<RepositoryRecord> UpdateRepositoryAsync(RepositoryDraft repository, CancellationToken cancellationToken = default);
}

public interface IContextEntryStore
{
    Task<ContextEntryRecord> CreateContextEntryAsync(ContextEntryDraft entry, CancellationToken cancellationToken = default);
    Task<ContextEntryRecord?> GetContextEntryAsync(Guid id, CancellationToken cancellationToken = default);
    Task<ContextEntryRecord?> GetContextEntryByContentHashAsync(Guid projectId, Guid? repositoryId, string contentHash, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ContextEntryRecord>> ListContextEntriesAsync(ContextEntryQuery query, CancellationToken cancellationToken = default);
}

public interface IDecisionStore
{
    Task<DecisionRecord> CreateDecisionAsync(DecisionDraft decision, CancellationToken cancellationToken = default);
    Task<DecisionRecord?> GetDecisionAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DecisionRecord>> ListDecisionsAsync(Guid projectId, bool includeSuperseded = false, int maxResults = 50, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DecisionRecord>> ListDecisionsAsync(DecisionQuery query, CancellationToken cancellationToken = default);
}

public interface IPhaseStore
{
    Task<PhaseRecord> CreatePhaseAsync(PhaseDraft phase, CancellationToken cancellationToken = default);
    Task<PhaseRecord> UpsertPhaseAsync(PhaseDraft phase, int? expectedVersion, CancellationToken cancellationToken = default);
    Task<PhaseRecord?> GetPhaseAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PhaseRecord>> ListPhasesAsync(PhaseQuery query, CancellationToken cancellationToken = default);
}

public interface ITestRunStore
{
    Task<TestRunRecord> CreateTestRunAsync(TestRunDraft testRun, CancellationToken cancellationToken = default);
    Task<TestRunRecord?> GetTestRunAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TestRunRecord>> ListTestRunsAsync(TestRunQuery query, CancellationToken cancellationToken = default);
}

public interface IFindingStore
{
    Task<FindingRecord> CreateFindingAsync(FindingDraft finding, CancellationToken cancellationToken = default);
    Task<FindingRecord?> GetFindingAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FindingRecord>> ListFindingHistoryAsync(Guid id, int maxResults = 50, CancellationToken cancellationToken = default);
    Task<FindingRecord> ReviseFindingAsync(FindingRevision revision, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FindingRecord>> ListFindingsAsync(FindingQuery query, CancellationToken cancellationToken = default);
}

public interface IArtifactStore
{
    Task<ArtifactRecord> CreateArtifactAsync(ArtifactDraft artifact, CancellationToken cancellationToken = default);
    Task<ArtifactRecord?> GetArtifactAsync(Guid id, CancellationToken cancellationToken = default);
}

public interface IHandoffStore
{
    Task<HandoffRecord> CreateHandoffAsync(HandoffDraft handoff, CancellationToken cancellationToken = default);
    Task<HandoffRecord?> GetHandoffAsync(Guid id, CancellationToken cancellationToken = default);
    Task<HandoffRecord?> GetLatestHandoffAsync(Guid projectId, Guid? repositoryId, CancellationToken cancellationToken = default);
}

public interface IMcpMetadataStore
{
    Task<McpObservationRecord> CreateMcpObservationAsync(McpObservationDraft observation, CancellationToken cancellationToken = default);
    Task<McpObservationRecord?> GetMcpObservationAsync(McpRecordKind recordKind, Guid recordId, int revision = 1, CancellationToken cancellationToken = default);
    Task<McpReferenceRecord> CreateMcpReferenceAsync(McpReferenceDraft reference, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<McpReferenceRecord>> ListMcpReferencesAsync(McpRecordKind recordKind, Guid recordId, int revision = 1, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<McpObservedContextEntry>> ListMcpContextEntriesAsync(McpContextPageQuery query, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<McpObservedDecision>> ListMcpDecisionsAsync(McpDecisionPageQuery query, CancellationToken cancellationToken = default);
}

public interface IMutationStore
{
    Task<MutationReceipt> ExecuteMutationAsync(
        MutationRequest request,
        Func<IAIContextStorage, CancellationToken, Task<string>> callback,
        CancellationToken cancellationToken = default);
}

public interface IAIContextStorage : IStorageInitializer, IProjectStore, IContextEntryStore, IDecisionStore, IPhaseStore, ITestRunStore, IFindingStore, IArtifactStore, IHandoffStore, IMcpMetadataStore, IMutationStore
{
}
