using System.Text.Json;
using System.Security.Cryptography;

namespace AIContextMCP.Core;

public sealed class AIContextApplication : IAIContextApplication
{
    private readonly IAIContextStorage _storage;
    private readonly GitRepositoryInspector _git;
    private readonly ApplicationCoreOptions _options;
    private readonly Action<string, string>? _operationLog;

    public AIContextApplication(IAIContextStorage storage, ApplicationCoreOptions options, Action<string, string>? operationLog = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (options.BootstrapBytes is < ApplicationLimits.MinimumBootstrapBytes or > ApplicationLimits.BootstrapBytes)
        {
            throw new ApplicationException(ApplicationErrorCode.InvalidInput, "Bootstrap output limit is invalid.");
        }

        var boundary = new RepositoryBoundary(options.ApprovedRepositoryRoots);
        _git = new GitRepositoryInspector(boundary);
        _operationLog = operationLog;
    }

    public Task<ProjectResolution> ResolveProjectAsync(ProjectResolutionRequest request, CancellationToken cancellationToken = default) =>
        ExecuteAsync("resolve", () => ResolveProjectCoreAsync(request, cancellationToken));

    public Task<GitRepositoryState> ObserveRepositoryAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
        ExecuteAsync("observe", () => _git.InspectAsync(repositoryPath, cancellationToken));

    private async Task<ProjectResolution> ResolveProjectCoreAsync(ProjectResolutionRequest request, CancellationToken cancellationToken)
    {
        return (await ResolveAsync(request, cancellationToken)).Public;
    }

    public Task<ProjectBootstrap> BootstrapAsync(ProjectBootstrapRequest request, CancellationToken cancellationToken = default) =>
        ExecuteAsync("bootstrap", () => BootstrapCoreAsync(request, cancellationToken));

    private async Task<ProjectBootstrap> BootstrapCoreAsync(ProjectBootstrapRequest request, CancellationToken cancellationToken)
    {
        var scope = await ResolveAsync(new ProjectResolutionRequest(request.RepositoryPath, request.Register, request.RebindRepositoryId), cancellationToken);
        return await BuildBootstrapAsync(scope, cancellationToken);
    }

    public Task<ContextSearchResult> SearchContextAsync(ContextSearchRequest request, CancellationToken cancellationToken = default) =>
        ExecuteAsync("context.search", () => SearchContextCoreAsync(request, cancellationToken));

    private async Task<ContextSearchResult> SearchContextCoreAsync(ContextSearchRequest request, CancellationToken cancellationToken)
    {
        if (request.MaxResults is < 1 or > ApplicationLimits.SearchResults)
        {
            throw new ApplicationException(ApplicationErrorCode.ContentTooLarge, "Requested result count exceeds its limit.");
        }

        var records = await StorageAsync(() => _storage.ListContextEntriesAsync(new ContextEntryQuery(
            request.ProjectId, request.RepositoryId, request.Category, request.Branch, request.CommitSha, request.Status, request.SinceUtc, request.Text, false, Math.Min(request.MaxResults + 1, StorageLimits.CollectionCount)), cancellationToken));
        var git = await TryGetGitStateAsync(request.ProjectId, request.RepositoryId, cancellationToken);
        var warnings = git is null && request.RepositoryId is not null ? ["Current Git state is unavailable; freshness is unknown."] : git is null ? [] : GitWarnings(git.Git);
        var entries = await Task.WhenAll(records.Take(request.MaxResults).Select(record => ToSearchItemAsync(record, git?.Git, cancellationToken)));
        return FitSearch(new ContextSearchResult(
            entries,
            records.Count > request.MaxResults,
            warnings));
    }

    public Task<ContextEntryRecord> RecordContextAsync(ContextRecordRequest request, CancellationToken cancellationToken = default) =>
        ExecuteAsync("context.record", () => RecordContextCoreAsync(request, cancellationToken));

