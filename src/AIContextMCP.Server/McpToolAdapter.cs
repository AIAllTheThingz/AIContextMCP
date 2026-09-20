using System.Text.Json;
using AIContextMCP.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIContextMCP.Server;

internal sealed record McpInvocationResult(string Json, bool IsError);

internal sealed class McpToolAdapter
{
    private const int ProtocolResultReserveBytes = 4096;
    private readonly IAIContextApplication _application;
    private readonly IAIContextStorage _storage;
    private readonly ServerOptions _options;
    private readonly SnapshotTokens _tokens;
    private readonly ILogger<McpToolAdapter> _logger;

    public McpToolAdapter(
        IAIContextApplication application,
        IAIContextStorage storage,
        IOptions<ServerOptions> options,
        SnapshotTokens tokens,
        ILogger<McpToolAdapter> logger)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public IReadOnlyList<McpToolDescriptor> Tools => McpToolCatalog.All;

    public async Task<McpInvocationResult> InvokeAsync(string? name, IReadOnlyDictionary<string, JsonElement>? arguments, CancellationToken cancellationToken)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        var operation = McpToolCatalog.Contains(name) ? name! : "unknown";
        try
        {
            if (!McpToolCatalog.Contains(name)) throw new McpInputException("InvalidInput", "Tool name is not supported.");
            var json = McpJson.ArgumentsJson(arguments);
            using var payloadDocument = JsonDocument.Parse(json);
            var payload = payloadDocument.RootElement.Clone();
            var result = name switch
            {
                "project.bootstrap" => await BootstrapAsync(McpJson.Deserialize<ProjectRef>(json), payload, correlationId, cancellationToken),
                "context.search" => await SearchContextAsync(McpJson.Deserialize<ContextSearch>(json), correlationId, cancellationToken),
                "decision.list" => await ListDecisionsAsync(McpJson.Deserialize<DecisionList>(json), correlationId, cancellationToken),
                "context.record" => await RecordContextAsync(McpJson.Deserialize<ContextRecord>(json), payload, correlationId, cancellationToken),
                "decision.record" => await RecordDecisionAsync(McpJson.Deserialize<DecisionRecord>(json), payload, correlationId, cancellationToken),
                "test.record" => await RecordTestAsync(McpJson.Deserialize<TestRecord>(json), payload, correlationId, cancellationToken),
                "finding.record" => await RecordFindingAsync(McpJson.Deserialize<FindingRecord>(json), payload, correlationId, cancellationToken),
                "handoff.create" => await CreateHandoffAsync(McpJson.Deserialize<HandoffCreate>(json), payload, correlationId, cancellationToken),
                _ => throw new McpInputException("InvalidInput", "Tool name is not supported.")
            };
            _logger.LogInformation("mcp.tool {Operation} {Outcome} {CorrelationId}", operation, "success", correlationId);
            return result;
        }
        catch (Exception exception) when (exception is McpInputException or AIContextMCP.Core.ApplicationException or StorageException or OperationCanceledException)
        {
            var failure = Failure(exception, correlationId);
            _logger.LogInformation("mcp.tool {Operation} {Outcome} {Code} {CorrelationId}", operation, "failure", failure.Code, correlationId);
            return failure.Result;
        }
        catch
        {
            var failure = Failure(new McpInputException("InternalError", "The operation could not be completed."), correlationId);
            _logger.LogInformation("mcp.tool {Operation} {Outcome} {Code} {CorrelationId}", operation, "failure", failure.Code, correlationId);
            return failure.Result;
        }
    }

    private async Task<McpInvocationResult> BootstrapAsync(ProjectRef request, JsonElement payload, string correlationId, CancellationToken cancellationToken)
    {
        Validate(request);
        if (!request.Register)
        {
            var bootstrap = await LoadBootstrapAsync(request, cancellationToken);
            return Success(await ToBootstrapAsync(bootstrap, request.IncludeWorkingTree, _storage, cancellationToken), correlationId, McpJson.BootstrapResponseBytes);
        }

        McpJson.RequestId(request.RequestId);
        McpJson.Required(request.RepositoryPath, McpJson.Identifier, "repository path");
        var observed = await _application.ObserveRepositoryAsync(request.RepositoryPath!, cancellationToken);
        var receipt = await _storage.ExecuteMutationAsync(
            new MutationRequest(observed.CanonicalPath, "project.bootstrap", request.RequestId!, McpJson.CanonicalPayloadHash(payload)),
            async (storage, token) =>
            {
                var bootstrap = await _application.BootstrapAsync(new ProjectBootstrapRequest(request.RepositoryPath!, Register: true), token);
                EnsureBootstrapMatches(request, bootstrap);
                return SuccessJson(await ToBootstrapAsync(bootstrap, request.IncludeWorkingTree, storage, token), correlationId, McpJson.BootstrapResponseBytes);
            },
            cancellationToken);
        return new McpInvocationResult(receipt.ReceiptJson, false);
    }

    private async Task<McpInvocationResult> SearchContextAsync(ContextSearch request, string correlationId, CancellationToken cancellationToken)
    {
        Validate(request);
        var scope = await RequireScopeAsync(request.ProjectId, request.RepositoryId, cancellationToken);
        if (request.FindingId is not null)
        {
            var finding = await _storage.GetFindingAsync(McpJson.RequiredId(request.FindingId, "finding id"), cancellationToken)
                ?? throw new McpInputException("NotFound", "Finding was not found.");
            if (finding.ProjectId != scope.Project.Id || scope.Repository is not null && finding.RepositoryId != scope.Repository.Id)
            {
                throw new McpInputException("CrossProjectReference", "Finding is outside the requested scope.");
            }
            var findingGit = await ObserveForReadAsync(scope, cancellationToken);
            var observation = await _storage.GetMcpObservationAsync(McpRecordKind.Finding, finding.Id, finding.Revision, cancellationToken);
            var freshness = Freshness(finding.Branch, finding.CommitSha, observation, findingGit);
            var freshnessWarnings = FreshnessWarnings(findingGit);
            SearchResult BuildFindingResult(int descriptionLimit)
            {
                var titleLimit = Math.Max(1, 256 * descriptionLimit / 4096);
                var optionalLimit = 2048 * descriptionLimit / 4096;
                var provenanceLimit = Math.Max(1, McpJson.DefaultString * descriptionLimit / 4096);
                var truncated = finding.Title.Length > titleLimit
                    || finding.Description.Length > descriptionLimit
                    || finding.Remediation?.Length > optionalLimit
                    || finding.ResolutionEvidence?.Length > optionalLimit
                    || observation?.SourceKind.Length > provenanceLimit;
                var detail = new McpFindingItem(finding.Id.ToString("D"), finding.Revision, Bound(finding.Title, titleLimit)!, finding.Severity, finding.Status,
                    Bound(finding.Description, Math.Max(1, descriptionLimit))!, Bound(finding.Remediation, optionalLimit), Bound(finding.ResolutionEvidence, optionalLimit),
                    finding.Branch, finding.CommitSha, freshness, Bound(observation?.SourceKind, provenanceLimit));
                var warnings = truncated
                    ? freshnessWarnings.Append("Finding detail fields were truncated for the response budget.").ToArray()
                    : freshnessWarnings;
                return new SearchResult([], null, truncated, warnings, [detail]);
            }

            var low = 0;
            var high = 4096;
            var findingResult = BuildFindingResult(0);
            while (low <= high)
            {
                var candidateLimit = low + (high - low) / 2;
                var candidate = BuildFindingResult(candidateLimit);
                if (ResponseFits(candidate, correlationId, request.MaxBytes))
                {
                    findingResult = candidate;
                    low = candidateLimit + 1;
                }
                else
                {
                    high = candidateLimit - 1;
                }
            }

            return Success(findingResult, correlationId, request.MaxBytes);
        }
        var branch = request.Commit is null ? request.Branch : null;
        var filterHash = FilterHash(new
        {
            scope.Project.Id,
            RepositoryId = scope.Repository?.Id,
            request.Category,
            Branch = branch,
            request.Commit,
            request.Status,
            request.RecencySinceUtc,
            request.Query,
            request.FindingId
        });
        var cursor = request.Cursor is null ? ((DateTimeOffset? ObservedUtc, Guid? RecordId))(null, null) : ReadCursor(request.Cursor, scope, filterHash);
        var rows = await _storage.ListMcpContextEntriesAsync(new McpContextPageQuery(
            scope.Project.Id,
            scope.Repository?.Id,
            request.Category,
            branch,
            request.Commit,
            request.Status,
            request.RecencySinceUtc,
            request.Query,
            cursor.ObservedUtc,
            cursor.RecordId,
            request.MaxResults + 1), cancellationToken);
        var page = rows.Take(request.MaxResults).ToArray();
        var nextCursor = rows.Count > request.MaxResults && page.Length > 0
            ? _tokens.CreateCursor(scope.Project.Id, scope.Repository?.Id, filterHash, page[^1].ObservedUtc, page[^1].Entry.Id)
            : null;
        var git = await ObserveForReadAsync(scope, cancellationToken);
        var entries = await Task.WhenAll(page.Select(row => ToContextItemAsync(row, git, cancellationToken)));
        var result = new SearchResult(entries, nextCursor, nextCursor is not null, FreshnessWarnings(git), []);
        return Success(result, correlationId, request.MaxBytes);
    }

    private async Task<McpInvocationResult> ListDecisionsAsync(DecisionList request, string correlationId, CancellationToken cancellationToken)
    {
        Validate(request);
        var scope = await RequireScopeAsync(request.ProjectId, request.RepositoryId, cancellationToken);
        var filterHash = FilterHash(new
        {
            scope.Project.Id,
            RepositoryId = scope.Repository?.Id,
            Statuses = request.Statuses?.OrderBy(status => status).ToArray(),
            request.IncludeSuperseded
        });
        var cursor = request.Cursor is null ? ((DateTimeOffset? ObservedUtc, Guid? RecordId))(null, null) : ReadCursor(request.Cursor, scope, filterHash);
        var rows = await _storage.ListMcpDecisionsAsync(new McpDecisionPageQuery(
            scope.Project.Id,
            scope.Repository?.Id,
            request.Statuses,
            request.IncludeSuperseded,
            cursor.ObservedUtc,
            cursor.RecordId,
            request.MaxResults + 1), cancellationToken);
        var page = rows.Take(request.MaxResults).ToArray();
        var nextCursor = rows.Count > request.MaxResults && page.Length > 0
            ? _tokens.CreateCursor(scope.Project.Id, scope.Repository?.Id, filterHash, page[^1].ObservedUtc, page[^1].Decision.Id)
            : null;
        var git = await ObserveForReadAsync(scope, cancellationToken);
        var decisions = await Task.WhenAll(page.Select(row => ToDecisionItemAsync(row, git, cancellationToken)));
        var result = new DecisionListResult(decisions, nextCursor, nextCursor is not null);
        return Success(result, correlationId, McpJson.ResponseBytes);
    }

    private async Task<ProjectBootstrap> LoadBootstrapAsync(ProjectRef request, CancellationToken cancellationToken)
    {
        if (request.RepositoryPath is not null)
        {
            var bootstrap = await _application.BootstrapAsync(new ProjectBootstrapRequest(request.RepositoryPath), cancellationToken);
            EnsureBootstrapMatches(request, bootstrap);
            return bootstrap;
        }

        var normalizedRemote = request.Remote is null ? null : GitRemoteIdentity.Normalize(request.Remote)
            ?? throw new McpInputException("InvalidInput", "Remote is invalid.");
        IReadOnlyList<RepositoryRecord> repositories;
        if (request.ProjectId is null)
        {
            repositories = await _storage.ListRepositoriesByRemoteAsync(normalizedRemote!, StorageLimits.CollectionCount, cancellationToken);
        }
        else
        {
            var projectId = McpJson.RequiredId(request.ProjectId, "project id");
            repositories = await _storage.ListRepositoriesByProjectAsync(projectId, StorageLimits.CollectionCount, cancellationToken);
            if (normalizedRemote is not null) repositories = repositories.Where(repository => string.Equals(repository.Remote, normalizedRemote, StringComparison.OrdinalIgnoreCase)).ToArray();
        }
        if (repositories.Count == 0) throw new McpInputException("NotFound", "No registered repository matches the requested project.");
        if (repositories.Count != 1) throw new McpInputException("Conflict", "Project bootstrap requires one unambiguous repository.");
        return await _application.BootstrapAsync(new ProjectBootstrapRequest(repositories[0].CanonicalPath), cancellationToken);
    }

    private void EnsureBootstrapMatches(ProjectRef request, ProjectBootstrap bootstrap)
    {
        if (request.ProjectId is not null && McpJson.RequiredId(request.ProjectId, "project id") != bootstrap.Resolution.Project.Id)
        {
            throw new McpInputException("Conflict", "Repository path does not match the requested project.");
        }

        var normalizedRemote = request.Remote is null ? null : GitRemoteIdentity.Normalize(request.Remote)
            ?? throw new McpInputException("InvalidInput", "Remote is invalid.");
        if (normalizedRemote is not null && !string.Equals(normalizedRemote, bootstrap.Resolution.Repository.Remote, StringComparison.OrdinalIgnoreCase))
        {
            throw new McpInputException("Conflict", "Repository path does not match the requested remote.");
        }
    }

    private async Task<Bootstrap> ToBootstrapAsync(ProjectBootstrap bootstrap, bool includeWorkingTree, IAIContextStorage storage, CancellationToken cancellationToken)
    {
        var handoff = await storage.GetLatestHandoffAsync(bootstrap.Resolution.Project.Id, bootstrap.Resolution.Repository.Id, cancellationToken);
        var handoffObservation = handoff is null
            ? null
            : await storage.GetMcpObservationAsync(McpRecordKind.Handoff, handoff.Id, cancellationToken: cancellationToken);
        var phase = bootstrap.CurrentPhase;
        var blockers = bootstrap.Blockers;
        var findings = bootstrap.Findings;
        var decisions = bootstrap.Decisions;
        var validation = bootstrap.LatestValidation;
        var warnings = bootstrap.Warnings.ToList();
        if (validation is { Freshness: RecordFreshness.Stale }) warnings.Add("Latest validation is stale for the current repository state.");
        if (validation is { Freshness: RecordFreshness.Unknown }) warnings.Add("Latest validation cannot be presented as current because its observed repository state is incomplete.");
        return new Bootstrap(
            bootstrap.Resolution.Project,
            bootstrap.Resolution.Repository,
            _tokens.CreateSnapshot(bootstrap.Resolution.Project, bootstrap.Resolution.Repository, bootstrap.Resolution.Git, includeWorkingTree),
            phase,
            blockers,
            findings,
            validation,
            decisions,
            bootstrap.References,
            handoff is null ? null : new HandoffSummary(handoff.Id.ToString("D"), handoff.Objective, handoff.RecommendedNextAction, handoff.PhaseId?.ToString("D"), handoff.CreatedUtc, Freshness(handoff.Branch, handoff.CommitSha, handoffObservation, bootstrap.Resolution.Git)),
            warnings.Distinct(StringComparer.Ordinal).Take(8).ToArray(),
            bootstrap.AvailableCategories);
    }

    private async Task<Scope> RequireScopeAsync(string projectId, string? repositoryId, CancellationToken cancellationToken)
    {
        var project = await _storage.GetProjectAsync(McpJson.RequiredId(projectId, "project id"), cancellationToken)
            ?? throw new McpInputException("NotFound", "Project was not found.");
        if (repositoryId is null) return new Scope(project, null);
        var repository = await _storage.GetRepositoryAsync(McpJson.RequiredId(repositoryId, "repository id"), cancellationToken)
            ?? throw new McpInputException("NotFound", "Repository was not found.");
        if (repository.ProjectId != project.Id) throw new McpInputException("CrossProjectReference", "Repository is outside the project scope.");
        return new Scope(project, repository);
    }

    private async Task<Snapshot> CurrentSnapshotAsync(Scope scope, CancellationToken cancellationToken)
    {
        if (scope.Repository is null) throw new McpInputException("StaleState", "Current repository state requires a repository scope.");
        var git = await _application.ObserveRepositoryAsync(scope.Repository.CanonicalPath, cancellationToken);
        if (!string.Equals(git.CanonicalPath, scope.Repository.CanonicalPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new McpInputException("PathRejected", "Repository identity changed during observation.");
        }

        return _tokens.CreateSnapshot(
            new ProjectIdentity(scope.Project.Id, scope.Project.StableProjectId, scope.Project.Name),
            new RepositoryIdentity(scope.Repository.Id, scope.Repository.CanonicalPath, scope.Repository.Remote, scope.Repository.RepositoryName),
            git,
            includeWorkingTree: true);
    }

    private async Task<GitRepositoryState?> ObserveForReadAsync(Scope scope, CancellationToken cancellationToken)
    {
        if (scope.Repository is null) return null;
        try
        {
            var git = await _application.ObserveRepositoryAsync(scope.Repository.CanonicalPath, cancellationToken);
            if (!string.Equals(git.CanonicalPath, scope.Repository.CanonicalPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new McpInputException("PathRejected", "Repository identity changed during observation.");
            }

            return git;
        }
        catch (AIContextMCP.Core.ApplicationException exception) when (exception.Code is ApplicationErrorCode.GitUnavailable or ApplicationErrorCode.GitTimeout or ApplicationErrorCode.GitCommandFailed or ApplicationErrorCode.NotGitRepository)
        {
            return null;
        }
    }

    private async Task<McpContextItem> ToContextItemAsync(McpObservedContextEntry row, GitRepositoryState? git, CancellationToken cancellationToken)
    {
        var metadata = await ReadMetadataAsync(_storage, McpRecordKind.ContextEntry, row.Entry.Id, cancellationToken);
        return new McpContextItem(
            row.Entry.Id.ToString("D"), row.Entry.Category, row.Entry.Title, row.Entry.Summary,
            row.Entry.Branch, row.Entry.CommitSha, row.Entry.Status, row.Entry.Tier, row.Entry.Objective,
            row.Entry.PhaseId?.ToString("D"), row.ObservedUtc, Freshness(row.Entry.Branch, row.Entry.CommitSha, metadata.Observation, git), metadata.Source, metadata.References);
    }

    private async Task<McpDecisionItem> ToDecisionItemAsync(McpObservedDecision row, GitRepositoryState? git, CancellationToken cancellationToken)
    {
        var metadata = await ReadMetadataAsync(_storage, McpRecordKind.Decision, row.Decision.Id, cancellationToken);
        return new McpDecisionItem(
            row.Decision.Id.ToString("D"), row.Decision.Title, row.Decision.Decision, row.Decision.Status,
            row.Decision.Version, row.Decision.SupersedesDecisionId?.ToString("D"), row.Decision.SupersededByDecisionId?.ToString("D"),
            row.Decision.ResolutionEvidence, row.ObservedUtc, Freshness(row.Decision.Branch, row.Decision.OriginatingCommitSha, metadata.Observation, git), metadata.Source, metadata.References);
    }

    private static async Task<(McpObservationRecord? Observation, Source? Source, IReadOnlyList<Reference> References)> ReadMetadataAsync(IAIContextStorage storage, McpRecordKind kind, Guid id, CancellationToken cancellationToken, int revision = 1)
    {
        var observation = await storage.GetMcpObservationAsync(kind, id, revision, cancellationToken);
        var references = await storage.ListMcpReferencesAsync(kind, id, revision, cancellationToken);
        return (
            observation,
            observation is null ? null : new Source(observation.SourceKind, observation.SourceObservedUtc, ToReference(observation.SourceReference)),
            references.Where(reference => reference.Role != McpReferenceRole.Source).Select(reference => ToReference(reference.Reference)!).ToArray());
    }

    private static Reference? ToReference(McpSourceReference? reference) => reference is null
        ? null
        : new Reference(reference.ProjectId.ToString("D"), reference.Kind, reference.RepositoryId?.ToString("D"), reference.Id?.ToString("D"), reference.RelativePath, reference.Uri, reference.Hash);

    private static RecordFreshness Freshness(string? branch, string? commit, McpObservationRecord? observation, GitRepositoryState? git) =>
        RepositorySnapshotFreshness.Evaluate(branch, commit, observation, git);

    private static IReadOnlyList<string> FreshnessWarnings(GitRepositoryState? git) => git is null
        ? ["Repository state is unavailable; freshness is unknown."]
        : git.WorkingTree == WorkingTreeState.Clean ? [] : ["Working-tree state is not clean; freshness is unknown."];

    private (DateTimeOffset? ObservedUtc, Guid? RecordId) ReadCursor(string cursor, Scope scope, string filterHash)
    {
        var value = _tokens.ReadCursor(cursor, scope.Project.Id, scope.Repository?.Id, filterHash);
        return (value.ObservedUtc, value.RecordId);
    }

    private static string FilterHash<T>(T value) => McpJson.CanonicalPayloadHash(McpJson.ToElement(value));

    private static McpInvocationResult Success<T>(T value, string correlationId, int limit) => new(SuccessJson(value, correlationId, limit), false);

    private static string SuccessJson<T>(T value, string correlationId, int limit)
    {
        var json = McpJson.Serialize(new McpEnvelope(true, McpJson.ToElement(value), null, 1, correlationId));
        if (!ResponseFits(json, limit)) throw new McpInputException("LimitExceeded", "Response exceeds its limit.");
        return json;
    }

    private static bool ResponseFits<T>(T value, string correlationId, int limit) =>
        ResponseFits(McpJson.Serialize(new McpEnvelope(true, McpJson.ToElement(value), null, 1, correlationId)), limit);

    private static bool ResponseFits(string json, int limit)
    {
        // MCP clients receive this envelope in both structuredContent and an escaped text block.
        var textBytes = System.Text.Encoding.UTF8.GetByteCount(McpJson.Serialize(json));
        return System.Text.Encoding.UTF8.GetByteCount(json) + textBytes <= limit - ProtocolResultReserveBytes;
    }

    private static (McpInvocationResult Result, string Code) Failure(Exception exception, string correlationId)
    {
        var (code, message, retryable) = exception switch
        {
            McpInputException input => (input.Code, input.Message, false),
            AIContextMCP.Core.ApplicationException application => Map(application),
            StorageException storage => Map(storage),
            OperationCanceledException => ("InternalError", "The operation was cancelled.", true),
            _ => ("InternalError", "The operation could not be completed.", true)
        };
        var json = McpJson.Serialize(new McpEnvelope(false, null, new McpError(code, message, correlationId, retryable), 1, correlationId));
        return (new McpInvocationResult(json, true), code);
    }

    private static (string Code, string Message, bool Retryable) Map(AIContextMCP.Core.ApplicationException exception) => exception.Code switch
    {
        ApplicationErrorCode.PathRejected or ApplicationErrorCode.RepositoryOutsideApprovedRoot or ApplicationErrorCode.RepositoryNotFound => ("PathRejected", "Repository path was rejected.", false),
        ApplicationErrorCode.UnbornRepository => ("UnbornRepository", "The repository has no commit yet.", false),
        ApplicationErrorCode.GitUnavailable or ApplicationErrorCode.GitTimeout or ApplicationErrorCode.GitCommandFailed => ("GitUnavailable", "Repository observation is unavailable.", true),
        ApplicationErrorCode.StaleState => ("StaleState", "Repository state is stale.", false),
        ApplicationErrorCode.ProjectIdentityConflict or ApplicationErrorCode.DuplicateRecord or ApplicationErrorCode.Conflict or ApplicationErrorCode.InvalidTransition => ("Conflict", "The requested state conflicts with stored state.", false),
        ApplicationErrorCode.SecretRejected => ("SecretRejected", "Secret-shaped content was rejected.", false),
        ApplicationErrorCode.ContentTooLarge => ("LimitExceeded", "A supplied value exceeds its limit.", false),
        ApplicationErrorCode.NotFound => ("NotFound", "The requested record was not found.", false),
        ApplicationErrorCode.StorageUnavailable => ("DatabaseUnavailable", "Storage is unavailable.", true),
        _ => ("InvalidInput", "The request is invalid.", false)
    };

    private static (string Code, string Message, bool Retryable) Map(StorageException exception) => exception.Code switch
    {
        StorageErrorCode.LimitExceeded => ("LimitExceeded", "A supplied value exceeds its limit.", false),
        StorageErrorCode.SecretRejected => ("SecretRejected", "Secret-shaped content was rejected.", false),
        StorageErrorCode.CrossProjectReference => ("CrossProjectReference", "Reference is outside the requested scope.", false),
        StorageErrorCode.Conflict or StorageErrorCode.Duplicate => ("Conflict", "The requested state conflicts with stored state.", false),
        StorageErrorCode.NotFound => ("NotFound", "The requested record was not found.", false),
        StorageErrorCode.DatabaseUnavailable or StorageErrorCode.DatabaseBusy => ("DatabaseUnavailable", "Storage is unavailable.", true),
        StorageErrorCode.DatabaseCorrupt or StorageErrorCode.UnsupportedSchema => ("DatabaseCorrupt", "Storage could not be validated.", false),
        _ => ("InvalidInput", "The request is invalid.", false)
    };

    private sealed record Scope(ProjectRecord Project, RepositoryRecord? Repository);
    private sealed record ReferencedRecord(Guid ProjectId, Guid? RepositoryId, string Hash);

    private static void Validate(ProjectRef request)
    {
        ArgumentNullException.ThrowIfNull(request);
        McpJson.Optional(request.ProjectId, McpJson.Identifier, "project id");
        McpJson.Optional(request.RepositoryPath, McpJson.Identifier, "repository path");
        McpJson.Optional(request.Remote, McpJson.Identifier, "remote");
        if (request.ProjectId is null && request.RepositoryPath is null && request.Remote is null) throw new McpInputException("InvalidInput", "Project id, repository path, or remote is required.");
        if (request.Register && request.RepositoryPath is null) throw new McpInputException("InvalidInput", "Registration requires a repository path.");
    }

    private static void Validate(ContextSearch request)
    {
        McpJson.RequiredId(request.ProjectId, "project id");
        McpJson.OptionalId(request.RepositoryId, "repository id");
        McpJson.Optional(request.Category, McpJson.DefaultString, "category");
        McpJson.Optional(request.Branch, McpJson.Identifier, "branch");
        McpJson.Optional(request.Commit, McpJson.Identifier, "commit");
        if (request.RecencySinceUtc is { } since) McpJson.Timestamp(since, "recency time");
        McpJson.OptionalText(request.Query, 256, "query");
        McpJson.Limit(request.MaxResults, "max results");
        McpJson.MaximumBytes(request.MaxBytes);
        McpJson.Optional(request.Cursor, McpJson.Cursor, "cursor");
        McpJson.OptionalId(request.FindingId, "finding id");
        if (request.FindingId is not null && (request.Branch is not null || request.Category is not null || request.Commit is not null || request.Status is not null || request.RecencySinceUtc is not null || request.Query is not null || request.Cursor is not null))
        {
            throw new McpInputException("InvalidInput", "findingId cannot be combined with search filters.");
        }
    }

    private static void Validate(DecisionList request)
    {
        McpJson.RequiredId(request.ProjectId, "project id");
        McpJson.OptionalId(request.RepositoryId, "repository id");
        if (request.Statuses is { Length: > StorageLimits.CollectionCount }) throw new McpInputException("LimitExceeded", "Statuses exceed their limit.");
        McpJson.Limit(request.MaxResults, "max results");
        McpJson.Optional(request.Cursor, McpJson.Cursor, "cursor");
    }

    private Task<McpInvocationResult> RecordContextAsync(ContextRecord request, JsonElement payload, string correlationId, CancellationToken cancellationToken) =>
        Mutations.RecordContextAsync(this, request, payload, correlationId, cancellationToken);

    private Task<McpInvocationResult> RecordDecisionAsync(DecisionRecord request, JsonElement payload, string correlationId, CancellationToken cancellationToken) =>
        Mutations.RecordDecisionAsync(this, request, payload, correlationId, cancellationToken);

    private Task<McpInvocationResult> RecordTestAsync(TestRecord request, JsonElement payload, string correlationId, CancellationToken cancellationToken) =>
        Mutations.RecordTestAsync(this, request, payload, correlationId, cancellationToken);

    private Task<McpInvocationResult> RecordFindingAsync(FindingRecord request, JsonElement payload, string correlationId, CancellationToken cancellationToken) =>
        Mutations.RecordFindingAsync(this, request, payload, correlationId, cancellationToken);

    private Task<McpInvocationResult> CreateHandoffAsync(HandoffCreate request, JsonElement payload, string correlationId, CancellationToken cancellationToken) =>
        Mutations.CreateHandoffAsync(this, request, payload, correlationId, cancellationToken);

    private async Task<McpInvocationResult> ExecuteMutationAsync(
        Scope scope,
        string tool,
        string requestId,
        JsonElement payload,
        Func<IAIContextStorage, CancellationToken, Task<string>> callback,
        CancellationToken cancellationToken)
    {
        var receipt = await _storage.ExecuteMutationAsync(
            new MutationRequest(scope.Project.Id.ToString("N"), tool, requestId, McpJson.CanonicalPayloadHash(payload)),
            callback,
            cancellationToken);
        return new McpInvocationResult(receipt.ReceiptJson, false);
    }

    private void VerifyObservedSnapshot(Scope scope, Snapshot snapshot)
    {
        if (scope.Repository is null)
        {
            _tokens.VerifySnapshotForProject(snapshot, scope.Project.Id);
            return;
        }

        _tokens.VerifySnapshot(snapshot, scope.Project.Id, scope.Repository.Id);
    }

    private async Task RequireCurrentSnapshotAsync(IAIContextStorage storage, Scope scope, Snapshot observedSnapshot, string? expectedToken, CancellationToken cancellationToken)
    {
        var currentScope = scope;
        if (currentScope.Repository is null)
        {
            var repositoryId = _tokens.VerifySnapshotForProject(observedSnapshot, scope.Project.Id)
                ?? throw new McpInputException("StaleState", "Current project state requires a repository observation.");
            var repository = await storage.GetRepositoryAsync(repositoryId, cancellationToken)
                ?? throw new McpInputException("StaleState", "Observed repository is no longer registered.");
            if (repository.ProjectId != scope.Project.Id || !string.Equals(repository.CanonicalPath, observedSnapshot.CanonicalRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new McpInputException("StaleState", "Observed repository is outside the project scope.");
            }

            currentScope = scope with { Repository = repository };
        }

        var current = await CurrentSnapshotAsync(currentScope, cancellationToken);
        _tokens.VerifyExpectedSnapshot(expectedToken, scope.Project.Id, currentScope.Repository!.Id, current);
    }

    private async Task<McpSourceReference?> SourceReferenceAsync(IAIContextStorage storage, Source source, Scope scope, CancellationToken cancellationToken)
    {
        McpJson.SourceShape(source);
        return source.Reference is null ? null : await StoredReferenceAsync(storage, source.Reference, scope, cancellationToken);
    }

    private async Task<McpSourceReference> StoredReferenceAsync(
        IAIContextStorage storage,
        Reference reference,
        Scope scope,
        CancellationToken cancellationToken,
        McpRecordKind? expectedRecordKind = null)
    {
        McpJson.ReferenceShape(reference);
        var requestedProjectId = McpJson.RequiredId(reference.ProjectId, "reference project id");
        var requestedRepositoryId = McpJson.OptionalId(reference.RepositoryId, "reference repository id");
        if (requestedProjectId != scope.Project.Id
            || (scope.Repository is null ? requestedRepositoryId is not null : requestedRepositoryId is not null && requestedRepositoryId != scope.Repository.Id))
        {
            throw new McpInputException("CrossProjectReference", "Reference is outside the requested scope.");
        }

        switch (reference.Kind)
        {
            case "artifact":
            {
                var artifact = await storage.GetArtifactAsync(McpJson.RequiredId(reference.Id, "artifact id"), cancellationToken)
                    ?? throw new McpInputException("NotFound", "Artifact was not found.");
                EnsureReferenceIdentity(scope, requestedProjectId, requestedRepositoryId, artifact.ProjectId, artifact.RepositoryId);
                VerifyReferenceHash(reference.Hash, artifact.ContentHash);
                return new McpSourceReference(artifact.ProjectId, artifact.RepositoryId, "artifact", artifact.Id, artifact.Reference, null, artifact.ContentHash);
            }
            case "record":
            {
                var record = await ResolveRecordReferenceAsync(storage, scope, McpJson.RequiredId(reference.Id, "record id"), expectedRecordKind, cancellationToken);
                EnsureReferenceIdentity(scope, requestedProjectId, requestedRepositoryId, record.ProjectId, record.RepositoryId);
                VerifyReferenceHash(reference.Hash, record.Hash);
                return new McpSourceReference(record.ProjectId, record.RepositoryId, "record", McpJson.RequiredId(reference.Id, "record id"), null, null, record.Hash);
            }
            case "repository-document":
            {
                if (scope.Repository is null) throw new McpInputException("CrossProjectReference", "Repository documents require a repository scope.");
                EnsureSafeRelative(reference.RelativePath, "repository document");
                var document = RepositoryDocumentBoundary.Validate(scope.Repository.CanonicalPath, reference.RelativePath!, StorageLimits.Content);
                VerifyReferenceHash(reference.Hash, document.ContentHash);
                return new McpSourceReference(scope.Project.Id, scope.Repository.Id, "repository-document", null, document.Reference, null, document.ContentHash);
            }
            case "external":
                if (reference.Hash is not null) throw new McpInputException("InvalidInput", "External references cannot claim an unverified hash.");
                EnsureSafeExternalUri(reference.Uri);
                return new McpSourceReference(requestedProjectId, requestedRepositoryId, "external", null, null, reference.Uri, null);
            default:
                throw new McpInputException("InvalidInput", "Reference kind is invalid.");
        }
    }

    private async Task<ReferencedRecord> ResolveRecordReferenceAsync(IAIContextStorage storage, Scope scope, Guid id, McpRecordKind? expectedKind, CancellationToken cancellationToken)
    {
        if (expectedKind is null or McpRecordKind.ContextEntry)
        {
            var context = await storage.GetContextEntryAsync(id, cancellationToken);
            if (context is not null) return Referenced(scope, context.ProjectId, context.RepositoryId, context);
            if (expectedKind is McpRecordKind.ContextEntry) throw new McpInputException("NotFound", "Referenced context entry was not found.");
        }

        if (expectedKind is null or McpRecordKind.Decision)
        {
            var decision = await storage.GetDecisionAsync(id, cancellationToken);
            if (decision is not null) return Referenced(scope, decision.ProjectId, decision.RepositoryId, decision);
            if (expectedKind is McpRecordKind.Decision) throw new McpInputException("NotFound", "Referenced decision was not found.");
        }

        if (expectedKind is null or McpRecordKind.TestRun)
        {
            var test = await storage.GetTestRunAsync(id, cancellationToken);
            if (test is not null) return Referenced(scope, test.ProjectId, test.RepositoryId, test);
            if (expectedKind is McpRecordKind.TestRun) throw new McpInputException("NotFound", "Referenced test run was not found.");
        }

        if (expectedKind is null or McpRecordKind.Finding)
        {
            var finding = await storage.GetFindingAsync(id, cancellationToken);
            if (finding is not null) return Referenced(scope, finding.ProjectId, finding.RepositoryId, finding);
            if (expectedKind is McpRecordKind.Finding) throw new McpInputException("NotFound", "Referenced finding was not found.");
        }

        if (expectedKind is null or McpRecordKind.Handoff)
        {
            var handoff = await storage.GetHandoffAsync(id, cancellationToken);
            if (handoff is not null) return Referenced(scope, handoff.ProjectId, handoff.RepositoryId, handoff);
            if (expectedKind is McpRecordKind.Handoff) throw new McpInputException("NotFound", "Referenced handoff was not found.");
        }

        var phase = await storage.GetPhaseAsync(id, cancellationToken);
        if (phase is not null) return Referenced(scope, phase.ProjectId, phase.RepositoryId, phase);
        throw new McpInputException("NotFound", "Referenced record was not found.");
    }

    private static ReferencedRecord Referenced<T>(Scope scope, Guid projectId, Guid? repositoryId, T record)
    {
        EnsureReferencedScope(scope, projectId, repositoryId);
        return new ReferencedRecord(projectId, repositoryId, McpJson.CanonicalPayloadHash(McpJson.ToElement(record)));
    }

    private static void EnsureReferenceIdentity(Scope scope, Guid requestedProjectId, Guid? requestedRepositoryId, Guid actualProjectId, Guid? actualRepositoryId)
    {
        EnsureReferencedScope(scope, actualProjectId, actualRepositoryId);
        if (requestedProjectId != actualProjectId || requestedRepositoryId != actualRepositoryId)
        {
            throw new McpInputException("CrossProjectReference", "Reference identity does not match its stored target.");
        }
    }

    private static void VerifyReferenceHash(string? claimedHash, string actualHash)
    {
        if (claimedHash is not null && !string.Equals(claimedHash, actualHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new McpInputException("Conflict", "Reference hash does not match server evidence.");
        }
    }

    private static void EnsureReferencedScope(Scope scope, Guid projectId, Guid? repositoryId)
    {
        if (projectId != scope.Project.Id
            || (scope.Repository is null ? repositoryId is not null : repositoryId is not null && repositoryId != scope.Repository.Id))
        {
            throw new McpInputException("CrossProjectReference", "Reference is outside the requested scope.");
        }
    }

    private async Task PersistObservationAsync(
        IAIContextStorage storage,
        McpRecordKind recordKind,
        Guid recordId,
        int revision,
        Scope scope,
        Source source,
        Snapshot snapshot,
        DateTimeOffset recordObservedUtc,
        CancellationToken cancellationToken)
    {
        var sourceReference = await SourceReferenceAsync(storage, source, scope, cancellationToken);
        await storage.CreateMcpObservationAsync(new McpObservationDraft(
            recordKind,
            recordId,
            revision,
            scope.Project.Id,
            scope.Repository?.Id,
            source.Kind,
            sourceReference,
            source.ObservedUtc,
            snapshot.CanonicalRoot,
            snapshot.Branch,
            snapshot.Head,
            snapshot.WorkingTreeFingerprint,
            snapshot.ObservedUtc,
            snapshot.Completeness,
            recordObservedUtc), cancellationToken);
        if (sourceReference is not null)
        {
            await storage.CreateMcpReferenceAsync(new McpReferenceDraft(recordKind, recordId, revision, McpReferenceRole.Source, 0, sourceReference), cancellationToken);
        }
    }

    private async Task PersistReferencesAsync(
        IAIContextStorage storage,
        McpRecordKind recordKind,
        Guid recordId,
        int revision,
        McpReferenceRole role,
        IReadOnlyList<Reference> references,
        Scope scope,
        CancellationToken cancellationToken,
        McpRecordKind? expectedRecordKind = null)
    {
        for (var index = 0; index < references.Count; index++)
        {
            var reference = await StoredReferenceAsync(storage, references[index], scope, cancellationToken, expectedRecordKind);
            await storage.CreateMcpReferenceAsync(new McpReferenceDraft(recordKind, recordId, revision, role, index, reference), cancellationToken);
        }
    }

    private async Task<(ArtifactRecord Artifact, Reference Reference)> CreateArtifactAsync(
        IAIContextStorage storage,
        Scope scope,
        string? artifactReference,
        Snapshot snapshot,
        CancellationToken cancellationToken)
    {
        McpJson.Required(artifactReference, McpJson.Identifier, "artifact reference");
        var verified = ArtifactFileBoundary.Validate(_options.ArtifactRoot, artifactReference!, StorageLimits.Content);
        var artifact = await storage.CreateArtifactAsync(new ArtifactDraft(
            Guid.NewGuid(),
            scope.Project.Id,
            scope.Repository?.Id,
            "mcp-evidence",
            Path.GetFileName(verified.Reference),
            verified.Reference,
            verified.ContentHash,
            verified.SizeBytes,
            snapshot.Branch,
            snapshot.Head), cancellationToken);
        return (artifact, new Reference(scope.Project.Id.ToString("D"), "artifact", scope.Repository?.Id.ToString("D"), artifact.Id.ToString("D"), verified.Reference, null, verified.ContentHash));
    }

    private static void EnsureSafeRelative(string? value, string name)
    {
        McpJson.Required(value, McpJson.Identifier, name);
        if (Path.IsPathRooted(value)
            || value!.Contains(':', StringComparison.Ordinal)
            || value.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).Any(segment => segment is "." or ".."))
        {
            throw new McpInputException("PathRejected", $"{name} must be a safe relative path.");
        }
    }

    private static string? Bound(string? value, int length) => value is null || value.Length <= length ? value : value[..length];

    private static void EnsureSafeExternalUri(string? value)
    {
        McpJson.Required(value, McpJson.Identifier, "external URI");
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.UserInfo.Length != 0
            || uri.Scheme is not ("https" or "http"))
        {
            throw new McpInputException("InvalidInput", "External URI is invalid.");
        }
    }

    private static string ReferenceValue(Reference reference) => reference.RelativePath ?? reference.Id ?? reference.Uri ?? reference.Kind;

    private static class Mutations
    {
        public static async Task<McpInvocationResult> RecordContextAsync(McpToolAdapter adapter, ContextRecord request, JsonElement payload, string correlationId, CancellationToken cancellationToken)
        {
            McpJson.RequestId(request.RequestId);
            McpJson.RequiredId(request.ProjectId, "project id");
            McpJson.OptionalId(request.RepositoryId, "repository id");
            McpJson.Required(request.Category, McpJson.DefaultString, "category");
            McpJson.RequiredText(request.Summary, StorageLimits.Summary, "summary");
            McpJson.OptionalText(request.Content, StorageLimits.Content, "content");
            McpJson.OptionalText(request.Name, StorageLimits.Title, "name");
            McpJson.OptionalText(request.Objective, StorageLimits.Summary, "objective");
            McpJson.OptionalId(request.PhaseId, "phase id");
            McpJson.OptionalId(request.SupersedesId, "supersedes id");
            McpJson.Optional(request.ArtifactRef, McpJson.Identifier, "artifact reference");
            McpJson.ExpectedVersion(request.ExpectedVersion);
            McpJson.Optional(request.ExpectedSnapshotToken, McpJson.Cursor, "expected snapshot token");
            McpJson.SourceShape(request.Source);
            McpJson.SnapshotShape(request.ObservedSnapshot, "observed snapshot");
            if (request.Tier is < 1 or > 3) throw new McpInputException("InvalidInput", "Tier is invalid.");
            var phase = string.Equals(request.Category, "phase", StringComparison.Ordinal);
            if (phase && (request.PhaseStatus is null || request.Name is null || request.Objective is null))
            {
                throw new McpInputException("InvalidInput", "Phase context requires name, objective, and phase status.");
            }

            var scope = await adapter.RequireScopeAsync(request.ProjectId, request.RepositoryId, cancellationToken);
            return await adapter.ExecuteMutationAsync(scope, "context.record", request.RequestId, payload, async (storage, token) =>
            {
                adapter.VerifyObservedSnapshot(scope, request.ObservedSnapshot);
                if (phase) await adapter.RequireCurrentSnapshotAsync(storage, scope, request.ObservedSnapshot, request.ExpectedSnapshotToken, token);
                if (request.SupersedesId is not null)
                {
                    if (request.ExpectedVersion != 1) throw new McpInputException("Conflict", "Context supersession requires expected version 1.");
                    await adapter.RequireCurrentSnapshotAsync(storage, scope, request.ObservedSnapshot, request.ExpectedSnapshotToken, token);
                }
                else if (!phase && request.ExpectedVersion is not null)
                {
                    throw new McpInputException("InvalidInput", "Expected version applies only to a phase transition or context supersession.");
                }

                var phaseId = McpJson.OptionalId(request.PhaseId, "phase id");
                PhaseRecord? updatedPhase = null;
                int? linkVersion = null;
                if (phase)
                {
                    phaseId ??= Guid.NewGuid();
                    var existing = await storage.GetPhaseAsync(phaseId.Value, token);
                    if (existing is null)
                    {
                        if (request.ExpectedVersion is not null) throw new McpInputException("Conflict", "New phases do not have an expected version.");
                        var initial = await storage.UpsertPhaseAsync(new PhaseDraft(
                            phaseId.Value, scope.Project.Id, scope.Repository?.Id, request.Name!, request.Objective!, request.PhaseStatus!.Value,
                            request.ObservedSnapshot.Branch, request.ObservedSnapshot.Head, null, null, 1, null), null, token);
                        linkVersion = initial.Version;
                    }
                    else
                    {
                        EnsureExactScope(scope, existing.ProjectId, existing.RepositoryId);
                        if (request.ExpectedVersion != existing.Version) throw new McpInputException("Conflict", "Phase version does not match the current record.");
                        linkVersion = existing.Version;
                    }
                }

                var entryId = Guid.NewGuid();
                var contentHash = FilterHash(new { request.Category, request.Name, request.Summary, request.Content, request.Objective, PhaseId = phaseId });
                var entry = await storage.CreateContextEntryAsync(new ContextEntryDraft(
                    entryId,
                    scope.Project.Id,
                    scope.Repository?.Id,
                    request.Category,
                    request.Name ?? request.Category,
                    request.Summary,
                    request.Content,
                    null,
                    request.ObservedSnapshot.Branch,
                    request.ObservedSnapshot.Head,
                    ContextStatus.Active,
                    contentHash,
                    request.Tier,
                    request.Objective,
                    phaseId), token);

                if (phase)
                {
                    updatedPhase = await storage.UpsertPhaseAsync(new PhaseDraft(
                        phaseId!.Value, scope.Project.Id, scope.Repository?.Id, request.Name!, request.Objective!, request.PhaseStatus!.Value,
                        request.ObservedSnapshot.Branch, request.ObservedSnapshot.Head, null, null, 1, entry.Id), linkVersion, token);
                }

                await adapter.PersistObservationAsync(storage, McpRecordKind.ContextEntry, entry.Id, 1, scope, request.Source, request.ObservedSnapshot, request.ObservedSnapshot.ObservedUtc, token);
                if (request.ArtifactRef is not null)
                {
                    var artifact = await adapter.CreateArtifactAsync(storage, scope, request.ArtifactRef, request.ObservedSnapshot, token);
                    await adapter.PersistReferencesAsync(storage, McpRecordKind.ContextEntry, entry.Id, 1, McpReferenceRole.Artifact, [artifact.Reference], scope, token);
                }
                if (request.SupersedesId is not null)
                {
                    var predecessorId = McpJson.RequiredId(request.SupersedesId, "supersedes id");
                    var predecessor = await storage.GetContextEntryAsync(predecessorId, token) ?? throw new McpInputException("NotFound", "Superseded context was not found.");
                    EnsureReferencedScope(scope, predecessor.ProjectId, predecessor.RepositoryId);
                    await adapter.PersistReferencesAsync(storage, McpRecordKind.ContextEntry, entry.Id, 1, McpReferenceRole.Supersedes,
                        [new Reference(scope.Project.Id.ToString("D"), "record", predecessor.RepositoryId?.ToString("D"), predecessor.Id.ToString("D"))], scope, token);
                }

                return SuccessJson(new RecordReceipt(entry.Id.ToString("D"), "Recorded", entry.CreatedUtc, updatedPhase?.Version ?? 1), correlationId, McpJson.ResponseBytes);
            }, cancellationToken);
        }

        public static async Task<McpInvocationResult> RecordDecisionAsync(McpToolAdapter adapter, DecisionRecord request, JsonElement payload, string correlationId, CancellationToken cancellationToken)
        {
            McpJson.RequestId(request.RequestId);
            McpJson.RequiredId(request.ProjectId, "project id");
            McpJson.OptionalId(request.RepositoryId, "repository id");
            McpJson.RequiredText(request.Title, StorageLimits.Title, "title");
            McpJson.RequiredText(request.Decision, StorageLimits.Content, "decision");
            McpJson.RequiredText(request.Rationale, StorageLimits.Rationale, "rationale");
            McpJson.OptionalText(request.ResolutionEvidence, StorageLimits.Content, "resolution evidence");
            McpJson.OptionalId(request.SupersedesId, "supersedes id");
            McpJson.ExpectedVersion(request.ExpectedVersion);
            McpJson.Optional(request.ExpectedSnapshotToken, McpJson.Cursor, "expected snapshot token");
            McpJson.SourceShape(request.Source);
            McpJson.SnapshotShape(request.ObservedSnapshot, "observed snapshot");
            if (request.Status == DecisionStatus.Superseded && request.ResolutionEvidence is null) throw new McpInputException("InvalidInput", "Superseded decisions require resolution evidence.");
            var scope = await adapter.RequireScopeAsync(request.ProjectId, request.RepositoryId, cancellationToken);
            return await adapter.ExecuteMutationAsync(scope, "decision.record", request.RequestId, payload, async (storage, token) =>
            {
                adapter.VerifyObservedSnapshot(scope, request.ObservedSnapshot);
                AIContextMCP.Core.DecisionRecord? predecessor = null;
                if (request.SupersedesId is not null)
                {
                    if (request.ExpectedVersion is null) throw new McpInputException("InvalidInput", "Decision supersession requires an expected version.");
                    await adapter.RequireCurrentSnapshotAsync(storage, scope, request.ObservedSnapshot, request.ExpectedSnapshotToken, token);
                    predecessor = await storage.GetDecisionAsync(McpJson.RequiredId(request.SupersedesId, "supersedes id"), token)
                        ?? throw new McpInputException("NotFound", "Superseded decision was not found.");
                    EnsureExactScope(scope, predecessor.ProjectId, predecessor.RepositoryId);
                    if (predecessor.Version != request.ExpectedVersion) throw new McpInputException("Conflict", "Decision version does not match the current record.");
                }
                else if (request.ExpectedVersion is not null)
                {
                    throw new McpInputException("InvalidInput", "Expected version requires a superseded decision.");
                }

                var decision = await storage.CreateDecisionAsync(new DecisionDraft(
                    Guid.NewGuid(), scope.Project.Id, scope.Repository?.Id, "mcp", "mcp", request.Title, request.Decision, request.Rationale,
                    null, request.ResolutionEvidence, request.ObservedSnapshot.Head, request.Status, predecessor?.Id, request.ObservedSnapshot.Branch, 1), token);
                await adapter.PersistObservationAsync(storage, McpRecordKind.Decision, decision.Id, 1, scope, request.Source, request.ObservedSnapshot, request.ObservedSnapshot.ObservedUtc, token);
                if (predecessor is not null)
                {
                    await adapter.PersistReferencesAsync(storage, McpRecordKind.Decision, decision.Id, 1, McpReferenceRole.Supersedes,
                        [new Reference(scope.Project.Id.ToString("D"), "record", predecessor.RepositoryId?.ToString("D"), predecessor.Id.ToString("D"))], scope, token);
                }
                return SuccessJson(new DecisionReceipt(decision.Id.ToString("D"), "Recorded", decision.CreatedUtc, decision.Version), correlationId, McpJson.ResponseBytes);
            }, cancellationToken);
        }

        public static async Task<McpInvocationResult> RecordTestAsync(McpToolAdapter adapter, TestRecord request, JsonElement payload, string correlationId, CancellationToken cancellationToken)
        {
            McpJson.RequestId(request.RequestId);
            McpJson.RequiredId(request.ProjectId, "project id");
            McpJson.OptionalId(request.RepositoryId, "repository id");
            McpJson.RequiredText(request.Name, StorageLimits.Title, "name");
            McpJson.RequiredText(request.Summary, StorageLimits.Summary, "summary");
            McpJson.RequiredText(request.CommandData, StorageLimits.Command, "command data");
            McpJson.OptionalText(request.Evidence, StorageLimits.Content, "evidence");
            McpJson.Optional(request.ArtifactRef, McpJson.Identifier, "artifact reference");
            McpJson.Optional(request.ExpectedSnapshotToken, McpJson.Cursor, "expected snapshot token");
            McpJson.OptionalId(request.SupersedesId, "supersedes id");
            McpJson.SourceShape(request.Source);
            McpJson.SnapshotShape(request.RunSnapshot, "run snapshot");
            McpJson.Timestamp(request.ObservedUtc, "observed time");
            ValidateTestCounts(request);
            var scope = await adapter.RequireScopeAsync(request.ProjectId, request.RepositoryId, cancellationToken);
            return await adapter.ExecuteMutationAsync(scope, "test.record", request.RequestId, payload, async (storage, token) =>
            {
                adapter.VerifyObservedSnapshot(scope, request.RunSnapshot);
                if (request.ExpectedSnapshotToken is not null) await adapter.RequireCurrentSnapshotAsync(storage, scope, request.RunSnapshot, request.ExpectedSnapshotToken, token);
                TestRunRecord? predecessor = null;
                if (request.SupersedesId is not null)
                {
                    predecessor = await storage.GetTestRunAsync(McpJson.RequiredId(request.SupersedesId, "supersedes id"), token)
                        ?? throw new McpInputException("NotFound", "Superseded test run was not found.");
                    EnsureExactScope(scope, predecessor.ProjectId, predecessor.RepositoryId);
                }
                ArtifactRecord? artifact = null;
                Reference? artifactReference = null;
                if (request.ArtifactRef is not null)
                {
                    var created = await adapter.CreateArtifactAsync(storage, scope, request.ArtifactRef, request.RunSnapshot, token);
                    artifact = created.Artifact;
                    artifactReference = created.Reference;
                }
                var completed = request.DurationMs is { } duration ? request.ObservedUtc.AddMilliseconds(duration) : (DateTimeOffset?)null;
                var total = checked(request.Passed + request.Failed + request.Skipped);
                var test = await storage.CreateTestRunAsync(new TestRunDraft(
                    Guid.NewGuid(), scope.Project.Id, scope.Repository?.Id, request.CommandData, request.RunSnapshot.Branch, request.RunSnapshot.Head,
                    request.Passed, request.Failed, request.Skipped, total, request.Status,
                    artifact?.Id, artifact?.Reference, request.ObservedUtc, completed, request.Name, request.Summary, request.Evidence, request.ObservedUtc), token);
                await adapter.PersistObservationAsync(storage, McpRecordKind.TestRun, test.Id, 1, scope, request.Source, request.RunSnapshot, request.ObservedUtc, token);
                if (artifactReference is not null)
                {
                    await adapter.PersistReferencesAsync(storage, McpRecordKind.TestRun, test.Id, 1, McpReferenceRole.Artifact, [artifactReference], scope, token);
                }
                if (predecessor is not null)
                {
                    await adapter.PersistReferencesAsync(storage, McpRecordKind.TestRun, test.Id, 1, McpReferenceRole.Supersedes,
                        [new Reference(scope.Project.Id.ToString("D"), "record", predecessor.RepositoryId?.ToString("D"), predecessor.Id.ToString("D"))], scope, token, McpRecordKind.TestRun);
                }
                return SuccessJson(new TestReceipt(test.Id.ToString("D"), "Recorded", test.CreatedUtc, 1, predecessor?.Id.ToString("D")), correlationId, McpJson.ResponseBytes);
            }, cancellationToken);
        }

        public static async Task<McpInvocationResult> RecordFindingAsync(McpToolAdapter adapter, FindingRecord request, JsonElement payload, string correlationId, CancellationToken cancellationToken)
        {
            McpJson.RequestId(request.RequestId);
            McpJson.RequiredId(request.ProjectId, "project id");
            McpJson.OptionalId(request.RepositoryId, "repository id");
            McpJson.OptionalId(request.FindingId, "finding id");
            McpJson.RequiredText(request.Title, StorageLimits.Title, "title");
            McpJson.RequiredText(request.Description, StorageLimits.Content, "description");
            McpJson.OptionalText(request.Component, McpJson.DefaultString, "component");
            McpJson.OptionalText(request.Location, McpJson.Identifier, "location");
            McpJson.OptionalText(request.Remediation, StorageLimits.Content, "remediation");
            McpJson.OptionalText(request.ResolutionEvidence, StorageLimits.Content, "resolution evidence");
            McpJson.ExpectedVersion(request.ExpectedVersion);
            McpJson.Optional(request.ExpectedSnapshotToken, McpJson.Cursor, "expected snapshot token");
            McpJson.SourceShape(request.Source);
            McpJson.SnapshotShape(request.ObservedSnapshot, "observed snapshot");
            if (request.Status is FindingStatus.Resolved or FindingStatus.Superseded && request.ResolutionEvidence is null)
            {
                throw new McpInputException("InvalidInput", "Resolved and superseded findings require resolution evidence.");
            }
            var scope = await adapter.RequireScopeAsync(request.ProjectId, request.RepositoryId, cancellationToken);
            return await adapter.ExecuteMutationAsync(scope, "finding.record", request.RequestId, payload, async (storage, token) =>
            {
                adapter.VerifyObservedSnapshot(scope, request.ObservedSnapshot);
                AIContextMCP.Core.FindingRecord finding;
                if (request.FindingId is null)
                {
                    if (request.ExpectedVersion is not null) throw new McpInputException("InvalidInput", "New findings do not have an expected version.");
                    finding = await storage.CreateFindingAsync(new FindingDraft(
                        Guid.NewGuid(), scope.Project.Id, scope.Repository?.Id, FindingCategory.Implementation, request.Title, request.Description, request.Severity,
                        request.Status, request.Status is FindingStatus.Resolved or FindingStatus.Superseded ? "recorded" : null, request.ResolutionEvidence,
                        request.ObservedSnapshot.Branch, request.ObservedSnapshot.Head, null, request.Component, request.Location, request.Remediation), token);
                }
                else
                {
                    if (request.ExpectedVersion is null) throw new McpInputException("InvalidInput", "Finding updates require an expected version.");
                    await adapter.RequireCurrentSnapshotAsync(storage, scope, request.ObservedSnapshot, request.ExpectedSnapshotToken, token);
                    var findingId = McpJson.RequiredId(request.FindingId, "finding id");
                    var current = await storage.GetFindingAsync(findingId, token) ?? throw new McpInputException("NotFound", "Finding was not found.");
                    EnsureExactScope(scope, current.ProjectId, current.RepositoryId);
                    finding = await storage.ReviseFindingAsync(new FindingRevision(
                        findingId, request.ExpectedVersion.Value, FindingCategory.Implementation, request.Title, request.Description, request.Severity, request.Status,
                        request.Status is FindingStatus.Resolved or FindingStatus.Superseded ? "recorded" : null, request.ResolutionEvidence,
                        request.ObservedSnapshot.Branch, request.ObservedSnapshot.Head, null, request.Component, request.Location, request.Remediation), token);
                }
                await adapter.PersistObservationAsync(storage, McpRecordKind.Finding, finding.Id, finding.Revision, scope, request.Source, request.ObservedSnapshot, request.ObservedSnapshot.ObservedUtc, token);
                return SuccessJson(new FindingReceipt(finding.Id.ToString("D"), "Recorded", finding.UpdatedUtc, finding.Revision), correlationId, McpJson.ResponseBytes);
            }, cancellationToken);
        }

        public static async Task<McpInvocationResult> CreateHandoffAsync(McpToolAdapter adapter, HandoffCreate request, JsonElement payload, string correlationId, CancellationToken cancellationToken)
        {
            McpJson.RequestId(request.RequestId);
            McpJson.RequiredId(request.ProjectId, "project id");
            McpJson.OptionalId(request.RepositoryId, "repository id");
            McpJson.OptionalId(request.PhaseId, "phase id");
            McpJson.RequiredText(request.Objective, StorageLimits.HandoffField, "objective");
            McpJson.RequiredText(request.CompletedWork, StorageLimits.HandoffField, "completed work");
            McpJson.RequiredText(request.ActiveWork, StorageLimits.HandoffField, "active work");
            McpJson.RequiredText(request.NextAction, StorageLimits.HandoffField, "next action");
            McpJson.References(request.Blockers, "blockers");
            McpJson.References(request.Decisions, "decisions");
            McpJson.References(request.Tests, "tests");
            McpJson.References(request.Findings, "findings");
            McpJson.References(request.RelevantFiles, "relevant files");
            McpJson.References(request.Artifacts, "artifacts");
            McpJson.SnapshotShape(request.ObservedSnapshot, "observed snapshot");
            McpJson.Optional(request.ExpectedSnapshotToken, McpJson.Cursor, "expected snapshot token");
            if (request.Blockers.Length + request.Decisions.Length + request.Tests.Length + request.Findings.Length + request.RelevantFiles.Length + request.Artifacts.Length > StorageLimits.CollectionCount)
            {
                throw new McpInputException("LimitExceeded", "Handoff references exceed their aggregate limit.");
            }
            var scope = await adapter.RequireScopeAsync(request.ProjectId, request.RepositoryId, cancellationToken);
            return await adapter.ExecuteMutationAsync(scope, "handoff.create", request.RequestId, payload, async (storage, token) =>
            {
                adapter.VerifyObservedSnapshot(scope, request.ObservedSnapshot);
                await adapter.RequireCurrentSnapshotAsync(storage, scope, request.ObservedSnapshot, request.ExpectedSnapshotToken, token);

                var blockersReferences = await ValidateReferencesAsync(adapter, storage, request.Blockers, scope, "record", null, token);
                var decisionReferences = await ValidateReferencesAsync(adapter, storage, request.Decisions, scope, "record", McpRecordKind.Decision, token);
                var testReferences = await ValidateReferencesAsync(adapter, storage, request.Tests, scope, "record", McpRecordKind.TestRun, token);
                var findingReferences = await ValidateReferencesAsync(adapter, storage, request.Findings, scope, "record", McpRecordKind.Finding, token);
                var fileReferences = await ValidateReferencesAsync(adapter, storage, request.RelevantFiles, scope, "repository-document", null, token);
                var artifactReferences = await ValidateReferencesAsync(adapter, storage, request.Artifacts, scope, "artifact", null, token);
                var blockers = JoinReferences(blockersReferences, "blockers");
                var decisions = JoinReferences(decisionReferences, "decisions");
                var tests = JoinReferences(testReferences, "tests");
                var findings = JoinReferences(findingReferences, "findings");
                var files = fileReferences.Select(ReferenceValue).ToArray();
                var artifacts = artifactReferences.Select(ReferenceValue).ToArray();
                var aggregate = request.Objective.Length + request.CompletedWork.Length + request.ActiveWork.Length + blockers.Length + decisions.Length + tests.Length + findings.Length + request.NextAction.Length + files.Sum(file => file.Length) + artifacts.Sum(artifact => artifact.Length);
                if (aggregate > StorageLimits.HandoffAggregate) throw new McpInputException("LimitExceeded", "Handoff content exceeds its aggregate limit.");
                var handoff = await storage.CreateHandoffAsync(new HandoffDraft(
                    Guid.NewGuid(), scope.Project.Id, scope.Repository?.Id, request.ObservedSnapshot.Branch, request.ObservedSnapshot.Head,
                    request.Objective, request.CompletedWork, request.ActiveWork, blockers, decisions, tests, findings, files, artifacts, request.NextAction,
                    McpJson.OptionalId(request.PhaseId, "phase id")), token);
                var source = new Source("handoff", request.ObservedSnapshot.ObservedUtc);
                await adapter.PersistObservationAsync(storage, McpRecordKind.Handoff, handoff.Id, 1, scope, source, request.ObservedSnapshot, request.ObservedSnapshot.ObservedUtc, token);
                await adapter.PersistReferencesAsync(storage, McpRecordKind.Handoff, handoff.Id, 1, McpReferenceRole.Blocker, blockersReferences, scope, token);
                await adapter.PersistReferencesAsync(storage, McpRecordKind.Handoff, handoff.Id, 1, McpReferenceRole.Decision, decisionReferences, scope, token, McpRecordKind.Decision);
                await adapter.PersistReferencesAsync(storage, McpRecordKind.Handoff, handoff.Id, 1, McpReferenceRole.Test, testReferences, scope, token, McpRecordKind.TestRun);
                await adapter.PersistReferencesAsync(storage, McpRecordKind.Handoff, handoff.Id, 1, McpReferenceRole.Finding, findingReferences, scope, token, McpRecordKind.Finding);
                await adapter.PersistReferencesAsync(storage, McpRecordKind.Handoff, handoff.Id, 1, McpReferenceRole.RelevantFile, fileReferences, scope, token);
                await adapter.PersistReferencesAsync(storage, McpRecordKind.Handoff, handoff.Id, 1, McpReferenceRole.RelevantArtifact, artifactReferences, scope, token);
                return SuccessJson(new HandoffReceipt(handoff.Id.ToString("D"), "Recorded", handoff.CreatedUtc, 1), correlationId, McpJson.ResponseBytes);
            }, cancellationToken);
        }

        private static async Task<IReadOnlyList<Reference>> ValidateReferencesAsync(McpToolAdapter adapter, IAIContextStorage storage, IReadOnlyList<Reference> references, Scope scope, string expectedKind, McpRecordKind? expectedRecordKind, CancellationToken cancellationToken)
        {
            var validated = new List<Reference>(references.Count);
            foreach (var reference in references)
            {
                if (!string.Equals(reference.Kind, expectedKind, StringComparison.Ordinal)) throw new McpInputException("InvalidInput", "Handoff reference kind is invalid.");
                var stored = await adapter.StoredReferenceAsync(storage, reference, scope, cancellationToken, expectedRecordKind);
                validated.Add(ToReference(stored)!);
            }

            return validated;
        }

        private static string JoinReferences(IReadOnlyList<Reference> references, string name)
        {
            var value = string.Join("\n", references.Select(ReferenceValue));
            McpJson.RequiredText(value.Length == 0 ? "none" : value, StorageLimits.HandoffField, name);
            return value.Length == 0 ? "none" : value;
        }

        private static void EnsureExactScope(Scope scope, Guid projectId, Guid? repositoryId)
        {
            if (scope.Project.Id != projectId || scope.Repository?.Id != repositoryId)
            {
                throw new McpInputException("CrossProjectReference", "Record is outside the requested scope.");
            }
        }

        private static void ValidateTestCounts(TestRecord request)
        {
            if (request.Passed is < 0 or > 1_000_000_000
                || request.Failed is < 0 or > 1_000_000_000
                || request.Skipped is < 0 or > 1_000_000_000
                || request.DurationMs is < 0 or > 604_800_000)
            {
                throw new McpInputException("InvalidInput", "Test counts or duration are invalid.");
            }
            if ((request.Status == TestRunStatus.Passed && (request.Failed != 0 || request.Passed == 0))
                || (request.Status == TestRunStatus.Failed && request.Failed == 0)
                || (request.Status == TestRunStatus.Skipped && (request.Passed != 0 || request.Failed != 0 || request.Skipped == 0))
                || (request.Status is TestRunStatus.Blocked or TestRunStatus.Unknown && request.Passed + request.Failed + request.Skipped != 0))
            {
                throw new McpInputException("InvalidInput", "Test status does not match the supplied counts.");
            }
        }
    }
}