    private async Task<ContextEntryRecord> RecordContextCoreAsync(ContextRecordRequest request, CancellationToken cancellationToken)
    {
        var scope = await ResolveWriteScopeAsync(request.ProjectId, request.RepositoryId, cancellationToken);
        var reference = MetadataReferenceValidator.Optional(request.AuthoritativeReference, "authoritative reference");
        var branch = request.Branch ?? scope?.Git.Branch;
        var commit = request.CommitSha ?? scope?.Git.HeadCommitSha;
        var contentHash = ComputeContextHash(request, reference, branch, commit);
        if (request.ContentHash is not null && !string.Equals(request.ContentHash, contentHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new ApplicationException(ApplicationErrorCode.InvalidInput, "Content hash does not match the supplied record.");
        }

        var duplicate = await StorageAsync(() => _storage.GetContextEntryByContentHashAsync(request.ProjectId, request.RepositoryId, contentHash, cancellationToken));
        if (duplicate is not null)
        {
            throw new ApplicationException(ApplicationErrorCode.DuplicateRecord, "An equivalent context record already exists.");
        }

        if (request.PhaseId is { } phaseId)
        {
            var phase = await StorageAsync(() => _storage.GetPhaseAsync(phaseId, cancellationToken))
                ?? throw new ApplicationException(ApplicationErrorCode.NotFound, "Phase was not found.");
            if (phase.ProjectId != request.ProjectId || phase.RepositoryId != request.RepositoryId)
            {
                throw new ApplicationException(ApplicationErrorCode.InvalidInput, "Phase is outside the resolved project scope.");
            }
        }

        return await StorageAsync(() => _storage.CreateContextEntryAsync(new ContextEntryDraft(
            request.EntryId ?? Guid.NewGuid(), request.ProjectId, request.RepositoryId, request.Category, request.Title, request.Summary, request.Content, reference,
            branch, commit, request.Status, contentHash, request.Tier, request.Objective, request.PhaseId), cancellationToken));
    }

    public Task<PhaseRecord> UpsertPhaseAsync(PhaseRecordRequest request, CancellationToken cancellationToken = default) =>
        ExecuteAsync("phase.upsert", () => UpsertPhaseCoreAsync(request, cancellationToken));

    private async Task<PhaseRecord> UpsertPhaseCoreAsync(PhaseRecordRequest request, CancellationToken cancellationToken)
    {
        var scope = await ResolveWriteScopeAsync(request.ProjectId, request.RepositoryId, cancellationToken);
        return await StorageAsync(() => _storage.UpsertPhaseAsync(new PhaseDraft(
            request.Id, request.ProjectId, request.RepositoryId, request.PhaseKey, request.Objective, request.Status,
            request.Branch ?? scope?.Git.Branch, request.CommitSha ?? scope?.Git.HeadCommitSha, request.StartedUtc, request.CompletedUtc, 1, request.ContextEntryId), request.ExpectedVersion, cancellationToken));
    }

    public Task<DecisionListResult> ListDecisionsAsync(DecisionListRequest request, CancellationToken cancellationToken = default) =>
        ExecuteAsync("decision.list", () => ListDecisionsCoreAsync(request, cancellationToken));

    private async Task<DecisionListResult> ListDecisionsCoreAsync(DecisionListRequest request, CancellationToken cancellationToken)
    {
        if (request.MaxResults is < 1 or > ApplicationLimits.SearchResults)
        {
            throw new ApplicationException(ApplicationErrorCode.ContentTooLarge, "Requested result count exceeds its limit.");
        }

        var records = await StorageAsync(() => _storage.ListDecisionsAsync(new DecisionQuery(
            request.ProjectId, request.RepositoryId, request.Category, request.Component, request.Branch, request.CommitSha, request.Status,
            request.SinceUtc, request.IncludeSuperseded, false, Math.Min(request.MaxResults + 1, StorageLimits.CollectionCount)), cancellationToken));
        var scope = await TryGetGitStateAsync(request.ProjectId, request.RepositoryId, cancellationToken);
        var warnings = scope is null && request.RepositoryId is not null ? ["Current Git state is unavailable; freshness is unknown."] : scope is null ? [] : GitWarnings(scope.Git);
        var decisions = await Task.WhenAll(records.Take(request.MaxResults).Select(record => ToDecisionItemAsync(record, scope?.Git, cancellationToken)));
        return FitDecisions(new DecisionListResult(
            decisions,
            records.Count > request.MaxResults,
            warnings));
    }

    public Task<DecisionRecord> RecordDecisionAsync(DecisionRecordRequest request, CancellationToken cancellationToken = default) =>
        ExecuteAsync("decision.record", () => RecordDecisionCoreAsync(request, cancellationToken));

    private async Task<DecisionRecord> RecordDecisionCoreAsync(DecisionRecordRequest request, CancellationToken cancellationToken)
    {
        var scope = await ResolveWriteScopeAsync(request.ProjectId, request.RepositoryId, cancellationToken);
        return await StorageAsync(() => _storage.CreateDecisionAsync(new DecisionDraft(
            Guid.NewGuid(), request.ProjectId, request.RepositoryId, request.Category, request.Component, request.Title, request.Decision, request.Rationale,
            MetadataReferenceValidator.Optional(request.AuthoritativeReference, "authoritative reference"), request.ResolutionEvidence,
            request.CommitSha ?? scope?.Git.HeadCommitSha, request.Status, request.SupersedesDecisionId, request.Branch ?? scope?.Git.Branch), cancellationToken));
    }

    public Task<TestRunRecord> RecordTestRunAsync(TestRecordRequest request, CancellationToken cancellationToken = default) =>
        ExecuteAsync("test.record", () => RecordTestRunCoreAsync(request, cancellationToken));

    private async Task<TestRunRecord> RecordTestRunCoreAsync(TestRecordRequest request, CancellationToken cancellationToken)
    {
        var scope = await ResolveWriteScopeAsync(request.ProjectId, request.RepositoryId, cancellationToken);
        if (request.RequireCurrentRepositoryState)
        {
            if (scope is null
                || scope.Git.WorkingTree != WorkingTreeState.Clean
                || (request.Branch is not null && request.Branch != scope.Git.Branch)
                || (request.CommitSha is not null && !string.Equals(request.CommitSha, scope.Git.HeadCommitSha, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ApplicationException(ApplicationErrorCode.StaleState, "Current validation requires a clean matching Git state.");
            }
        }

        return await StorageAsync(() => _storage.CreateTestRunAsync(new TestRunDraft(
            Guid.NewGuid(), request.ProjectId, request.RepositoryId, request.Command, request.Branch ?? scope?.Git.Branch,
            request.CommitSha ?? scope?.Git.HeadCommitSha, request.Passed, request.Failed, request.Skipped, request.Total, request.Status,
            request.ArtifactId, MetadataReferenceValidator.Optional(request.ArtifactReference, "artifact reference", allowWebUri: false), request.StartedUtc, request.CompletedUtc,
            request.Name, request.Summary, request.Evidence, request.ObservedUtc), cancellationToken));
    }

    public Task<FindingRecord> RecordFindingAsync(FindingRecordRequest request, CancellationToken cancellationToken = default) =>
        ExecuteAsync("finding.record", () => RecordFindingCoreAsync(request, cancellationToken));

    private async Task<FindingRecord> RecordFindingCoreAsync(FindingRecordRequest request, CancellationToken cancellationToken)
    {
        var scope = await ResolveWriteScopeAsync(request.ProjectId, request.RepositoryId, cancellationToken);
        var branch = request.Branch ?? scope?.Git.Branch;
        var commit = request.CommitSha ?? scope?.Git.HeadCommitSha;
        var reference = MetadataReferenceValidator.Optional(request.AuthoritativeReference, "authoritative reference");
        if (request.FindingId is not { } findingId)
        {
            return await StorageAsync(() => _storage.CreateFindingAsync(new FindingDraft(Guid.NewGuid(), request.ProjectId, request.RepositoryId, request.Category, request.Title, request.Description, request.Severity, request.Status, request.Resolution, request.ResolutionEvidence, branch, commit, reference, request.Component, request.Location, request.Remediation), cancellationToken));
        }

        if (request.ExpectedRevision is not { } revision)
        {
            throw new ApplicationException(ApplicationErrorCode.InvalidTransition, "Finding updates require an expected revision.");
        }

        var current = await StorageAsync(() => _storage.GetFindingAsync(findingId, cancellationToken))
            ?? throw new ApplicationException(ApplicationErrorCode.NotFound, "Finding was not found.");
        if (current.ProjectId != request.ProjectId || current.RepositoryId != request.RepositoryId)
        {
            throw new ApplicationException(ApplicationErrorCode.InvalidTransition, "Finding update is outside the resolved project scope.");
        }

        return await StorageAsync(() => _storage.ReviseFindingAsync(new FindingRevision(findingId, revision, request.Category, request.Title, request.Description, request.Severity, request.Status, request.Resolution, request.ResolutionEvidence, branch, commit, reference, request.Component, request.Location, request.Remediation), cancellationToken));
    }

    public Task<HandoffRecord> CreateHandoffAsync(HandoffCreateRequest request, CancellationToken cancellationToken = default) =>
        ExecuteAsync("handoff.create", () => CreateHandoffCoreAsync(request, cancellationToken));

    private async Task<HandoffRecord> CreateHandoffCoreAsync(HandoffCreateRequest request, CancellationToken cancellationToken)
    {
        var scope = await ResolveWriteScopeAsync(request.ProjectId, request.RepositoryId, cancellationToken);
        var bootstrap = scope is null ? null : await BuildBootstrapAsync(scope, cancellationToken);
        var files = MetadataReferenceValidator.RelativeList(request.RelevantFiles, "relevant files");
        var artifacts = MetadataReferenceValidator.RelativeList(request.RelevantArtifacts, "relevant artifacts");
        var draft = new HandoffDraft(
            Guid.NewGuid(), request.ProjectId, request.RepositoryId, scope?.Git.Branch, scope?.Git.HeadCommitSha,
            RequiredText(request.Objective, "objective"), RequiredText(request.CompletedWork, "completed work"), RequiredText(request.ActiveWork, "active work"),
            MergeSection(request.ActiveBlockers, bootstrap?.Blockers.Select(item => item.Title) ?? []),
            MergeSection(request.ImportantDecisions, bootstrap?.Decisions.Select(item => item.Title) ?? []),
            MergeSection(request.LatestTestStatus, bootstrap?.LatestValidation is { } test ? [$"{test.Status} ({test.Freshness}): {test.Passed}/{test.Failed}/{test.Skipped}"] : []),
            MergeSection(request.UnresolvedFindings, bootstrap?.Findings.Select(item => item.Title) ?? []), files, artifacts,
            RequiredText(request.RecommendedNextAction, "recommended next action"), request.PhaseId);
        ValidateHandoffBytes(draft);
        return await StorageAsync(() => _storage.CreateHandoffAsync(draft, cancellationToken));
    }

    private async Task<ResolvedScope> ResolveAsync(ProjectResolutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RebindRepositoryId is not null && !request.Register)
        {
            throw new ApplicationException(ApplicationErrorCode.InvalidInput, "Repository rebinding requires explicit registration.");
        }

        var git = await _git.InspectAsync(request.RepositoryPath, cancellationToken);
        var existing = await StorageAsync(() => _storage.GetRepositoryByCanonicalPathAsync(git.CanonicalPath, cancellationToken));
        if (existing is not null)
        {
            var project = await RequireProjectAsync(existing.ProjectId, cancellationToken);
            if (request.RebindRepositoryId is not null && request.RebindRepositoryId != existing.Id)
            {
                throw new ApplicationException(ApplicationErrorCode.ProjectIdentityConflict, "Repository path is already bound to another identity.");
            }

            if (request.Register)
            {
                existing = await StorageAsync(() => _storage.UpdateRepositoryAsync(ToRepositoryDraft(existing, git), cancellationToken));
            }

            return BuildScope(project, existing, git);
        }

        var remoteMatches = git.Remote is null
            ? Array.Empty<RepositoryRecord>()
            : (await StorageAsync(() => _storage.ListRepositoriesByRemoteAsync(git.Remote, StorageLimits.CollectionCount, cancellationToken))).ToArray();
        if (request.RebindRepositoryId is { } rebindId)
        {
            var repository = await StorageAsync(() => _storage.GetRepositoryAsync(rebindId, cancellationToken))
                ?? throw new ApplicationException(ApplicationErrorCode.NotFound, "Repository to rebind was not found.");
            if (git.Remote is null || !string.Equals(repository.Remote, git.Remote, StringComparison.OrdinalIgnoreCase))
            {
                throw new ApplicationException(ApplicationErrorCode.ProjectIdentityConflict, "Repository rebind requires the same verified remote identity.");
            }

            var project = await RequireProjectAsync(repository.ProjectId, cancellationToken);
            repository = await StorageAsync(() => _storage.UpdateRepositoryAsync(ToRepositoryDraft(repository, git), cancellationToken));
            return BuildScope(project, repository, git);
        }

        if (!request.Register)
        {
            throw new ApplicationException(remoteMatches.Length > 0 ? ApplicationErrorCode.ProjectIdentityConflict : ApplicationErrorCode.NotFound,
                remoteMatches.Length > 0 ? "Repository identity requires an explicit rebind or registration decision." : "Repository is not registered.");
        }

        var projectId = Guid.NewGuid();
        var projectDraft = new ProjectDraft(projectId, $"project-{projectId:N}", Truncate(git.RepositoryName, StorageLimits.Name));
        var repositoryDraft = new RepositoryDraft(Guid.NewGuid(), projectId, git.CanonicalPath, git.Remote, git.Organization, git.RepositoryName, git.Branch ?? "HEAD", git.Branch, git.HeadCommitSha);
        var registered = await StorageAsync(() => _storage.RegisterProjectAsync(projectDraft, repositoryDraft, cancellationToken));
        return BuildScope(registered.Project, registered.Repository, git);
    }

    private async Task<ProjectBootstrap> BuildBootstrapAsync(ResolvedScope scope, CancellationToken cancellationToken)
    {
        var exactPhases = await StorageAsync(() => _storage.ListPhasesAsync(new PhaseQuery(scope.Project.Id, scope.Repository.Id, PhaseStatus.Active, scope.Git.Branch, scope.Git.HeadCommitSha, false, 2), cancellationToken));
        var branchPhases = scope.Git.Branch is null ? Array.Empty<PhaseRecord>() : await StorageAsync(() => _storage.ListPhasesAsync(new PhaseQuery(scope.Project.Id, scope.Repository.Id, PhaseStatus.Active, scope.Git.Branch, null, false, 2), cancellationToken));
        var globalPhases = await StorageAsync(() => _storage.ListPhasesAsync(new PhaseQuery(scope.Project.Id, scope.Repository.Id, PhaseStatus.Active, null, null, true, 2), cancellationToken));
        var projectPhases = await StorageAsync(() => _storage.ListPhasesAsync(new PhaseQuery(scope.Project.Id, null, PhaseStatus.Active, null, null, false, 2, true), cancellationToken));
        var phases = exactPhases.Concat(branchPhases).Concat(globalPhases).Concat(projectPhases).DistinctBy(phase => phase.Id).Take(2).ToArray();
        var phase = phases.FirstOrDefault();
        var blockers = await RelevantContextsAsync(scope, "blocker", ApplicationLimits.BootstrapBlockers, cancellationToken);
        var findings = await RelevantFindingsAsync(scope, ApplicationLimits.BootstrapFindings, cancellationToken);
        var tests = await StorageAsync(() => _storage.ListTestRunsAsync(new TestRunQuery(scope.Project.Id, scope.Repository.Id, scope.Git.Branch, scope.Git.HeadCommitSha, null, false, 1), cancellationToken));
        var projectTests = await StorageAsync(() => _storage.ListTestRunsAsync(new TestRunQuery(scope.Project.Id, null, scope.Git.Branch, scope.Git.HeadCommitSha, null, false, 1, true), cancellationToken));
        var latest = tests.Concat(projectTests).OrderByDescending(test => test.CompletedUtc ?? test.CreatedUtc).FirstOrDefault();
        if (latest is null)
        {
            var historical = await StorageAsync(() => _storage.ListTestRunsAsync(new TestRunQuery(scope.Project.Id, scope.Repository.Id, null, null, null, false, 1), cancellationToken));
            var projectHistorical = await StorageAsync(() => _storage.ListTestRunsAsync(new TestRunQuery(scope.Project.Id, null, null, null, null, false, 1, true), cancellationToken));
            latest = historical.Concat(projectHistorical).OrderByDescending(test => test.CompletedUtc ?? test.CreatedUtc).FirstOrDefault();
        }

        var decisions = await RelevantDecisionsAsync(scope, ApplicationLimits.BootstrapDecisions, cancellationToken);
        var warnings = scope.Warnings.Concat(GitWarnings(scope.Git)).Distinct(StringComparer.Ordinal).ToList();
        if (phases.Length > 1)
        {
            warnings.Add("Multiple active phases require resolution before one can be authoritative.");
        }

        var phaseItem = phase is null ? null : await ToPhaseBootstrapItemAsync(phase, scope.Git, cancellationToken);
        var blockerItems = await Task.WhenAll(blockers.Select(record => FreshenBootstrapItemAsync(
            new BootstrapItem(record.Id, "blocker", Truncate(record.Title, 128), Truncate(record.Summary, 256), RecordFreshness.Unknown, TruncateOptional(record.AuthoritativeReference, 256)),
            McpRecordKind.ContextEntry, 1, record.Branch, record.CommitSha, scope.Git, cancellationToken)));
        var findingItems = await Task.WhenAll(findings.Select(record => FreshenBootstrapItemAsync(
            new BootstrapItem(record.Id, "finding", Truncate(record.Title, 128), Truncate(record.Description, 256), RecordFreshness.Unknown, TruncateOptional(record.AuthoritativeReference, 256)),
            McpRecordKind.Finding, record.Revision, record.Branch, record.CommitSha, scope.Git, cancellationToken)));
        var decisionItems = await Task.WhenAll(decisions.Select(record => FreshenBootstrapItemAsync(
            new BootstrapItem(record.Id, "decision", Truncate(record.Title, 128), Truncate(record.Decision, 256), RecordFreshness.Unknown, TruncateOptional(record.AuthoritativeReference, 256)),
            McpRecordKind.Decision, 1, record.Branch, record.OriginatingCommitSha, scope.Git, cancellationToken)));
        var validation = latest is null ? null : await ToBootstrapValidationAsync(latest, scope.Git, cancellationToken);
        if (validation is { Freshness: RecordFreshness.Stale }) warnings.Add("Latest validation is stale for the current Git state.");
        if (validation is { Freshness: RecordFreshness.Unknown }) warnings.Add("Latest validation cannot be presented as current because its repository state is incomplete or not clean.");
        var references = CollectReferences(blockerItems, findingItems, decisionItems, latest);
        return FitBootstrap(new ProjectBootstrap(scope.Public, phaseItem, blockerItems, findingItems, validation, decisionItems, references, warnings.Take(8).Select(warning => Truncate(warning, 256)).ToArray(), ["blocker", "context", "decision", "finding", "phase", "test"]));
    }

    private async Task<IReadOnlyList<ContextEntryRecord>> RelevantContextsAsync(ResolvedScope scope, string category, int maximum, CancellationToken cancellationToken)
    {
        var exact = await StorageAsync(() => _storage.ListContextEntriesAsync(new ContextEntryQuery(scope.Project.Id, scope.Repository.Id, category, scope.Git.Branch, scope.Git.HeadCommitSha, ContextStatus.Active, null, null, false, maximum), cancellationToken));
        var branch = scope.Git.Branch is null ? Array.Empty<ContextEntryRecord>() : await StorageAsync(() => _storage.ListContextEntriesAsync(new ContextEntryQuery(scope.Project.Id, scope.Repository.Id, category, scope.Git.Branch, null, ContextStatus.Active, null, null, false, maximum), cancellationToken));
        var global = await StorageAsync(() => _storage.ListContextEntriesAsync(new ContextEntryQuery(scope.Project.Id, scope.Repository.Id, category, null, null, ContextStatus.Active, null, null, true, maximum), cancellationToken));
        var project = await StorageAsync(() => _storage.ListContextEntriesAsync(new ContextEntryQuery(scope.Project.Id, null, category, null, null, ContextStatus.Active, null, null, false, maximum, true), cancellationToken));
        return exact.Concat(branch).Concat(global).Concat(project).DistinctBy(record => record.Id).Take(maximum).ToArray();
    }

    private async Task<IReadOnlyList<FindingRecord>> RelevantFindingsAsync(ResolvedScope scope, int maximum, CancellationToken cancellationToken)
    {
        var exact = await StorageAsync(() => _storage.ListFindingsAsync(new FindingQuery(scope.Project.Id, scope.Repository.Id, scope.Git.Branch, scope.Git.HeadCommitSha, FindingStatus.Open, false, maximum), cancellationToken));
        var branch = scope.Git.Branch is null ? Array.Empty<FindingRecord>() : await StorageAsync(() => _storage.ListFindingsAsync(new FindingQuery(scope.Project.Id, scope.Repository.Id, scope.Git.Branch, null, FindingStatus.Open, false, maximum), cancellationToken));
        var global = await StorageAsync(() => _storage.ListFindingsAsync(new FindingQuery(scope.Project.Id, scope.Repository.Id, null, null, FindingStatus.Open, true, maximum), cancellationToken));
        var project = await StorageAsync(() => _storage.ListFindingsAsync(new FindingQuery(scope.Project.Id, null, null, null, FindingStatus.Open, false, maximum, true), cancellationToken));
        return exact.Concat(branch).Concat(global).Concat(project)
            .DistinctBy(record => record.Id)
            .OrderBy(record => record.Severity)
            .ThenByDescending(record => record.UpdatedUtc)
            .Take(maximum)
            .ToArray();
    }

    private async Task<IReadOnlyList<DecisionRecord>> RelevantDecisionsAsync(ResolvedScope scope, int maximum, CancellationToken cancellationToken)
    {
        var exact = await StorageAsync(() => _storage.ListDecisionsAsync(new DecisionQuery(scope.Project.Id, scope.Repository.Id, null, null, scope.Git.Branch, scope.Git.HeadCommitSha, DecisionStatus.Accepted, null, false, false, maximum), cancellationToken));
        var branch = scope.Git.Branch is null ? Array.Empty<DecisionRecord>() : await StorageAsync(() => _storage.ListDecisionsAsync(new DecisionQuery(scope.Project.Id, scope.Repository.Id, null, null, scope.Git.Branch, null, DecisionStatus.Accepted, null, false, false, maximum), cancellationToken));
        var global = await StorageAsync(() => _storage.ListDecisionsAsync(new DecisionQuery(scope.Project.Id, scope.Repository.Id, null, null, null, null, DecisionStatus.Accepted, null, false, true, maximum), cancellationToken));
        var project = await StorageAsync(() => _storage.ListDecisionsAsync(new DecisionQuery(scope.Project.Id, null, null, null, null, null, DecisionStatus.Accepted, null, false, false, maximum, true), cancellationToken));
        return exact.Concat(branch).Concat(global).Concat(project).DistinctBy(record => record.Id).Take(maximum).ToArray();
    }

    private async Task<ResolvedScope?> ResolveWriteScopeAsync(Guid projectId, Guid? repositoryId, CancellationToken cancellationToken)
    {
        var project = await RequireProjectAsync(projectId, cancellationToken);
        if (repositoryId is not { } id)
        {
            return null;
        }

        var repository = await StorageAsync(() => _storage.GetRepositoryAsync(id, cancellationToken))
            ?? throw new ApplicationException(ApplicationErrorCode.NotFound, "Repository was not found.");
        if (repository.ProjectId != projectId)
        {
            throw new ApplicationException(ApplicationErrorCode.InvalidInput, "Repository does not belong to the project.");
        }

        return BuildScope(project, repository, await _git.InspectAsync(repository.CanonicalPath, cancellationToken));
    }

    private async Task<ResolvedScope?> TryGetGitStateAsync(Guid projectId, Guid? repositoryId, CancellationToken cancellationToken)
    {
        if (repositoryId is null)
        {
            return null;
        }

        try
        {
            return await ResolveWriteScopeAsync(projectId, repositoryId, cancellationToken);
        }
        catch (ApplicationException exception) when (exception.Code is ApplicationErrorCode.PathRejected or ApplicationErrorCode.RepositoryOutsideApprovedRoot or ApplicationErrorCode.RepositoryNotFound or ApplicationErrorCode.NotGitRepository or ApplicationErrorCode.UnbornRepository or ApplicationErrorCode.GitUnavailable or ApplicationErrorCode.GitTimeout or ApplicationErrorCode.GitCommandFailed)
        {
            return null;
        }
    }

    private async Task<ProjectRecord> RequireProjectAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await StorageAsync(() => _storage.GetProjectAsync(projectId, cancellationToken));
        return project ?? throw new ApplicationException(ApplicationErrorCode.NotFound, "Project was not found.");
    }

    private static RepositoryDraft ToRepositoryDraft(RepositoryRecord repository, GitRepositoryState git) => new(repository.Id, repository.ProjectId, git.CanonicalPath, git.Remote, git.Organization, git.RepositoryName, git.Branch ?? repository.DefaultBranch, git.Branch, git.HeadCommitSha, repository.Status);

    private static ResolvedScope BuildScope(ProjectRecord project, RepositoryRecord repository, GitRepositoryState git)
    {
        var warnings = new List<string>();
        if (!string.Equals(repository.CanonicalPath, git.CanonicalPath, StringComparison.OrdinalIgnoreCase)) warnings.Add("Persisted repository path differs from current Git root.");
        if (!string.Equals(repository.Remote, git.Remote, StringComparison.OrdinalIgnoreCase)) warnings.Add("Persisted repository remote differs from current Git remote.");
        if (repository.LastKnownBranch is not null && repository.LastKnownBranch != git.Branch) warnings.Add("Persisted repository branch is stale.");
        if (repository.LastKnownCommitSha is not null && !string.Equals(repository.LastKnownCommitSha, git.HeadCommitSha, StringComparison.OrdinalIgnoreCase)) warnings.Add("Persisted repository HEAD is stale.");
        var publicValue = new ProjectResolution(
            new ProjectIdentity(project.Id, project.StableProjectId, Truncate(project.Name, 128)),
            new RepositoryIdentity(repository.Id, git.CanonicalPath, git.Remote, Truncate(git.RepositoryName, 128)),
            git with { RepositoryName = Truncate(git.RepositoryName, 128), Organization = TruncateOptional(git.Organization, 128) }, warnings);
        return new ResolvedScope(project, repository, git, warnings, publicValue);
    }

    private async Task<ContextSearchItem> ToSearchItemAsync(ContextEntryRecord record, GitRepositoryState? git, CancellationToken cancellationToken)
    {
        var freshness = await FreshnessAsync(McpRecordKind.ContextEntry, record.Id, 1, record.Branch, record.CommitSha, git, cancellationToken);
        return new ContextSearchItem(record.Id, record.Category, Truncate(record.Title, 256), Truncate(record.Summary, 512), record.AuthoritativeReference, record.Branch, record.CommitSha, record.Status, record.CreatedUtc, freshness);
    }

    private async Task<DecisionListItem> ToDecisionItemAsync(DecisionRecord record, GitRepositoryState? git, CancellationToken cancellationToken)
    {
        var freshness = await FreshnessAsync(McpRecordKind.Decision, record.Id, 1, record.Branch, record.OriginatingCommitSha, git, cancellationToken);
        return new DecisionListItem(record.Id, record.Category, record.Component, Truncate(record.Title, 256), Truncate(record.Decision, 512), record.AuthoritativeReference, record.Status, record.SupersedesDecisionId, record.SupersededByDecisionId, record.Branch, record.OriginatingCommitSha, freshness, record.UpdatedUtc);
    }

    private async Task<BootstrapItem> ToPhaseBootstrapItemAsync(PhaseRecord phase, GitRepositoryState git, CancellationToken cancellationToken)
    {
        var observation = phase.ContextEntryId is { } contextId
            ? await StorageAsync(() => _storage.GetMcpObservationAsync(McpRecordKind.ContextEntry, contextId, cancellationToken: cancellationToken))
            : null;
        return new BootstrapItem(phase.Id, "phase", Truncate(phase.PhaseKey, 128), Truncate(phase.Objective, 256), RepositorySnapshotFreshness.Evaluate(phase.Branch, phase.CommitSha, observation, git));
    }

    private async Task<BootstrapItem> FreshenBootstrapItemAsync(BootstrapItem item, McpRecordKind kind, int revision, string? branch, string? commit, GitRepositoryState git, CancellationToken cancellationToken)
    {
        var freshness = await FreshnessAsync(kind, item.Id, revision, branch, commit, git, cancellationToken);
        return item with { Freshness = freshness };
    }

    private async Task<BootstrapValidation> ToBootstrapValidationAsync(TestRunRecord test, GitRepositoryState git, CancellationToken cancellationToken)
    {
        var freshness = await FreshnessAsync(McpRecordKind.TestRun, test.Id, 1, test.Branch, test.CommitSha, git, cancellationToken);
        return new BootstrapValidation(test.Id, test.Status, test.Passed, test.Failed, test.Skipped, test.Branch, test.CommitSha, freshness, test.CreatedUtc);
    }

    private async Task<RecordFreshness> FreshnessAsync(McpRecordKind kind, Guid id, int revision, string? branch, string? commit, GitRepositoryState? git, CancellationToken cancellationToken)
    {
        var observation = await StorageAsync(() => _storage.GetMcpObservationAsync(kind, id, revision, cancellationToken));
        return RepositorySnapshotFreshness.Evaluate(branch, commit, observation, git);
    }

    private static IReadOnlyList<BootstrapReference> CollectReferences(IReadOnlyList<BootstrapItem> blockers, IReadOnlyList<BootstrapItem> findings, IReadOnlyList<BootstrapItem> decisions, TestRunRecord? test)
    {
        var references = blockers.Concat(findings).Concat(decisions)
            .Where(item => item.Reference is not null)
            .Select(item => new BootstrapReference(item.Kind, item.Id, item.Reference!));
        if (test?.ArtifactReference is { } artifact)
        {
            references = references.Append(new BootstrapReference("test-artifact", test.Id, artifact));
        }

        return references.DistinctBy(reference => reference.Value, StringComparer.Ordinal).Take(ApplicationLimits.BootstrapReferences).ToArray();
    }

    private ProjectBootstrap FitBootstrap(ProjectBootstrap bootstrap)
    {
        if (Fits(bootstrap, _options.BootstrapBytes))
        {
            return bootstrap;
        }

        var compact = bootstrap with
        {
            Resolution = CompactResolution(bootstrap.Resolution, 96),
            CurrentPhase = CompactItem(bootstrap.CurrentPhase, 96),
            Blockers = bootstrap.Blockers.Take(2).Select(item => CompactItem(item, 96)!).ToArray(),
            Findings = bootstrap.Findings.Take(2).Select(item => CompactItem(item, 96)!).ToArray(),
            Decisions = bootstrap.Decisions.Take(2).Select(item => CompactItem(item, 96)!).ToArray(),
            LatestValidation = CompactValidation(bootstrap.LatestValidation),
            References = bootstrap.References.Take(4).ToArray(),
            Warnings = bootstrap.Warnings.Take(3).Select(warning => Truncate(warning, 96)).ToArray()
        };
        if (Fits(compact, _options.BootstrapBytes))
        {
            return compact;
        }

        var minimal = compact with
        {
            Resolution = CompactResolution(compact.Resolution, 48, omitBranch: true),
            CurrentPhase = null,
            Blockers = [],
            Findings = [],
            Decisions = [],
            References = [],
            Warnings = ["Bootstrap detail and current branch were omitted to meet the configured response limit."]
        };
        if (Fits(minimal, _options.BootstrapBytes))
        {
            return minimal;
        }

        throw new ApplicationException(ApplicationErrorCode.ContentTooLarge, "Bootstrap identity exceeds the configured response limit.");
    }

    private ContextSearchResult FitSearch(ContextSearchResult result)
    {
        if (Fits(result, ApplicationLimits.SearchBytes)) return result;
        var compact = result with
        {
            Entries = result.Entries.Take(10).Select(item => item with { Category = Truncate(item.Category, 64), Title = Truncate(item.Title, 96), Summary = Truncate(item.Summary, 128), AuthoritativeReference = null, Branch = null }).ToArray(),
            Warnings = ["Results were compacted to meet the response limit."]
        };
        if (Fits(compact, ApplicationLimits.SearchBytes)) return compact;
        var minimal = compact with { Entries = compact.Entries.Take(3).Select(item => item with { Title = Truncate(item.Title, 48), Summary = Truncate(item.Summary, 48) }).ToArray() };
        if (Fits(minimal, ApplicationLimits.SearchBytes)) return minimal;
        throw new ApplicationException(ApplicationErrorCode.ContentTooLarge, "Search result exceeds its response limit.");
    }

    private DecisionListResult FitDecisions(DecisionListResult result)
    {
        if (Fits(result, ApplicationLimits.SearchBytes)) return result;
        var compact = result with
        {
            Decisions = result.Decisions.Take(10).Select(item => item with { Category = Truncate(item.Category, 64), Component = Truncate(item.Component, 64), Title = Truncate(item.Title, 96), Decision = Truncate(item.Decision, 128), AuthoritativeReference = null, Branch = null }).ToArray(),
            Warnings = ["Results were compacted to meet the response limit."]
        };
        if (Fits(compact, ApplicationLimits.SearchBytes)) return compact;
        var minimal = compact with { Decisions = compact.Decisions.Take(3).Select(item => item with { Title = Truncate(item.Title, 48), Decision = Truncate(item.Decision, 48) }).ToArray() };
        if (Fits(minimal, ApplicationLimits.SearchBytes)) return minimal;
        throw new ApplicationException(ApplicationErrorCode.ContentTooLarge, "Decision result exceeds its response limit.");
    }

    private static ProjectResolution CompactResolution(ProjectResolution resolution, int maximum, bool omitBranch = false) => resolution with
    {
        Project = resolution.Project with { StableProjectId = Truncate(resolution.Project.StableProjectId, maximum), Name = Truncate(resolution.Project.Name, maximum) },
        Repository = resolution.Repository with { RepositoryName = Truncate(resolution.Repository.RepositoryName, maximum) },
        Git = resolution.Git with { Branch = omitBranch ? null : resolution.Git.Branch, Organization = TruncateOptional(resolution.Git.Organization, maximum), RepositoryName = Truncate(resolution.Git.RepositoryName, maximum) },
        StaleWarnings = resolution.StaleWarnings.Take(3).Select(warning => Truncate(warning, maximum)).ToArray()
    };

    private static BootstrapItem? CompactItem(BootstrapItem? item, int maximum) => item is null ? null : item with { Title = Truncate(item.Title, maximum), Summary = Truncate(item.Summary, maximum), Reference = null };
    private static BootstrapValidation? CompactValidation(BootstrapValidation? validation) => validation is null ? null : validation with { Branch = null };
    private static string[] GitWarnings(GitRepositoryState git) => git.WorkingTree == WorkingTreeState.Clean ? [] : [git.WorkingTreeWarning ?? "Working-tree state is not clean; HEAD-based records are not current validation."];
    private static string RequiredText(string value, string field) => string.IsNullOrWhiteSpace(value) || value.Length > StorageLimits.HandoffField ? throw new ApplicationException(value?.Length > StorageLimits.HandoffField ? ApplicationErrorCode.ContentTooLarge : ApplicationErrorCode.InvalidInput, $"{field} is invalid.") : value;
    private static string MergeSection(string supplied, IEnumerable<string> derived) => RequiredText(supplied, "handoff section") + string.Concat(derived.Take(4).Select(value => $"\n- {Truncate(value, 256)}"));
    private static void ValidateHandoffBytes(HandoffDraft handoff)
    {
        if (!Fits(handoff, StorageLimits.HandoffAggregate))
        {
            throw new ApplicationException(ApplicationErrorCode.ContentTooLarge, "Handoff exceeds its serialized limit.");
        }
    }
    private static bool Fits<T>(T value, int maximumBytes) => JsonSerializer.SerializeToUtf8Bytes(value).Length <= maximumBytes;

    private static string ComputeContextHash(ContextRecordRequest request, string? reference, string? branch, string? commit)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireBounded(request.Category, "category", StorageLimits.ShortText, required: true);
        RequireBounded(request.Title, "title", StorageLimits.Title, required: true);
        RequireBounded(request.Summary, "summary", StorageLimits.Summary, required: true);
        RequireBounded(request.Content, "content", StorageLimits.Content, required: false);
        RequireBounded(branch, "branch", StorageLimits.ShortText, required: false);
        RequireBounded(commit, "commit", 64, required: false);
        if (!Enum.IsDefined(request.Status))
        {
            throw new ApplicationException(ApplicationErrorCode.InvalidInput, "Context status is invalid.");
        }

        if (request.ContentHash is { Length: > 64 })
        {
            throw new ApplicationException(ApplicationErrorCode.InvalidInput, "Content hash is invalid.");
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            request.Category,
            request.Title,
            request.Summary,
            request.Content,
            AuthoritativeReference = reference,
            Branch = branch,
            CommitSha = commit,
            Status = (int)request.Status,
            Tier = request.Tier,
            request.Objective,
            request.PhaseId
        });
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static void RequireBounded(string? value, string field, int maximum, bool required)
    {
        var tooLong = value?.Length > maximum;
        if ((required && string.IsNullOrWhiteSpace(value)) || tooLong)
        {
            throw new ApplicationException(tooLong ? ApplicationErrorCode.ContentTooLarge : ApplicationErrorCode.InvalidInput, $"{field} is invalid.");
        }
    }

    private async Task<T> ExecuteAsync<T>(string operation, Func<Task<T>> action)
    {
        try
        {
            var result = await action();
            LogOperation(operation, "success");
            return result;
        }
        catch (ApplicationException exception)
        {
            LogOperation(operation, exception.Code.ToString());
            throw;
        }
        catch (OperationCanceledException)
        {
            LogOperation(operation, "cancelled");
            throw;
        }
    }

    private void LogOperation(string operation, string outcome)
    {
        try
        {
            _operationLog?.Invoke(operation, outcome);
        }
        catch
        {
        }
    }
    private static string Truncate(string value, int maximum) => value.Length <= maximum ? value : $"{value[..Math.Max(0, maximum - 1)]}…";
    private static string? TruncateOptional(string? value, int maximum) => value is null ? null : Truncate(value, maximum);

    private static async Task<T> StorageAsync<T>(Func<Task<T>> action)
    {
        try { return await action(); }
        catch (StorageException exception) { throw MapStorage(exception); }
    }

    private static ApplicationException MapStorage(StorageException exception) => exception.Code switch
    {
        StorageErrorCode.LimitExceeded => new ApplicationException(ApplicationErrorCode.ContentTooLarge, "Record exceeds its limit."),
        StorageErrorCode.SecretRejected => new ApplicationException(ApplicationErrorCode.SecretRejected, "Record contains secret-shaped content."),
        StorageErrorCode.Duplicate => new ApplicationException(ApplicationErrorCode.DuplicateRecord, "A duplicate record already exists."),
        StorageErrorCode.NotFound => new ApplicationException(ApplicationErrorCode.NotFound, "Referenced record was not found."),
        StorageErrorCode.Conflict or StorageErrorCode.CrossProjectReference => new ApplicationException(ApplicationErrorCode.Conflict, "Record operation conflicts with persisted state."),
        StorageErrorCode.DatabaseUnavailable or StorageErrorCode.DatabaseBusy or StorageErrorCode.DatabaseCorrupt or StorageErrorCode.UnsupportedSchema => new ApplicationException(ApplicationErrorCode.StorageUnavailable, "Storage is unavailable."),
        _ => new ApplicationException(ApplicationErrorCode.InvalidInput, "Record input is invalid.")
    };

    private sealed record ResolvedScope(ProjectRecord Project, RepositoryRecord Repository, GitRepositoryState Git, IReadOnlyList<string> Warnings, ProjectResolution Public);
}
