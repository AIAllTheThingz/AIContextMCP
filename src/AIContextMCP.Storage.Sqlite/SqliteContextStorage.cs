using System.Globalization;
using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;
using AIContextMCP.Core;
using Microsoft.Data.Sqlite;

namespace AIContextMCP.Storage.Sqlite;

public sealed class SqliteContextStorage : IAIContextStorage
{
    private static readonly Regex CommitPattern = new("^[0-9a-fA-F]{7,64}$", RegexOptions.CultureInvariant);
    private static readonly Regex HashPattern = new("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant);
    private static readonly Regex CanonicalHashPattern = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    private static readonly Regex MutationToolPattern = new("^[a-z]+(?:\\.[a-z]+)+$", RegexOptions.CultureInvariant);
    private static readonly Regex MutationRequestIdPattern = new("^(?<issued>0|[1-9][0-9]{0,10}):(?<id>[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$", RegexOptions.CultureInvariant);
    private static readonly Regex ReceiptSecretPropertyPattern = new("^(?:password|pwd|passphrase|api[_-]?key|access[_-]?token|token)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private readonly SqliteStorageOptions _options;
    private readonly string _databasePath;
    private readonly AsyncLocal<MutationContext?> _mutationContext = new();

    public SqliteContextStorage(SqliteStorageOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _databasePath = NormalizeOperatorPath(options.DatabasePath, "database");
        ArtifactRoot = NormalizeOperatorPath(options.ArtifactRoot, "artifact root");
        if (options.BusyTimeoutMilliseconds is < 1 or > 30_000)
        {
            throw new StorageException(StorageErrorCode.Validation, "Busy timeout must be between 1 and 30000 milliseconds.");
        }
    }

    public string DatabasePath => _databasePath;
    public string ArtifactRoot { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(allowCreate: true, cancellationToken);
        var tables = await GetUserTablesAsync(connection, cancellationToken);
        if (tables.Count == 0)
        {
            if (await HasUserSchemaObjectsAsync(connection, cancellationToken))
            {
                throw new StorageException(StorageErrorCode.DatabaseCorrupt, "Database schema is malformed.");
            }

            try
            {
                await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
                await SqliteSchema.ApplyVersionOneAsync(connection, transaction, cancellationToken);
                await SqliteSchema.ApplyVersionTwoAsync(connection, transaction, cancellationToken);
                await SqliteSchema.ApplyVersionThreeAsync(connection, transaction, cancellationToken);
                await SqliteSchema.ApplyVersionFourAsync(connection, transaction, cancellationToken);
                await SqliteSchema.ApplyVersionFiveAsync(connection, transaction, cancellationToken);
                await InsertSchemaVersionAsync(connection, transaction, SqliteSchema.CurrentVersion, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch (SqliteException exception)
            {
                throw MapException(exception);
            }

            await ConfigureKnownDatabaseAsync(connection, cancellationToken);

            return;
        }

        await MigrateAndAssertCurrentSchemaAsync(connection, tables, cancellationToken);
        await ConfigureKnownDatabaseAsync(connection, cancellationToken);
    }

    public async Task<int> GetSchemaVersionAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenInitializedConnectionAsync(cancellationToken);
        return SqliteSchema.CurrentVersion;
    }

    public async Task<MutationReceipt> ExecuteMutationAsync(
        MutationRequest request,
        Func<IAIContextStorage, CancellationToken, Task<string>> callback,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (_mutationContext.Value is not null)
        {
            throw new StorageException(StorageErrorCode.Conflict, "Nested durable mutations are not supported.");
        }

        ValidateMutationRequest(request, UtcNow());
        cancellationToken.ThrowIfCancellationRequested();
        await using var connection = await OpenInitializedConnectionAsync(cancellationToken);
        try
        {
            await using var transaction = connection.BeginTransaction(deferred: false);
            var now = UtcNow();
            var validated = ValidateMutationRequest(request, now);
            cancellationToken.ThrowIfCancellationRequested();
            await PruneMutationReceiptsAsync(connection, transaction, now, cancellationToken);
            var existing = await GetMutationReceiptAsync(connection, transaction, validated.Scope, validated.Tool, validated.RequestId, cancellationToken);
            if (existing is not null)
            {
                if (!string.Equals(existing.CanonicalPayloadHash, validated.CanonicalPayloadHash, StringComparison.Ordinal))
                {
                    throw new StorageException(StorageErrorCode.Conflict, "Mutation request id was already used for a different payload.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                await transaction.CommitAsync(cancellationToken);
                return existing;
            }

            var context = new MutationContext(connection, transaction);
            _mutationContext.Value = context;
            try
            {
                var receiptJson = await callback(this, cancellationToken);
                await context.CloseAsync();
                validated = ValidateMutationRequest(request, UtcNow());
                cancellationToken.ThrowIfCancellationRequested();
                ValidateMutationReceiptJson(receiptJson);
                var receipt = new MutationReceipt(
                    validated.Scope,
                    validated.Tool,
                    validated.RequestId,
                    validated.CanonicalPayloadHash,
                    receiptJson,
                    validated.IssuedUtc,
                    validated.ExpiresUtc,
                    UtcNow());
                await InsertMutationReceiptAsync(connection, transaction, receipt, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                await transaction.CommitAsync(cancellationToken);
                return receipt;
            }
            finally
            {
                await context.CloseAsync();
                _mutationContext.Value = null;
            }
        }
        catch (SqliteException exception)
        {
            throw MapException(exception);
        }
    }

    public async Task<ProjectRecord> CreateProjectAsync(ProjectDraft project, CancellationToken cancellationToken = default)
    {
        ValidateProject(project);
        return await ExecuteStorageAsync(async (connection, transaction, token) =>
        {
            var now = UtcNow();
            await using var command = Command(connection, """
                INSERT INTO Projects (Id, StableProjectId, Name, Status, CreatedUtc, UpdatedUtc)
                VALUES ($id, $stableProjectId, $name, $status, $createdUtc, $updatedUtc);
                """);
            command.Transaction = transaction;
            Add(command, "$id", Id(project.Id));
            Add(command, "$stableProjectId", project.StableProjectId);
            Add(command, "$name", project.Name);
            Add(command, "$status", (int)project.Status);
            Add(command, "$createdUtc", Timestamp(now));
            Add(command, "$updatedUtc", Timestamp(now));
            await ExecuteAsync(command, token);
            return new ProjectRecord(project.Id, project.StableProjectId, project.Name, project.Status, now, now);
        }, cancellationToken);
    }

    public async Task<(ProjectRecord Project, RepositoryRecord Repository)> RegisterProjectAsync(ProjectDraft project, RepositoryDraft repository, CancellationToken cancellationToken = default)
    {
        ValidateProject(project);
        ValidateRepository(repository);
        if (repository.ProjectId != project.Id)
        {
            throw new StorageException(StorageErrorCode.CrossProjectReference, "Repository must belong to the registered project.");
        }

        return await ExecuteTransactionalWriteAsync(async (connection, transaction, token) =>
        {
            var now = UtcNow();
            await using (var projectCommand = Command(connection, "INSERT INTO Projects (Id, StableProjectId, Name, Status, CreatedUtc, UpdatedUtc) VALUES ($id, $stableProjectId, $name, $status, $createdUtc, $updatedUtc);"))
            {
                projectCommand.Transaction = transaction;
                Add(projectCommand, "$id", Id(project.Id));
                Add(projectCommand, "$stableProjectId", project.StableProjectId);
                Add(projectCommand, "$name", project.Name);
                Add(projectCommand, "$status", (int)project.Status);
                Add(projectCommand, "$createdUtc", Timestamp(now));
                Add(projectCommand, "$updatedUtc", Timestamp(now));
                await ExecuteAsync(projectCommand, token);
            }

            await InsertRepositoryAsync(connection, transaction, repository, now, token);
            return (
                new ProjectRecord(project.Id, project.StableProjectId, project.Name, project.Status, now, now),
                new RepositoryRecord(repository.Id, repository.ProjectId, repository.CanonicalPath, repository.Remote, repository.Organization, repository.RepositoryName, repository.DefaultBranch, repository.LastKnownBranch, repository.LastKnownCommitSha, repository.Status, now, now));
        }, cancellationToken);
    }

    public async Task<ProjectRecord?> GetProjectAsync(Guid id, CancellationToken cancellationToken = default)
    {
        RequireId(id, "project id");
        return await ExecuteStorageAsync(async (connection, transaction, token) =>
        {
            await using var command = Command(connection, "SELECT Id, StableProjectId, Name, Status, CreatedUtc, UpdatedUtc FROM Projects WHERE Id = $id;");
            command.Transaction = transaction;
            Add(command, "$id", Id(id));
            await using var reader = await ExecuteReaderAsync(command, token);
            return await ReadAsync(reader, token) ? ReadProject(reader) : null;
        }, cancellationToken);
    }

    public async Task<ProjectRecord?> GetProjectByStableIdAsync(string stableProjectId, CancellationToken cancellationToken = default)
    {
        ValidateRequired(stableProjectId, "stable project id", StorageLimits.Identifier);
        return await ExecuteStorageAsync(async (connection, transaction, token) =>
        {
            await using var command = Command(connection, "SELECT Id, StableProjectId, Name, Status, CreatedUtc, UpdatedUtc FROM Projects WHERE StableProjectId = $stableProjectId;");
            command.Transaction = transaction;
            Add(command, "$stableProjectId", stableProjectId);
            await using var reader = await ExecuteReaderAsync(command, token);
            return await ReadAsync(reader, token) ? ReadProject(reader) : null;
        }, cancellationToken);
    }

    public async Task<ProjectRecord> UpdateProjectAsync(Guid id, string name, ProjectStatus status, CancellationToken cancellationToken = default)
    {
        RequireId(id, "project id");
        ValidateRequired(name, "name", StorageLimits.Name);
        ValidateEnum(status, "project status");
        return await ExecuteStorageAsync(async (connection, transaction, token) =>
        {
            var existing = await GetProjectAsync(connection, transaction, id, token) ?? throw new StorageException(StorageErrorCode.NotFound, "Project was not found.");
            var now = UtcNow();
            await using var command = Command(connection, "UPDATE Projects SET Name = $name, Status = $status, UpdatedUtc = $updatedUtc WHERE Id = $id;");
            command.Transaction = transaction;
            Add(command, "$id", Id(id));
            Add(command, "$name", name);
            Add(command, "$status", (int)status);
            Add(command, "$updatedUtc", Timestamp(now));
            await ExecuteAsync(command, token);
            return existing with { Name = name, Status = status, UpdatedUtc = now };
        }, cancellationToken);
    }

    public async Task<RepositoryRecord> CreateRepositoryAsync(RepositoryDraft repository, CancellationToken cancellationToken = default)
    {
        ValidateRepository(repository);
        return await ExecuteStorageAsync(async (connection, transaction, token) =>
        {
            await EnsureProjectExistsAsync(connection, transaction, repository.ProjectId, token);
            var now = UtcNow();
            await InsertRepositoryAsync(connection, transaction, repository, now, token);
            return new RepositoryRecord(repository.Id, repository.ProjectId, repository.CanonicalPath, repository.Remote, repository.Organization, repository.RepositoryName, repository.DefaultBranch, repository.LastKnownBranch, repository.LastKnownCommitSha, repository.Status, now, now);
        }, cancellationToken);
    }

    public async Task<RepositoryRecord?> GetRepositoryAsync(Guid id, CancellationToken cancellationToken = default)
    {
        RequireId(id, "repository id");
        return await ExecuteStorageAsync(async (connection, transaction, token) =>
        {
            await using var command = Command(connection, "SELECT Id, ProjectId, CanonicalPath, Remote, Organization, RepositoryName, DefaultBranch, LastKnownBranch, LastKnownCommitSha, Status, CreatedUtc, UpdatedUtc FROM Repositories WHERE Id = $id;");
            command.Transaction = transaction;
            Add(command, "$id", Id(id));
            await using var reader = await ExecuteReaderAsync(command, token);
            return await ReadAsync(reader, token) ? ReadRepository(reader) : null;
        }, cancellationToken);
    }

    public async Task<RepositoryRecord?> GetRepositoryByCanonicalPathAsync(string canonicalPath, CancellationToken cancellationToken = default)
    {
        ValidateRequired(canonicalPath, "canonical path", StorageLimits.Identifier);
        return await ExecuteStorageAsync(async (connection, transaction, token) =>
        {
            await using var command = Command(connection, "SELECT Id, ProjectId, CanonicalPath, Remote, Organization, RepositoryName, DefaultBranch, LastKnownBranch, LastKnownCommitSha, Status, CreatedUtc, UpdatedUtc FROM Repositories WHERE CanonicalPath = $canonicalPath;");
            command.Transaction = transaction;
            Add(command, "$canonicalPath", canonicalPath);
            await using var reader = await ExecuteReaderAsync(command, token);
            return await ReadAsync(reader, token) ? ReadRepository(reader) : null;
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<RepositoryRecord>> ListRepositoriesByProjectAsync(Guid projectId, int maxResults = 20, CancellationToken cancellationToken = default)
    {
        RequireId(projectId, "project id");
        ValidateMaxResults(maxResults);
        return await ExecuteStorageAsync(async (connection, transaction, token) =>
        {
            await using var command = Command(connection, "SELECT Id, ProjectId, CanonicalPath, Remote, Organization, RepositoryName, DefaultBranch, LastKnownBranch, LastKnownCommitSha, Status, CreatedUtc, UpdatedUtc FROM Repositories WHERE ProjectId = $projectId ORDER BY CreatedUtc ASC, Id ASC LIMIT $maxResults;");
            command.Transaction = transaction;
            Add(command, "$projectId", Id(projectId));
            Add(command, "$maxResults", maxResults);
            return await ReadManyAsync(command, ReadRepository, token);
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<RepositoryRecord>> ListRepositoriesByRemoteAsync(string remote, int maxResults = 20, CancellationToken cancellationToken = default)
    {
        ValidateRequired(remote, "remote", StorageLimits.Identifier);
        ValidateMaxResults(maxResults);
        return await ExecuteStorageAsync(async (connection, transaction, token) =>
        {
            await using var command = Command(connection, "SELECT Id, ProjectId, CanonicalPath, Remote, Organization, RepositoryName, DefaultBranch, LastKnownBranch, LastKnownCommitSha, Status, CreatedUtc, UpdatedUtc FROM Repositories WHERE Remote = $remote ORDER BY CreatedUtc ASC, Id ASC LIMIT $maxResults;");
            command.Transaction = transaction;
            Add(command, "$remote", remote);
            Add(command, "$maxResults", maxResults);
            return await ReadManyAsync(command, ReadRepository, token);
        }, cancellationToken);
    }

    public async Task<RepositoryRecord> UpdateRepositoryAsync(RepositoryDraft repository, CancellationToken cancellationToken = default)
    {
        ValidateRepository(repository);
        return await ExecuteStorageAsync(async (connection, transaction, token) =>
        {
            var existing = await GetRepositoryAsync(connection, transaction, repository.Id, token) ?? throw new StorageException(StorageErrorCode.NotFound, "Repository was not found.");
            if (existing.ProjectId != repository.ProjectId)
            {
                throw new StorageException(StorageErrorCode.CrossProjectReference, "Repository project cannot be changed.");
            }

            var now = UtcNow();
            await using var command = Command(connection, """
                UPDATE Repositories
                SET CanonicalPath = $canonicalPath, Remote = $remote, Organization = $organization, RepositoryName = $repositoryName,
                    DefaultBranch = $defaultBranch, LastKnownBranch = $lastKnownBranch, LastKnownCommitSha = $lastKnownCommitSha,
                    Status = $status, UpdatedUtc = $updatedUtc
                WHERE Id = $id;
                """);
            command.Transaction = transaction;
            Add(command, "$id", Id(repository.Id));
            Add(command, "$canonicalPath", repository.CanonicalPath);
            Add(command, "$remote", repository.Remote);
            Add(command, "$organization", repository.Organization);
            Add(command, "$repositoryName", repository.RepositoryName);
            Add(command, "$defaultBranch", repository.DefaultBranch);
            Add(command, "$lastKnownBranch", repository.LastKnownBranch);
            Add(command, "$lastKnownCommitSha", repository.LastKnownCommitSha);
            Add(command, "$status", (int)repository.Status);
            Add(command, "$updatedUtc", Timestamp(now));
            await ExecuteAsync(command, token);
            return new RepositoryRecord(repository.Id, repository.ProjectId, repository.CanonicalPath, repository.Remote, repository.Organization, repository.RepositoryName, repository.DefaultBranch, repository.LastKnownBranch, repository.LastKnownCommitSha, repository.Status, existing.CreatedUtc, now);
        }, cancellationToken);
    }

    private static async Task InsertRepositoryAsync(SqliteConnection connection, SqliteTransaction? transaction, RepositoryDraft repository, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, """
            INSERT INTO Repositories (Id, ProjectId, CanonicalPath, Remote, Organization, RepositoryName, DefaultBranch, LastKnownBranch, LastKnownCommitSha, Status, CreatedUtc, UpdatedUtc)
            VALUES ($id, $projectId, $canonicalPath, $remote, $organization, $repositoryName, $defaultBranch, $lastKnownBranch, $lastKnownCommitSha, $status, $createdUtc, $updatedUtc);
            """);
        command.Transaction = transaction;
        Add(command, "$id", Id(repository.Id));
        Add(command, "$projectId", Id(repository.ProjectId));
        Add(command, "$canonicalPath", repository.CanonicalPath);
        Add(command, "$remote", repository.Remote);
        Add(command, "$organization", repository.Organization);
        Add(command, "$repositoryName", repository.RepositoryName);
        Add(command, "$defaultBranch", repository.DefaultBranch);
        Add(command, "$lastKnownBranch", repository.LastKnownBranch);
        Add(command, "$lastKnownCommitSha", repository.LastKnownCommitSha);
        Add(command, "$status", (int)repository.Status);
        Add(command, "$createdUtc", Timestamp(now));
        Add(command, "$updatedUtc", Timestamp(now));
        await ExecuteAsync(command, cancellationToken);
    }

    private async Task<T> ExecuteStorageAsync<T>(
        Func<SqliteConnection, SqliteTransaction?, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        var context = _mutationContext.Value;
        if (context is not null)
        {
            return await context.ExecuteAsync(operation, cancellationToken);
        }

        await using var connection = await OpenInitializedConnectionAsync(cancellationToken);
        return await operation(connection, null, cancellationToken);
    }

    private async Task<T> ExecuteTransactionalWriteAsync<T>(
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        var context = _mutationContext.Value;
        if (context is not null)
        {
            return await context.ExecuteAsync((connection, transaction, token) => operation(connection, transaction!, token), cancellationToken);
        }

        await using var connection = await OpenInitializedConnectionAsync(cancellationToken);
        try
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var result = await operation(connection, transaction, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (SqliteException exception)
        {
            throw MapException(exception);
        }
    }

    private static ValidatedMutationRequest ValidateMutationRequest(MutationRequest request, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scope = request.Scope ?? string.Empty;
        var tool = request.Tool ?? string.Empty;
        var canonicalPayloadHash = request.CanonicalPayloadHash ?? string.Empty;
        ValidateMutationScope(scope);
        ValidateMutationField(tool, "mutation tool", StorageLimits.Name);
        if (!MutationToolPattern.IsMatch(tool))
        {
            throw new StorageException(StorageErrorCode.Validation, "Mutation tool is malformed.");
        }

        if (!CanonicalHashPattern.IsMatch(canonicalPayloadHash))
        {
            throw new StorageException(StorageErrorCode.Validation, "Canonical payload hash is malformed.");
        }

        var requestId = request.RequestId ?? string.Empty;
        var match = MutationRequestIdPattern.Match(requestId);
        if (!match.Success
            || !long.TryParse(match.Groups["issued"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var epochSeconds)
            || !Guid.TryParseExact(match.Groups["id"].Value, "D", out _))
        {
            throw new StorageException(StorageErrorCode.Validation, "Mutation request id is malformed.");
        }

        DateTimeOffset issuedUtc;
        try
        {
            issuedUtc = DateTimeOffset.FromUnixTimeSeconds(epochSeconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new StorageException(StorageErrorCode.Validation, "Mutation request id timestamp is malformed.");
        }

        if (issuedUtc > now.AddSeconds(60) || issuedUtc < now.AddDays(-30))
        {
            throw new StorageException(StorageErrorCode.Conflict, "Mutation request id is outside the replay window.");
        }

        return new ValidatedMutationRequest(
            scope,
            tool,
            requestId,
            canonicalPayloadHash,
            issuedUtc,
            issuedUtc.AddDays(30));
    }

    private static void ValidateMutationField(string? value, string field, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value.Any(char.IsControl))
        {
            throw new StorageException(value is { Length: > 0 } && value.Length > maximumLength ? StorageErrorCode.LimitExceeded : StorageErrorCode.Validation, $"{field} is invalid.");
        }
    }

    private static void ValidateMutationScope(string? scope)
    {
        ValidateMutationField(scope, "mutation scope", StorageLimits.Identifier);
        if (SecretValueClassifier.IsSecretShaped(scope))
        {
            throw new StorageException(StorageErrorCode.SecretRejected, "Mutation scope contains secret-shaped data.");
        }
    }

    private static void ValidateMutationReceiptJson(string? receiptJson)
    {
        if (string.IsNullOrWhiteSpace(receiptJson))
        {
            throw new StorageException(StorageErrorCode.Validation, "Mutation receipt is required.");
        }

        if (Encoding.UTF8.GetByteCount(receiptJson) > StorageLimits.MutationReceiptBytes)
        {
            throw new StorageException(StorageErrorCode.LimitExceeded, "Mutation receipt exceeds its limit.");
        }

        try
        {
            using var document = JsonDocument.Parse(receiptJson, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 32 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new StorageException(StorageErrorCode.Validation, "Mutation receipt must be a JSON object.");
            }

            if (ContainsSecretShapedValue(document.RootElement))
            {
                throw new StorageException(StorageErrorCode.SecretRejected, "Mutation receipt contains secret-shaped data.");
            }
        }
        catch (JsonException exception)
        {
            throw new StorageException(StorageErrorCode.Validation, "Mutation receipt is not strict JSON.", exception);
        }
    }

    private static bool ContainsSecretShapedValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => SecretValueClassifier.IsSecretShaped(element.GetString()),
        JsonValueKind.Array => element.EnumerateArray().Any(ContainsSecretShapedValue),
        JsonValueKind.Object => element.EnumerateObject().Any(property =>
            (ReceiptSecretPropertyPattern.IsMatch(property.Name) && property.Value.ValueKind != JsonValueKind.Null && (property.Value.ValueKind != JsonValueKind.String || !string.IsNullOrEmpty(property.Value.GetString())))
            || ContainsSecretShapedValue(property.Value)),
        _ => false
    };

    private static async Task PruneMutationReceiptsAsync(SqliteConnection connection, SqliteTransaction transaction, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, "DELETE FROM MutationReceipts WHERE ExpiresUtc < $now;");
        command.Transaction = transaction;
        Add(command, "$now", Timestamp(now));
        await ExecuteAsync(command, cancellationToken);
    }

    private static async Task<MutationReceipt?> GetMutationReceiptAsync(SqliteConnection connection, SqliteTransaction transaction, string scope, string tool, string requestId, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, "SELECT Scope, Tool, RequestId, CanonicalPayloadHash, ReceiptJson, IssuedUtc, ExpiresUtc, CompletedUtc FROM MutationReceipts WHERE Scope = $scope AND Tool = $tool AND RequestId = $requestId;");
        command.Transaction = transaction;
        Add(command, "$scope", scope);
        Add(command, "$tool", tool);
        Add(command, "$requestId", requestId);
        await using var reader = await ExecuteReaderAsync(command, cancellationToken);
        return await ReadAsync(reader, cancellationToken) ? ReadMutationReceipt(reader) : null;
    }

    private static async Task InsertMutationReceiptAsync(SqliteConnection connection, SqliteTransaction transaction, MutationReceipt receipt, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, "INSERT INTO MutationReceipts (Scope, Tool, RequestId, CanonicalPayloadHash, ReceiptJson, IssuedUtc, ExpiresUtc, CompletedUtc) VALUES ($scope, $tool, $requestId, $canonicalPayloadHash, $receiptJson, $issuedUtc, $expiresUtc, $completedUtc);");
        command.Transaction = transaction;
        Add(command, "$scope", receipt.Scope);
        Add(command, "$tool", receipt.Tool);
        Add(command, "$requestId", receipt.RequestId);
        Add(command, "$canonicalPayloadHash", receipt.CanonicalPayloadHash);
        Add(command, "$receiptJson", receipt.ReceiptJson);
        Add(command, "$issuedUtc", Timestamp(receipt.IssuedUtc));
        Add(command, "$expiresUtc", Timestamp(receipt.ExpiresUtc));
        Add(command, "$completedUtc", Timestamp(receipt.CompletedUtc));
        await ExecuteAsync(command, cancellationToken);
    }

    private static MutationReceipt ReadMutationReceipt(SqliteDataReader reader)
    {
        try
        {
            var scope = reader.GetString(0);
            var tool = reader.GetString(1);
            var requestId = reader.GetString(2);
            var canonicalPayloadHash = reader.GetString(3);
            var receiptJson = reader.GetString(4);
            var issuedUtc = ReadTimestamp(reader.GetString(5));
            var expiresUtc = ReadTimestamp(reader.GetString(6));
            var completedUtc = ReadTimestamp(reader.GetString(7));
            ValidateMutationScope(scope);
            ValidateMutationField(tool, "mutation tool", StorageLimits.Name);
            var requestIdMatch = MutationRequestIdPattern.Match(requestId);
            if (!MutationToolPattern.IsMatch(tool)
                || !requestIdMatch.Success
                || !CanonicalHashPattern.IsMatch(canonicalPayloadHash)
                || !long.TryParse(requestIdMatch.Groups["issued"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var epochSeconds)
                || DateTimeOffset.FromUnixTimeSeconds(epochSeconds) != issuedUtc
                || expiresUtc != issuedUtc.AddDays(30))
            {
                throw new StorageException(StorageErrorCode.DatabaseCorrupt, "Stored mutation receipt is malformed.");
            }

            ValidateMutationReceiptJson(receiptJson);
            return new MutationReceipt(scope, tool, requestId, canonicalPayloadHash, receiptJson, issuedUtc, expiresUtc, completedUtc);
        }
        catch (StorageException exception) when (exception.Code is StorageErrorCode.Validation or StorageErrorCode.LimitExceeded or StorageErrorCode.SecretRejected)
        {
            throw new StorageException(StorageErrorCode.DatabaseCorrupt, "Stored mutation receipt is malformed.");
        }
    }

    private sealed record ValidatedMutationRequest(
        string Scope,
        string Tool,
        string RequestId,
        string CanonicalPayloadHash,
        DateTimeOffset IssuedUtc,
        DateTimeOffset ExpiresUtc);

    private sealed class MutationContext(SqliteConnection connection, SqliteTransaction transaction)
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private int _accepting = 1;

        public async Task<T> ExecuteAsync<T>(Func<SqliteConnection, SqliteTransaction?, CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _accepting) == 0)
            {
                throw new StorageException(StorageErrorCode.Conflict, "Mutation callback has completed.");
            }

            await _gate.WaitAsync(cancellationToken);
            try
            {
                if (Volatile.Read(ref _accepting) == 0)
                {
                    throw new StorageException(StorageErrorCode.Conflict, "Mutation callback has completed.");
                }

                return await operation(connection, transaction, cancellationToken);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task CloseAsync()
        {
            Interlocked.Exchange(ref _accepting, 0);
            await _gate.WaitAsync(CancellationToken.None);
            _gate.Release();
        }
    }

    private async Task<SqliteConnection> OpenInitializedConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = await OpenConnectionAsync(allowCreate: false, cancellationToken);
        try
        {
            await AssertCurrentSchemaAsync(connection, null, cancellationToken);
            await ConfigureKnownDatabaseAsync(connection, cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private async Task<SqliteConnection> OpenConnectionAsync(bool allowCreate, CancellationToken cancellationToken)
    {
        EnsureSafeDatabaseTarget();
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = allowCreate ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            Pooling = false,
            DefaultTimeout = Math.Max(1, (int)Math.Ceiling(_options.BusyTimeoutMilliseconds / 1_000d))
        };
        var connection = new SqliteConnection(builder.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            EnsureSafeDatabaseTarget();
            await SetPragmaAsync(connection, "PRAGMA foreign_keys = ON;", cancellationToken);
            await SetPragmaAsync(connection, $"PRAGMA busy_timeout = {_options.BusyTimeoutMilliseconds};", cancellationToken);
            await using var command = Command(connection, "PRAGMA foreign_keys;");
            if (Convert.ToInt32(await ExecuteScalarAsync(command, cancellationToken), CultureInfo.InvariantCulture) != 1)
            {
                throw new StorageException(StorageErrorCode.DatabaseCorrupt, "Foreign-key enforcement could not be enabled.");
            }

            return connection;
        }
        catch (StorageException)
        {
            await connection.DisposeAsync();
            throw;
        }
        catch (SqliteException exception)
        {
            await connection.DisposeAsync();
            throw MapException(exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            await connection.DisposeAsync();
            throw new StorageException(StorageErrorCode.DatabaseUnavailable, "Database cannot be opened.", exception);
        }
        catch (IOException exception)
        {
            await connection.DisposeAsync();
            throw new StorageException(StorageErrorCode.DatabaseUnavailable, "Database cannot be opened.", exception);
        }
    }

    private async Task MigrateAndAssertCurrentSchemaAsync(SqliteConnection connection, IReadOnlySet<string>? knownTables, CancellationToken cancellationToken)
    {
        try
        {
            var tables = knownTables ?? await GetUserTablesAsync(connection, cancellationToken);
            if (!tables.Contains("SchemaVersion"))
            {
                throw new StorageException(StorageErrorCode.DatabaseCorrupt, "Database schema is incomplete.");
            }

            await using var command = Command(connection, "SELECT Version FROM SchemaVersion ORDER BY Version;");
            await using var reader = await ExecuteReaderAsync(command, cancellationToken);
            var versions = new List<int>();
            while (await ReadAsync(reader, cancellationToken))
            {
                versions.Add(reader.GetInt32(0));
            }

            if (versions.Count != 1)
            {
                throw new StorageException(StorageErrorCode.DatabaseCorrupt, "Database schema version is invalid.");
            }

            var version = versions[0];
            if (version > SqliteSchema.CurrentVersion)
            {
                throw new StorageException(StorageErrorCode.UnsupportedSchema, "Database schema is newer than this application supports.");
            }

            if (version < 1)
            {
                throw new StorageException(StorageErrorCode.UnsupportedSchema, "Database schema version is not supported.");
            }

            if (!SqliteSchema.GetRequiredTables(version).IsSubsetOf(tables))
            {
                throw new StorageException(StorageErrorCode.DatabaseCorrupt, "Database schema is incomplete.");
            }

            if (version == SqliteSchema.CurrentVersion)
            {
                await ValidateSchemaStructureAsync(connection, version, cancellationToken);
                return;
            }

            await ValidateSchemaStructureAsync(connection, version, cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            if (version == 1)
            {
                await SqliteSchema.ApplyVersionTwoAsync(connection, transaction, cancellationToken);
            }

            if (version is 1 or 2)
            {
                await SqliteSchema.ApplyVersionThreeAsync(connection, transaction, cancellationToken);
            }

            if (version is 1 or 2 or 3)
            {
                await SqliteSchema.ApplyVersionFourAsync(connection, transaction, cancellationToken);
                await SqliteSchema.ApplyVersionFiveAsync(connection, transaction, cancellationToken);
            }
            else if (version == 4)
            {
                await SqliteSchema.ApplyVersionFiveAsync(connection, transaction, cancellationToken);
            }

            if (version is 1 or 2 or 3 or 4)
            {
                await using var update = Command(connection, "UPDATE SchemaVersion SET Version = $version, MigrationId = $migrationId, AppliedUtc = $appliedUtc WHERE Version = $previousVersion;");
                update.Transaction = transaction;
                Add(update, "$version", SqliteSchema.CurrentVersion);
                Add(update, "$migrationId", $"v{SqliteSchema.CurrentVersion}");
                Add(update, "$appliedUtc", Timestamp(UtcNow()));
                Add(update, "$previousVersion", version);
                if (await ExecuteCountAsync(update, cancellationToken) != 1)
                {
                    throw new StorageException(StorageErrorCode.DatabaseCorrupt, "Database schema version could not be migrated.");
                }

                await transaction.CommitAsync(cancellationToken);
                await ValidateSchemaStructureAsync(connection, SqliteSchema.CurrentVersion, cancellationToken);
                return;
            }

            throw new StorageException(StorageErrorCode.UnsupportedSchema, "Database schema version is not supported.");
        }
        catch (SqliteException exception)
        {
            throw MapException(exception);
        }
    }

    private async Task AssertCurrentSchemaAsync(SqliteConnection connection, IReadOnlySet<string>? knownTables, CancellationToken cancellationToken)
    {
        try
        {
            var tables = knownTables ?? await GetUserTablesAsync(connection, cancellationToken);
            if (!tables.Contains("SchemaVersion"))
            {
                throw new StorageException(StorageErrorCode.DatabaseCorrupt, "Database schema is incomplete.");
            }

            await using var command = Command(connection, "SELECT Version FROM SchemaVersion ORDER BY Version;");
            await using var reader = await ExecuteReaderAsync(command, cancellationToken);
            if (!await ReadAsync(reader, cancellationToken) || reader.GetInt32(0) != SqliteSchema.CurrentVersion || await ReadAsync(reader, cancellationToken))
            {
                throw new StorageException(StorageErrorCode.UnsupportedSchema, "Database schema is not initialized to the supported version.");
            }

            if (!SqliteSchema.RequiredTables.IsSubsetOf(tables))
            {
                throw new StorageException(StorageErrorCode.DatabaseCorrupt, "Database schema is incomplete.");
            }

            await ValidateSchemaStructureAsync(connection, SqliteSchema.CurrentVersion, cancellationToken);
        }
        catch (SqliteException exception)
        {
            throw MapException(exception);
        }
    }

    private static async Task ValidateSchemaStructureAsync(SqliteConnection connection, int version, CancellationToken cancellationToken)
    {
        await using (var indexCommand = Command(connection, "SELECT name FROM sqlite_master WHERE type = 'index';"))
        await using (var reader = await ExecuteReaderAsync(indexCommand, cancellationToken))
        {
            var indexes = new HashSet<string>(StringComparer.Ordinal);
            while (await ReadAsync(reader, cancellationToken))
            {
                indexes.Add(reader.GetString(0));
            }

            if (!SqliteSchema.GetRequiredIndexes(version).IsSubsetOf(indexes))
            {
                throw new StorageException(StorageErrorCode.DatabaseCorrupt, "Database indexes are incomplete.");
            }
        }

        foreach (var requirement in SqliteSchema.GetRequiredColumns(version))
        {
            await using var columnCommand = Command(connection, $"PRAGMA table_info([{requirement.Key}]);");
            await using var columnReader = await ExecuteReaderAsync(columnCommand, cancellationToken);
            var columns = new HashSet<string>(StringComparer.Ordinal);
            while (await ReadAsync(columnReader, cancellationToken))
            {
                columns.Add(columnReader.GetString(1));
            }

            if (!requirement.Value.IsSubsetOf(columns))
            {
                throw new StorageException(StorageErrorCode.DatabaseCorrupt, "Database table columns are incomplete.");
            }
        }

        foreach (var requirement in SqliteSchema.GetMinimumForeignKeys(version))
        {
            await using var foreignKeyCommand = Command(connection, $"PRAGMA foreign_key_list([{requirement.Key}]);");
            await using var foreignKeyReader = await ExecuteReaderAsync(foreignKeyCommand, cancellationToken);
            var count = 0;
            while (await ReadAsync(foreignKeyReader, cancellationToken))
            {
                count++;
            }

            if (count < requirement.Value)
            {
                throw new StorageException(StorageErrorCode.DatabaseCorrupt, "Database foreign-key constraints are incomplete.");
            }
        }
    }

    private static async Task ConfigureKnownDatabaseAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var journalMode = Command(connection, "PRAGMA journal_mode;");
        var currentMode = Convert.ToString(await ExecuteScalarAsync(journalMode, cancellationToken), CultureInfo.InvariantCulture);
        if (!string.Equals(currentMode, "delete", StringComparison.OrdinalIgnoreCase))
        {
            await SetPragmaAsync(connection, "PRAGMA journal_mode = DELETE;", cancellationToken);
        }

        await SetPragmaAsync(connection, "PRAGMA synchronous = FULL;", cancellationToken);
    }

    private static async Task<HashSet<string>> GetUserTablesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            await using var command = Command(connection, "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';");
            await using var reader = await ExecuteReaderAsync(command, cancellationToken);
            var tables = new HashSet<string>(StringComparer.Ordinal);
            while (await ReadAsync(reader, cancellationToken))
            {
                tables.Add(reader.GetString(0));
            }

            return tables;
        }
        catch (SqliteException exception)
        {
            throw MapException(exception);
        }
    }

    private static async Task<bool> HasUserSchemaObjectsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            await using var command = Command(connection, "SELECT 1 FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' LIMIT 1;");
            return await ExecuteScalarAsync(command, cancellationToken) is not null;
        }
        catch (SqliteException exception)
        {
            throw MapException(exception);
        }
    }

    private static async Task InsertSchemaVersionAsync(SqliteConnection connection, SqliteTransaction transaction, int version, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, "INSERT INTO SchemaVersion (Version, MigrationId, AppliedUtc) VALUES ($version, $migrationId, $appliedUtc);");
        command.Transaction = transaction;
        Add(command, "$version", version);
        Add(command, "$migrationId", $"v{version}");
        Add(command, "$appliedUtc", Timestamp(UtcNow()));
        await ExecuteAsync(command, cancellationToken);
    }

    private static async Task SetPragmaAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, sql);
        try
        {
            await ExecuteAsync(command, cancellationToken);
        }
        catch (SqliteException exception)
        {
            throw MapException(exception);
        }
    }

    private static SqliteCommand Command(SqliteConnection connection, string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        return command;
    }

    private static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static async Task ExecuteAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqliteException exception)
        {
            throw MapException(exception);
        }
    }

    private static async Task<int> ExecuteCountAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        try
        {
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqliteException exception)
        {
            throw MapException(exception);
        }
    }

    private static async Task<object?> ExecuteScalarAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        try
        {
            return await command.ExecuteScalarAsync(cancellationToken);
        }
        catch (SqliteException exception)
        {
            throw MapException(exception);
        }
    }

    private static async Task<SqliteDataReader> ExecuteReaderAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        try
        {
            return await command.ExecuteReaderAsync(cancellationToken);
        }
        catch (SqliteException exception)
        {
            throw MapException(exception);
        }
    }

    private static async Task<bool> ReadAsync(SqliteDataReader reader, CancellationToken cancellationToken)
    {
        try
        {
            return await reader.ReadAsync(cancellationToken);
        }
        catch (SqliteException exception)
        {
            throw MapException(exception);
        }
    }

    private static ProjectRecord ReadProject(SqliteDataReader reader) => new(
        ReadGuid(reader.GetString(0)), reader.GetString(1), reader.GetString(2), ReadEnum<ProjectStatus>(reader.GetInt32(3)), ReadTimestamp(reader.GetString(4)), ReadTimestamp(reader.GetString(5)));

    private static RepositoryRecord ReadRepository(SqliteDataReader reader) => new(
        ReadGuid(reader.GetString(0)), ReadGuid(reader.GetString(1)), reader.GetString(2), ReadNullableString(reader, 3), ReadNullableString(reader, 4), reader.GetString(5), reader.GetString(6), ReadNullableString(reader, 7), ReadNullableString(reader, 8), ReadEnum<RepositoryStatus>(reader.GetInt32(9)), ReadTimestamp(reader.GetString(10)), ReadTimestamp(reader.GetString(11)));

    private static async Task<T[]> ReadManyAsync<T>(SqliteCommand command, Func<SqliteDataReader, T> read, CancellationToken cancellationToken)
    {
        await using var reader = await ExecuteReaderAsync(command, cancellationToken);
        var records = new List<T>();
        while (await ReadAsync(reader, cancellationToken))
        {
            records.Add(read(reader));
        }

        return records.ToArray();
    }

    private static async Task<ProjectRecord?> GetProjectAsync(SqliteConnection connection, SqliteTransaction? transaction, Guid id, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, "SELECT Id, StableProjectId, Name, Status, CreatedUtc, UpdatedUtc FROM Projects WHERE Id = $id;");
        command.Transaction = transaction;
        Add(command, "$id", Id(id));
        await using var reader = await ExecuteReaderAsync(command, cancellationToken);
        return await ReadAsync(reader, cancellationToken) ? ReadProject(reader) : null;
    }

    private static async Task<RepositoryRecord?> GetRepositoryAsync(SqliteConnection connection, SqliteTransaction? transaction, Guid id, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, "SELECT Id, ProjectId, CanonicalPath, Remote, Organization, RepositoryName, DefaultBranch, LastKnownBranch, LastKnownCommitSha, Status, CreatedUtc, UpdatedUtc FROM Repositories WHERE Id = $id;");
        command.Transaction = transaction;
        Add(command, "$id", Id(id));
        await using var reader = await ExecuteReaderAsync(command, cancellationToken);
        return await ReadAsync(reader, cancellationToken) ? ReadRepository(reader) : null;
    }

    private static async Task EnsureProjectExistsAsync(SqliteConnection connection, SqliteTransaction? transaction, Guid projectId, CancellationToken cancellationToken)
    {
        RequireId(projectId, "project id");
        await using var command = Command(connection, "SELECT 1 FROM Projects WHERE Id = $id;");
        command.Transaction = transaction;
        Add(command, "$id", Id(projectId));
        if (await ExecuteScalarAsync(command, cancellationToken) is null)
        {
            throw new StorageException(StorageErrorCode.NotFound, "Project was not found.");
        }
    }

    private static void ValidateProject(ProjectDraft project)
    {
        ArgumentNullException.ThrowIfNull(project);
        RequireId(project.Id, "project id");
        ValidateRequired(project.StableProjectId, "stable project id", StorageLimits.Identifier);
        ValidateRequired(project.Name, "name", StorageLimits.Name);
        ValidateEnum(project.Status, "project status");
    }

    private static void ValidateRepository(RepositoryDraft repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        RequireId(repository.Id, "repository id");
        RequireId(repository.ProjectId, "project id");
        ValidateRequired(repository.CanonicalPath, "canonical path", StorageLimits.Identifier);
        ValidateOptional(repository.Remote, "remote", StorageLimits.Identifier);
        ValidateOptional(repository.Organization, "organization", StorageLimits.Name);
        ValidateRequired(repository.RepositoryName, "repository name", StorageLimits.Name);
        ValidateRequired(repository.DefaultBranch, "default branch", StorageLimits.ShortText);
        ValidateOptional(repository.LastKnownBranch, "last known branch", StorageLimits.ShortText);
        ValidateCommit(repository.LastKnownCommitSha, "last known commit");
        ValidateEnum(repository.Status, "repository status");
    }

    public async Task<ContextEntryRecord> CreateContextEntryAsync(ContextEntryDraft entry, CancellationToken cancellationToken = default)
    {
        ValidateContextEntry(entry);
        return await ExecuteStorageAsync(async (connection, transaction, token) =>
        {
            await EnsureProjectExistsAsync(connection, transaction, entry.ProjectId, token);
            await EnsureRepositoryScopeAsync(connection, transaction, entry.ProjectId, entry.RepositoryId, token);
            await EnsurePhaseScopeAsync(connection, transaction, entry.ProjectId, entry.RepositoryId, entry.PhaseId, token);
            var now = UtcNow();
            var supersededUtc = entry.Status == ContextStatus.Superseded ? now : (DateTimeOffset?)null;
            await using var command = Command(connection, """
                INSERT INTO ContextEntries (Id, ProjectId, RepositoryId, Category, Title, Summary, Content, AuthoritativeReference, Branch, CommitSha, Status, ContentHash, CreatedUtc, UpdatedUtc, SupersededUtc, Tier, Objective, PhaseId)
                VALUES ($id, $projectId, $repositoryId, $category, $title, $summary, $content, $authoritativeReference, $branch, $commitSha, $status, $contentHash, $createdUtc, $updatedUtc, $supersededUtc, $tier, $objective, $phaseId);
                """);
            command.Transaction = transaction;
            Add(command, "$id", Id(entry.Id));
            Add(command, "$projectId", Id(entry.ProjectId));
            Add(command, "$repositoryId", NullableId(entry.RepositoryId));
            Add(command, "$category", entry.Category);
            Add(command, "$title", entry.Title);
            Add(command, "$summary", entry.Summary);
            Add(command, "$content", entry.Content);
            Add(command, "$authoritativeReference", entry.AuthoritativeReference);
            Add(command, "$branch", entry.Branch);
            Add(command, "$commitSha", entry.CommitSha);
            Add(command, "$status", (int)entry.Status);
            Add(command, "$contentHash", entry.ContentHash);
            Add(command, "$createdUtc", Timestamp(now));
            Add(command, "$updatedUtc", Timestamp(now));
            Add(command, "$supersededUtc", NullableTimestamp(supersededUtc));
            Add(command, "$tier", entry.Tier);
            Add(command, "$objective", entry.Objective);
            Add(command, "$phaseId", NullableId(entry.PhaseId));
            await ExecuteAsync(command, token);
            return new ContextEntryRecord(entry.Id, entry.ProjectId, entry.RepositoryId, entry.Category, entry.Title, entry.Summary, entry.Content, entry.AuthoritativeReference, entry.Branch, entry.CommitSha, entry.Status, entry.ContentHash, now, now, supersededUtc, entry.Tier, entry.Objective, entry.PhaseId);
        }, cancellationToken);
    }

    public async Task SupersedeContextEntryAsync(Guid id, Guid projectId, Guid? repositoryId, Guid supersededById, CancellationToken cancellationToken = default)
    {
        RequireId(id, "context entry id");
        RequireId(projectId, "project id");
        RequireOptionalId(repositoryId, "repository id");
        RequireId(supersededById, "superseding context entry id");
        if (id == supersededById) throw new StorageException(StorageErrorCode.Conflict, "Context entry cannot supersede itself.");
        await ExecuteTransactionalWriteAsync(async (connection, transaction, token) =>
        {
            await using var successor = Command(connection, "SELECT ProjectId, RepositoryId FROM ContextEntries WHERE Id = $id;");
            successor.Transaction = transaction;
            Add(successor, "$id", Id(supersededById));
            await using var successorReader = await ExecuteReaderAsync(successor, token);
            if (!await ReadAsync(successorReader, token)) throw new StorageException(StorageErrorCode.NotFound, "Superseding context entry was not found.");
            var successorProject = successorReader.GetString(0);
            var successorRepository = successorReader.IsDBNull(1) ? null : successorReader.GetString(1);
            if (!string.Equals(successorProject, Id(projectId), StringComparison.Ordinal)
                || !string.Equals(successorRepository, NullableId(repositoryId), StringComparison.Ordinal))
            {
                throw new StorageException(StorageErrorCode.CrossProjectReference, "Context supersession must remain within the same project and repository scope.");
            }

            await using var update = Command(connection, """
                UPDATE ContextEntries
                SET Status = $status, UpdatedUtc = $updatedUtc, SupersededUtc = $supersededUtc
                WHERE Id = $id AND ProjectId = $projectId
                  AND ((RepositoryId IS NULL AND $repositoryId IS NULL) OR RepositoryId = $repositoryId)
                  AND Status = $active AND SupersededUtc IS NULL;
                """);
            update.Transaction = transaction;
            var now = UtcNow();
            Add(update, "$status", (int)ContextStatus.Superseded);
            Add(update, "$updatedUtc", Timestamp(now));
            Add(update, "$supersededUtc", Timestamp(now));
            Add(update, "$id", Id(id));
            Add(update, "$projectId", Id(projectId));
            Add(update, "$repositoryId", NullableId(repositoryId));
            Add(update, "$active", (int)ContextStatus.Active);
            if (await ExecuteCountAsync(update, token) == 1) return true;

            await using var exists = Command(connection, "SELECT ProjectId, RepositoryId, Status FROM ContextEntries WHERE Id = $id;");
            exists.Transaction = transaction;
            Add(exists, "$id", Id(id));
            await using var reader = await ExecuteReaderAsync(exists, token);
            if (!await ReadAsync(reader, token)) throw new StorageException(StorageErrorCode.NotFound, "Context entry to supersede was not found.");
            var foundProject = reader.GetString(0);
            var foundRepository = reader.IsDBNull(1) ? null : reader.GetString(1);
            if (!string.Equals(foundProject, Id(projectId), StringComparison.Ordinal)
                || !string.Equals(foundRepository, NullableId(repositoryId), StringComparison.Ordinal))
            {
                throw new StorageException(StorageErrorCode.CrossProjectReference, "Context supersession must remain within the same project and repository scope.");
            }
            throw new StorageException(StorageErrorCode.Conflict, "Context entry has already been superseded or is not active.");
        }, cancellationToken);
    }

    public async Task<ContextEntryRecord?> GetContextEntryAsync(Guid id, CancellationToken cancellationToken = default)
    {
        RequireId(id, "context entry id");
        return await ExecuteStorageAsync(async (connection, transaction, token) =>
        {
            await using var command = Command(connection, "SELECT Id, ProjectId, RepositoryId, Category, Title, Summary, Content, AuthoritativeReference, Branch, CommitSha, Status, ContentHash, CreatedUtc, UpdatedUtc, SupersededUtc, Tier, Objective, PhaseId FROM ContextEntries WHERE Id = $id;");
            command.Transaction = transaction;
            Add(command, "$id", Id(id));
            await using var reader = await ExecuteReaderAsync(command, token);
            return await ReadAsync(reader, token) ? ReadContextEntry(reader) : null;
        }, cancellationToken);
    }

    public async Task<ContextEntryRecord?> GetContextEntryByContentHashAsync(Guid projectId, Guid? repositoryId, string contentHash, CancellationToken cancellationToken = default)
    {
        RequireId(projectId, "project id");
        RequireOptionalId(repositoryId, "repository id");
        ValidateHash(contentHash, "content hash");
        return await ExecuteStorageAsync(async (connection, transaction, token) =>
        {
            await using var command = Command(connection, """
                SELECT Id, ProjectId, RepositoryId, Category, Title, Summary, Content, AuthoritativeReference, Branch, CommitSha, Status, ContentHash, CreatedUtc, UpdatedUtc, SupersededUtc, Tier, Objective, PhaseId
                FROM ContextEntries
                WHERE ProjectId = $projectId AND ((RepositoryId IS NULL AND $repositoryId IS NULL) OR RepositoryId = $repositoryId) AND ContentHash = $contentHash
                ORDER BY CreatedUtc DESC, Id ASC LIMIT 1;
                """);
            command.Transaction = transaction;
            Add(command, "$projectId", Id(projectId));
            Add(command, "$repositoryId", NullableId(repositoryId));
            Add(command, "$contentHash", contentHash);
            await using var reader = await ExecuteReaderAsync(command, token);
            return await ReadAsync(reader, token) ? ReadContextEntry(reader) : null;
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<ContextEntryRecord>> ListContextEntriesAsync(ContextEntryQuery query, CancellationToken cancellationToken = default)
    {
        ValidateContextEntryQuery(query);
        return await ExecuteStorageAsync(async (connection, transaction, token) =>
        {
            var sql = new StringBuilder("SELECT Id, ProjectId, RepositoryId, Category, Title, Summary, Content, AuthoritativeReference, Branch, CommitSha, Status, ContentHash, CreatedUtc, UpdatedUtc, SupersededUtc, Tier, Objective, PhaseId FROM ContextEntries WHERE ProjectId = $projectId");
            if (query.RepositoryId is not null) sql.Append(" AND RepositoryId = $repositoryId");
            if (query.RepositoryIsNull) sql.Append(" AND RepositoryId IS NULL");
            if (query.Category is not null) sql.Append(" AND Category = $category");
            if (query.Branch is not null) sql.Append(" AND Branch = $branch");
            if (query.CommitSha is not null) sql.Append(" AND CommitSha = $commitSha");
            if (query.Status is not null) sql.Append(" AND Status = $status");
            if (query.SinceUtc is not null) sql.Append(" AND CreatedUtc >= $sinceUtc");
            if (query.Text is not null) sql.Append(" AND (Title LIKE $text ESCAPE '\\' OR Summary LIKE $text ESCAPE '\\')");
            sql.Append(" ORDER BY CreatedUtc DESC, Id ASC LIMIT $maxResults;");
            await using var command = Command(connection, sql.ToString());
            command.Transaction = transaction;
            Add(command, "$projectId", Id(query.ProjectId));
            if (query.RepositoryId is { } repositoryId) Add(command, "$repositoryId", Id(repositoryId));
            if (query.Category is not null) Add(command, "$category", query.Category);
            if (query.Branch is not null) Add(command, "$branch", query.Branch);
            if (query.CommitSha is not null) Add(command, "$commitSha", query.CommitSha);
            if (query.Status is { } status) Add(command, "$status", (int)status);
            if (query.SinceUtc is { } sinceUtc) Add(command, "$sinceUtc", Timestamp(sinceUtc));
            if (query.Text is not null) Add(command, "$text", $"%{EscapeLike(query.Text)}%");
            Add(command, "$maxResults", query.MaxResults);
            return await ReadManyAsync(command, ReadContextEntry, token);
        }, cancellationToken);
    }

    public async Task<DecisionRecord> CreateDecisionAsync(DecisionDraft decision, CancellationToken cancellationToken = default)
    {
        ValidateDecision(decision);
        return await ExecuteTransactionalWriteAsync(async (connection, transaction, token) =>
        {
            await EnsureProjectExistsAsync(connection, transaction, decision.ProjectId, token);
            await EnsureRepositoryScopeAsync(connection, transaction, decision.ProjectId, decision.RepositoryId, token);
            if (decision.SupersedesDecisionId is { } targetId)
            {
                var target = await GetDecisionScopeAsync(connection, transaction, targetId, token)
                    ?? throw new StorageException(StorageErrorCode.NotFound, "Decision to supersede was not found.");
                if (target.ProjectId != decision.ProjectId || target.RepositoryId != decision.RepositoryId)
                {
                    throw new StorageException(StorageErrorCode.CrossProjectReference, "Decision supersession must remain within the same project and repository scope.");
                }

                if (target.Status == DecisionStatus.Superseded || target.SupersededByDecisionId is not null)
                {
                    throw new StorageException(StorageErrorCode.Conflict, "Decision has already been superseded.");
                }
            }

            var now = UtcNow();
            await InsertDecisionAsync(connection, transaction, decision, now, token);
            if (decision.SupersedesDecisionId is { } oldId)
            {
                await using var supersede = Command(connection, """
                    UPDATE Decisions
                    SET Status = $status, SupersededByDecisionId = $supersededByDecisionId, UpdatedUtc = $updatedUtc, SupersededUtc = $supersededUtc, Version = Version + 1
                    WHERE Id = $id AND ProjectId = $projectId AND SupersededByDecisionId IS NULL;
                    """);
                supersede.Transaction = transaction;
                Add(supersede, "$status", (int)DecisionStatus.Superseded);
                Add(supersede, "$supersededByDecisionId", Id(decision.Id));
                Add(supersede, "$updatedUtc", Timestamp(now));
                Add(supersede, "$supersededUtc", Timestamp(now));
                Add(supersede, "$id", Id(oldId));
                Add(supersede, "$projectId", Id(decision.ProjectId));
                if (await ExecuteCountAsync(supersede, token) != 1)
                {
                    throw new StorageException(StorageErrorCode.Conflict, "Decision supersession could not be completed.");
                }
            }

            return new DecisionRecord(decision.Id, decision.ProjectId, decision.RepositoryId, decision.Category, decision.Component, decision.Title, decision.Decision, decision.Rationale, decision.AuthoritativeReference, decision.ResolutionEvidence, decision.OriginatingCommitSha, decision.Status, decision.SupersedesDecisionId, null, now, now, null, decision.Branch, decision.Version);
        }, cancellationToken);
    }

    public async Task<DecisionRecord?> GetDecisionAsync(Guid id, CancellationToken cancellationToken = default)
    {
        RequireId(id, "decision id");
        return await ExecuteStorageAsync(async (connection, transaction, token) =>
        {
            await using var command = Command(connection, "SELECT Id, ProjectId, RepositoryId, Category, Component, Title, DecisionText, Rationale, AuthoritativeReference, ResolutionEvidence, OriginatingCommitSha, Branch, Status, SupersedesDecisionId, SupersededByDecisionId, CreatedUtc, UpdatedUtc, SupersededUtc, Version FROM Decisions WHERE Id = $id;");
            command.Transaction = transaction;
            Add(command, "$id", Id(id));
            await using var reader = await ExecuteReaderAsync(command, token);
            return await ReadAsync(reader, token) ? ReadDecision(reader) : null;
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<DecisionRecord>> ListDecisionsAsync(Guid projectId, bool includeSuperseded = false, int maxResults = 50, CancellationToken cancellationToken = default)
    {
        RequireId(projectId, "project id");
        ValidateMaxResults(maxResults);
        return await ExecuteStorageAsync(async (connection, transaction, token) =>
        {
            await using var command = Command(connection, """
                SELECT Id, ProjectId, RepositoryId, Category, Component, Title, DecisionText, Rationale, AuthoritativeReference, ResolutionEvidence, OriginatingCommitSha, Branch, Status, SupersedesDecisionId, SupersededByDecisionId, CreatedUtc, UpdatedUtc, SupersededUtc, Version
                FROM Decisions
                WHERE ProjectId = $projectId AND ($includeSuperseded = 1 OR Status <> $superseded)
                ORDER BY CreatedUtc DESC, Id ASC
                LIMIT $maxResults;
                """);
            command.Transaction = transaction;
            Add(command, "$projectId", Id(projectId));
            Add(command, "$includeSuperseded", includeSuperseded ? 1 : 0);
            Add(command, "$superseded", (int)DecisionStatus.Superseded);
            Add(command, "$maxResults", maxResults);
            await using var reader = await ExecuteReaderAsync(command, token);
            var decisions = new List<DecisionRecord>();
            while (await ReadAsync(reader, token))
            {
                decisions.Add(ReadDecision(reader));
            }

            return decisions;
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<DecisionRecord>> ListDecisionsAsync(DecisionQuery query, CancellationToken cancellationToken = default)
    {
        ValidateDecisionQuery(query);
        return await ExecuteStorageAsync(async (connection, transaction, token) =>
        {
            var sql = new StringBuilder("SELECT Id, ProjectId, RepositoryId, Category, Component, Title, DecisionText, Rationale, AuthoritativeReference, ResolutionEvidence, OriginatingCommitSha, Branch, Status, SupersedesDecisionId, SupersededByDecisionId, CreatedUtc, UpdatedUtc, SupersededUtc, Version FROM Decisions WHERE ProjectId = $projectId");
            if (query.RepositoryId is not null) sql.Append(" AND RepositoryId = $repositoryId");
            if (query.RepositoryIsNull) sql.Append(" AND RepositoryId IS NULL");
            if (query.Category is not null) sql.Append(" AND Category = $category");
            if (query.Component is not null) sql.Append(" AND Component = $component");
            if (query.Branch is not null) sql.Append(" AND Branch = $branch");
            if (query.BranchIsNull) sql.Append(" AND Branch IS NULL");
            if (query.CommitSha is not null) sql.Append(" AND OriginatingCommitSha = $commitSha");
            if (query.Status is not null) sql.Append(" AND Status = $status");
            else if (!query.IncludeSuperseded) sql.Append(" AND Status <> $superseded");
            if (query.SinceUtc is not null) sql.Append(" AND CreatedUtc >= $sinceUtc");
            sql.Append(" ORDER BY CreatedUtc DESC, Id ASC LIMIT $maxResults;");
            await using var command = Command(connection, sql.ToString());
            command.Transaction = transaction;
            Add(command, "$projectId", Id(query.ProjectId));
            if (query.RepositoryId is { } repositoryId) Add(command, "$repositoryId", Id(repositoryId));
            if (query.Category is not null) Add(command, "$category", query.Category);
            if (query.Component is not null) Add(command, "$component", query.Component);
            if (query.Branch is not null) Add(command, "$branch", query.Branch);
            if (query.CommitSha is not null) Add(command, "$commitSha", query.CommitSha);
            if (query.Status is { } status) Add(command, "$status", (int)status);
            else if (!query.IncludeSuperseded) Add(command, "$superseded", (int)DecisionStatus.Superseded);
            if (query.SinceUtc is { } sinceUtc) Add(command, "$sinceUtc", Timestamp(sinceUtc));
            Add(command, "$maxResults", query.MaxResults);
            return await ReadManyAsync(command, ReadDecision, token);
        }, cancellationToken);
    }

    private static async Task InsertDecisionAsync(SqliteConnection connection, SqliteTransaction transaction, DecisionDraft decision, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, """
            INSERT INTO Decisions (Id, ProjectId, RepositoryId, Category, Component, Title, DecisionText, Rationale, AuthoritativeReference, ResolutionEvidence, OriginatingCommitSha, Branch, Status, SupersedesDecisionId, SupersededByDecisionId, CreatedUtc, UpdatedUtc, SupersededUtc, Version)
            VALUES ($id, $projectId, $repositoryId, $category, $component, $title, $decisionText, $rationale, $authoritativeReference, $resolutionEvidence, $originatingCommitSha, $branch, $status, $supersedesDecisionId, NULL, $createdUtc, $updatedUtc, NULL, $version);
            """);
        command.Transaction = transaction;
        Add(command, "$id", Id(decision.Id));
        Add(command, "$projectId", Id(decision.ProjectId));
        Add(command, "$repositoryId", NullableId(decision.RepositoryId));
        Add(command, "$category", decision.Category);
        Add(command, "$component", decision.Component);
        Add(command, "$title", decision.Title);
        Add(command, "$decisionText", decision.Decision);
        Add(command, "$rationale", decision.Rationale);
        Add(command, "$authoritativeReference", decision.AuthoritativeReference);
        Add(command, "$resolutionEvidence", decision.ResolutionEvidence);
        Add(command, "$originatingCommitSha", decision.OriginatingCommitSha);
        Add(command, "$branch", decision.Branch);
        Add(command, "$status", (int)decision.Status);
        Add(command, "$supersedesDecisionId", NullableId(decision.SupersedesDecisionId));
        Add(command, "$version", decision.Version);
        Add(command, "$createdUtc", Timestamp(now));
        Add(command, "$updatedUtc", Timestamp(now));
        await ExecuteAsync(command, cancellationToken);
    }

    private static ContextEntryRecord ReadContextEntry(SqliteDataReader reader) => new(
        ReadGuid(reader.GetString(0)), ReadGuid(reader.GetString(1)), ReadNullableGuid(reader, 2), reader.GetString(3), reader.GetString(4), reader.GetString(5), ReadNullableString(reader, 6), ReadNullableString(reader, 7), ReadNullableString(reader, 8), ReadNullableString(reader, 9), ReadEnum<ContextStatus>(reader.GetInt32(10)), ReadNullableString(reader, 11), ReadTimestamp(reader.GetString(12)), ReadTimestamp(reader.GetString(13)), ReadNullableTimestamp(reader, 14), reader.GetInt32(15), ReadNullableString(reader, 16), ReadNullableGuid(reader, 17));

    private static McpObservedContextEntry ReadMcpObservedContextEntry(SqliteDataReader reader) => new(ReadContextEntry(reader), ReadTimestamp(reader.GetString(18)));

    private static DecisionRecord ReadDecision(SqliteDataReader reader) => new(
        ReadGuid(reader.GetString(0)), ReadGuid(reader.GetString(1)), ReadNullableGuid(reader, 2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), ReadNullableString(reader, 8), ReadNullableString(reader, 9), ReadNullableString(reader, 10), ReadEnum<DecisionStatus>(reader.GetInt32(12)), ReadNullableGuid(reader, 13), ReadNullableGuid(reader, 14), ReadTimestamp(reader.GetString(15)), ReadTimestamp(reader.GetString(16)), ReadNullableTimestamp(reader, 17), ReadNullableString(reader, 11), reader.GetInt32(18));

    private static McpObservedDecision ReadMcpObservedDecision(SqliteDataReader reader) => new(ReadDecision(reader), ReadTimestamp(reader.GetString(19)));

    private static async Task<(Guid ProjectId, Guid? RepositoryId, DecisionStatus Status, Guid? SupersededByDecisionId)?> GetDecisionScopeAsync(SqliteConnection connection, SqliteTransaction transaction, Guid id, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, "SELECT ProjectId, RepositoryId, Status, SupersededByDecisionId FROM Decisions WHERE Id = $id;");
        command.Transaction = transaction;
        Add(command, "$id", Id(id));
        await using var reader = await ExecuteReaderAsync(command, cancellationToken);
        return await ReadAsync(reader, cancellationToken)
            ? (ReadGuid(reader.GetString(0)), ReadNullableGuid(reader, 1), ReadEnum<DecisionStatus>(reader.GetInt32(2)), ReadNullableGuid(reader, 3))
            : null;
    }

    private static void ValidateContextEntry(ContextEntryDraft entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        RequireId(entry.Id, "context entry id");
        RequireId(entry.ProjectId, "project id");
        RequireOptionalId(entry.RepositoryId, "repository id");
        ValidateRequired(entry.Category, "category", StorageLimits.ShortText);
        ValidateRequired(entry.Title, "title", StorageLimits.Title);
        ValidateRequired(entry.Summary, "summary", StorageLimits.Summary);
        ValidateOptional(entry.Content, "content", StorageLimits.Content);
        ValidateOptional(entry.AuthoritativeReference, "authoritative reference", StorageLimits.Reference);
        ValidateOptional(entry.Branch, "branch", StorageLimits.ShortText);
        ValidateCommit(entry.CommitSha, "commit");
        ValidateEnum(entry.Status, "context status");
        ValidateHash(entry.ContentHash, "content hash");
        if (entry.Tier is < 1 or > 3) throw new StorageException(StorageErrorCode.Validation, "context tier is invalid.");
        ValidateOptional(entry.Objective, "objective", StorageLimits.Summary);
        RequireOptionalId(entry.PhaseId, "phase id");
    }

    private static void ValidateDecision(DecisionDraft decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        RequireId(decision.Id, "decision id");
        RequireId(decision.ProjectId, "project id");
        RequireOptionalId(decision.RepositoryId, "repository id");
        ValidateRequired(decision.Category, "category", StorageLimits.ShortText);
        ValidateRequired(decision.Component, "component", StorageLimits.ShortText);
        ValidateRequired(decision.Title, "title", StorageLimits.Title);
        ValidateRequired(decision.Decision, "decision", StorageLimits.Summary);
        ValidateRequired(decision.Rationale, "rationale", StorageLimits.Rationale);
        ValidateOptional(decision.AuthoritativeReference, "authoritative reference", StorageLimits.Reference);
        ValidateOptional(decision.ResolutionEvidence, "resolution evidence", StorageLimits.Rationale);
        ValidateCommit(decision.OriginatingCommitSha, "originating commit");
        ValidateOptional(decision.Branch, "branch", StorageLimits.ShortText);
        ValidateEnum(decision.Status, "decision status");
        RequireOptionalId(decision.SupersedesDecisionId, "superseded decision id");
        if (decision.Version < 1) throw new StorageException(StorageErrorCode.Validation, "decision version is invalid.");
        if (decision.Status == DecisionStatus.Superseded)
        {
            throw new StorageException(StorageErrorCode.Validation, "New decisions cannot start in the superseded state.");
        }

        if (decision.SupersedesDecisionId == decision.Id)
        {
            throw new StorageException(StorageErrorCode.Validation, "A decision cannot supersede itself.");
        }

        if (decision.SupersedesDecisionId is not null && string.IsNullOrWhiteSpace(decision.ResolutionEvidence))
        {
            throw new StorageException(StorageErrorCode.Validation, "Decision supersession requires resolution evidence.");
        }
    }

    public async Task<PhaseRecord> CreatePhaseAsync(PhaseDraft phase, CancellationToken cancellationToken = default)
    {
        ValidatePhase(phase, out var startedUtc, out var completedUtc);
        return await ExecuteStorageAsync(async (connection, transaction, token) =>
        {
            await EnsureProjectExistsAsync(connection, transaction, phase.ProjectId, token);
            await EnsureRepositoryScopeAsync(connection, transaction, phase.ProjectId, phase.RepositoryId, token);
            await EnsureContextEntryScopeAsync(connection, transaction, phase.ProjectId, phase.RepositoryId, phase.ContextEntryId, token);
            var now = UtcNow();
            await using var command = Command(connection, """
                INSERT INTO Phases (Id, ProjectId, RepositoryId, PhaseKey, Objective, Status, Branch, CommitSha, StartedUtc, CompletedUtc, CreatedUtc, UpdatedUtc, Version, ContextEntryId)
                VALUES ($id, $projectId, $repositoryId, $phaseKey, $objective, $status, $branch, $commitSha, $startedUtc, $completedUtc, $createdUtc, $updatedUtc, $version, $contextEntryId);
                """);
            command.Transaction = transaction;
            Add(command, "$id", Id(phase.Id));
            Add(command, "$projectId", Id(phase.ProjectId));
            Add(command, "$repositoryId", NullableId(phase.RepositoryId));
            Add(command, "$phaseKey", phase.PhaseKey);
            Add(command, "$objective", phase.Objective);
            Add(command, "$status", (int)phase.Status);
            Add(command, "$branch", phase.Branch);
            Add(command, "$commitSha", phase.CommitSha);
            Add(command, "$startedUtc", NullableTimestamp(startedUtc));
            Add(command, "$completedUtc", NullableTimestamp(completedUtc));
            Add(command, "$createdUtc", Timestamp(now));
            Add(command, "$updatedUtc", Timestamp(now));
            Add(command, "$version", phase.Version);
            Add(command, "$contextEntryId", NullableId(phase.ContextEntryId));
            await ExecuteAsync(command, token);
            return new PhaseRecord(phase.Id, phase.ProjectId, phase.RepositoryId, phase.PhaseKey, phase.Objective, phase.Status, phase.Branch, phase.CommitSha, startedUtc, completedUtc, now, now, phase.Version, phase.ContextEntryId);
        }, cancellationToken);
    }

    public async Task<PhaseRecord> UpsertPhaseAsync(PhaseDraft phase, int? expectedVersion, CancellationToken cancellationToken = default)
    {
        ValidatePhase(phase, out var startedUtc, out var completedUtc);
        if (expectedVersion is < 1)
        {
            throw new StorageException(StorageErrorCode.Validation, "Expected phase version is invalid.");
        }

        return await ExecuteTransactionalWriteAsync(async (connection, transaction, token) =>
        {
            await EnsureProjectExistsAsync(connection, transaction, phase.ProjectId, token);
            await EnsureRepositoryScopeAsync(connection, transaction, phase.ProjectId, phase.RepositoryId, token);
            await EnsureContextEntryScopeAsync(connection, transaction, phase.ProjectId, phase.RepositoryId, phase.ContextEntryId, token);
            var existing = await GetPhaseAsync(connection, transaction, phase.Id, token);
            var now = UtcNow();
            if (existing is null)
            {
                if (expectedVersion is not null)
                {
                    throw new StorageException(StorageErrorCode.Conflict, "Phase version does not match a new phase.");
                }

                await using var insert = Command(connection, """
                    INSERT INTO Phases (Id, ProjectId, RepositoryId, PhaseKey, Objective, Status, Branch, CommitSha, StartedUtc, CompletedUtc, CreatedUtc, UpdatedUtc, Version, ContextEntryId)
                    VALUES ($id, $projectId, $repositoryId, $phaseKey, $objective, $status, $branch, $commitSha, $startedUtc, $completedUtc, $createdUtc, $updatedUtc, 1, $contextEntryId);
                    """);
                insert.Transaction = transaction;
                Add(insert, "$id", Id(phase.Id));
                Add(insert, "$projectId", Id(phase.ProjectId));
                Add(insert, "$repositoryId", NullableId(phase.RepositoryId));
                Add(insert, "$phaseKey", phase.PhaseKey);
                Add(insert, "$objective", phase.Objective);
                Add(insert, "$status", (int)phase.Status);
                Add(insert, "$branch", phase.Branch);
                Add(insert, "$commitSha", phase.CommitSha);
                Add(insert, "$startedUtc", NullableTimestamp(startedUtc));
                Add(insert, "$completedUtc", NullableTimestamp(completedUtc));
                Add(insert, "$createdUtc", Timestamp(now));
                Add(insert, "$updatedUtc", Timestamp(now));
                Add(insert, "$contextEntryId", NullableId(phase.ContextEntryId));
                await ExecuteAsync(insert, token);
                return new PhaseRecord(phase.Id, phase.ProjectId, phase.RepositoryId, phase.PhaseKey, phase.Objective, phase.Status, phase.Branch, phase.CommitSha, startedUtc, completedUtc, now, now, 1, phase.ContextEntryId);
            }

            if (existing.ProjectId != phase.ProjectId || existing.RepositoryId != phase.RepositoryId || existing.Version != expectedVersion)
            {
                throw new StorageException(StorageErrorCode.Conflict, "Phase version or scope does not match the current record.");
            }

            await using var update = Command(connection, """
                UPDATE Phases
                SET PhaseKey = $phaseKey, Objective = $objective, Status = $status, Branch = $branch, CommitSha = $commitSha,
                    StartedUtc = $startedUtc, CompletedUtc = $completedUtc, UpdatedUtc = $updatedUtc, Version = $version, ContextEntryId = $contextEntryId
                WHERE Id = $id AND Version = $expectedVersion;
                """);
            update.Transaction = transaction;
            Add(update, "$phaseKey", phase.PhaseKey);
            Add(update, "$objective", phase.Objective);
            Add(update, "$status", (int)phase.Status);
            Add(update, "$branch", phase.Branch);
            Add(update, "$commitSha", phase.CommitSha);
            Add(update, "$startedUtc", NullableTimestamp(startedUtc));
            Add(update, "$completedUtc", NullableTimestamp(completedUtc));
            Add(update, "$updatedUtc", Timestamp(now));
            Add(update, "$version", existing.Version + 1);
            Add(update, "$contextEntryId", NullableId(phase.ContextEntryId));
            Add(update, "$id", Id(phase.Id));
            Add(update, "$expectedVersion", existing.Version);
            if (await ExecuteCountAsync(update, token) != 1)
            {
                throw new StorageException(StorageErrorCode.Conflict, "Phase changed while it was being updated.");
            }

            return new PhaseRecord(phase.Id, phase.ProjectId, phase.RepositoryId, phase.PhaseKey, phase.Objective, phase.Status, phase.Branch, phase.CommitSha, startedUtc, completedUtc, existing.CreatedUtc, now, existing.Version + 1, phase.ContextEntryId);
        }, cancellationToken);
    }

    public async Task<PhaseRecord?> GetPhaseAsync(Guid id, CancellationToken cancellationToken = default)
    {
        RequireId(id, "phase id");
        return await ExecuteStorageAsync(async (connection, transaction, token) =>
        {
            await using var command = Command(connection, "SELECT Id, ProjectId, RepositoryId, PhaseKey, Objective, Status, Branch, CommitSha, StartedUtc, CompletedUtc, CreatedUtc, UpdatedUtc, Version, ContextEntryId FROM Phases WHERE Id = $id;");
            command.Transaction = transaction;
            Add(command, "$id", Id(id));
            await using var reader = await ExecuteReaderAsync(command, token);
            return await ReadAsync(reader, token) ? ReadPhase(reader) : null;
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<PhaseRecord>> ListPhasesAsync(PhaseQuery query, CancellationToken cancellationToken = default)
    {
        ValidatePhaseQuery(query);
        return await ExecuteStorageAsync(async (connection, transaction, token) =>
        {
            var sql = new StringBuilder("SELECT Id, ProjectId, RepositoryId, PhaseKey, Objective, Status, Branch, CommitSha, StartedUtc, CompletedUtc, CreatedUtc, UpdatedUtc, Version, ContextEntryId FROM Phases WHERE ProjectId = $projectId");
            if (query.RepositoryId is not null) sql.Append(" AND RepositoryId = $repositoryId");
            if (query.RepositoryIsNull) sql.Append(" AND RepositoryId IS NULL");
            if (query.Status is not null) sql.Append(" AND Status = $status");
            if (query.Branch is not null) sql.Append(" AND Branch = $branch");
            if (query.BranchIsNull) sql.Append(" AND Branch IS NULL");
            if (query.CommitSha is not null) sql.Append(" AND CommitSha = $commitSha");
            sql.Append(" ORDER BY CASE Status WHEN 1 THEN 0 ELSE 1 END, UpdatedUtc DESC, Id ASC LIMIT $maxResults;");
            await using var command = Command(connection, sql.ToString());
            command.Transaction = transaction;
            Add(command, "$projectId", Id(query.ProjectId));
            if (query.RepositoryId is { } repositoryId) Add(command, "$repositoryId", Id(repositoryId));
            if (query.Status is { } status) Add(command, "$status", (int)status);
            if (query.Branch is not null) Add(command, "$branch", query.Branch);
            if (query.CommitSha is not null) Add(command, "$commitSha", query.CommitSha);
            Add(command, "$maxResults", query.MaxResults);
            return await ReadManyAsync(command, ReadPhase, token);
        }, cancellationToken);
    }

    public async Task<ArtifactRecord> CreateArtifactAsync(ArtifactDraft artifact, CancellationToken cancellationToken = default)
    {
        ValidateArtifact(artifact);
        return await ExecuteStorageAsync(async (connection, transaction, token) =>
        {
            await EnsureProjectExistsAsync(connection, transaction, artifact.ProjectId, token);
            await EnsureRepositoryScopeAsync(connection, transaction, artifact.ProjectId, artifact.RepositoryId, token);
            var now = UtcNow();
            await using var command = Command(connection, """
                INSERT INTO Artifacts (Id, ProjectId, RepositoryId, ArtifactType, Title, Reference, ContentHash, SizeBytes, Branch, CommitSha, CreatedUtc, UpdatedUtc)
                VALUES ($id, $projectId, $repositoryId, $artifactType, $title, $reference, $contentHash, $sizeBytes, $branch, $commitSha, $createdUtc, $updatedUtc);
                """);
            command.Transaction = transaction;
            Add(command, "$id", Id(artifact.Id));
            Add(command, "$projectId", Id(artifact.ProjectId));
            Add(command, "$repositoryId", NullableId(artifact.RepositoryId));
            Add(command, "$artifactType", artifact.ArtifactType);
            Add(command, "$title", artifact.Title);
            Add(command, "$reference", artifact.Reference);
            Add(command, "$contentHash", artifact.ContentHash);
            Add(command, "$sizeBytes", artifact.SizeBytes);
            Add(command, "$branch", artifact.Branch);
            Add(command, "$commitSha", artifact.CommitSha);
            Add(command, "$createdUtc", Timestamp(now));
            Add(command, "$updatedUtc", Timestamp(now));
            await ExecuteAsync(command, token);
            return new ArtifactRecord(artifact.Id, artifact.ProjectId, artifact.RepositoryId, artifact.ArtifactType, artifact.Title, artifact.Reference, artifact.ContentHash, artifact.SizeBytes, artifact.Branch, artifact.CommitSha, now, now);
        }, cancellationToken);
    }

    public async Task<ArtifactRecord?> GetArtifactAsync(Guid id, CancellationToken cancellationToken = default)
    {
        RequireId(id, "artifact id");
        return await ExecuteStorageAsync(async (connection, transaction, token) =>
        {
            await using var command = Command(connection, "SELECT Id, ProjectId, RepositoryId, ArtifactType, Title, Reference, ContentHash, SizeBytes, Branch, CommitSha, CreatedUtc, UpdatedUtc FROM Artifacts WHERE Id = $id;");
            command.Transaction = transaction;
            Add(command, "$id", Id(id));
            await using var reader = await ExecuteReaderAsync(command, token);
            return await ReadAsync(reader, token) ? ReadArtifact(reader) : null;
        }, cancellationToken);
    }

    public async Task<TestRunRecord> CreateTestRunAsync(TestRunDraft testRun, CancellationToken cancellationToken = default)
    {
        ValidateTestRun(testRun, out var startedUtc, out var completedUtc);
        return await ExecuteStorageAsync(async (connection, transaction, token) =>
        {
            await EnsureProjectExistsAsync(connection, transaction, testRun.ProjectId, token);
            await EnsureRepositoryScopeAsync(connection, transaction, testRun.ProjectId, testRun.RepositoryId, token);
            if (testRun.ArtifactId is { } artifactId)
            {
                var artifactScope = await GetArtifactScopeAsync(connection, transaction, artifactId, token)
                    ?? throw new StorageException(StorageErrorCode.NotFound, "Test artifact was not found.");
                if (artifactScope.ProjectId != testRun.ProjectId || artifactScope.RepositoryId != testRun.RepositoryId)
                {
                    throw new StorageException(StorageErrorCode.CrossProjectReference, "Test artifact must remain within the same project and repository scope.");
                }
            }

            var now = UtcNow();
            await using var command = Command(connection, """
                INSERT INTO TestRuns (Id, ProjectId, RepositoryId, CommandText, Branch, CommitSha, Passed, Failed, Skipped, Total, Status, ArtifactId, ArtifactReference, StartedUtc, CompletedUtc, CreatedUtc, Name, Summary, Evidence, ObservedUtc)
                VALUES ($id, $projectId, $repositoryId, $commandText, $branch, $commitSha, $passed, $failed, $skipped, $total, $status, $artifactId, $artifactReference, $startedUtc, $completedUtc, $createdUtc, $name, $summary, $evidence, $observedUtc);
                """);
            command.Transaction = transaction;
            Add(command, "$id", Id(testRun.Id));
            Add(command, "$projectId", Id(testRun.ProjectId));
            Add(command, "$repositoryId", NullableId(testRun.RepositoryId));
            Add(command, "$commandText", testRun.Command);
            Add(command, "$branch", testRun.Branch);
            Add(command, "$commitSha", testRun.CommitSha);
            Add(command, "$passed", testRun.Passed);
            Add(command, "$failed", testRun.Failed);
            Add(command, "$skipped", testRun.Skipped);
            Add(command, "$total", testRun.Total);
            Add(command, "$status", (int)testRun.Status);
            Add(command, "$artifactId", NullableId(testRun.ArtifactId));
            Add(command, "$artifactReference", testRun.ArtifactReference);
            Add(command, "$startedUtc", NullableTimestamp(startedUtc));
            Add(command, "$completedUtc", NullableTimestamp(completedUtc));
            Add(command, "$createdUtc", Timestamp(now));
            Add(command, "$name", testRun.Name);
            Add(command, "$summary", testRun.Summary);
            Add(command, "$evidence", testRun.Evidence);
            Add(command, "$observedUtc", NullableTimestamp(testRun.ObservedUtc));
            await ExecuteAsync(command, token);
            return new TestRunRecord(testRun.Id, testRun.ProjectId, testRun.RepositoryId, testRun.Command, testRun.Branch, testRun.CommitSha, testRun.Passed, testRun.Failed, testRun.Skipped, testRun.Total, testRun.Status, testRun.ArtifactId, testRun.ArtifactReference, startedUtc, completedUtc, now, testRun.Name, testRun.Summary, testRun.Evidence, testRun.ObservedUtc);
        }, cancellationToken);
    }

    public async Task<TestRunRecord?> GetTestRunAsync(Guid id, CancellationToken cancellationToken = default)
    {
        RequireId(id, "test run id");
        return await ExecuteStorageAsync(async (connection, transaction, token) =>
        {
            await using var command = Command(connection, "SELECT Id, ProjectId, RepositoryId, CommandText, Branch, CommitSha, Passed, Failed, Skipped, Total, Status, ArtifactId, ArtifactReference, StartedUtc, CompletedUtc, CreatedUtc, Name, Summary, Evidence, ObservedUtc FROM TestRuns WHERE Id = $id;");
            command.Transaction = transaction;
            Add(command, "$id", Id(id));
            await using var reader = await ExecuteReaderAsync(command, token);
            return await ReadAsync(reader, token) ? ReadTestRun(reader) : null;
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<TestRunRecord>> ListTestRunsAsync(TestRunQuery query, CancellationToken cancellationToken = default)
    {
        ValidateTestRunQuery(query);
        return await ExecuteStorageAsync(async (connection, transaction, token) =>
        {
            var sql = new StringBuilder("SELECT Id, ProjectId, RepositoryId, CommandText, Branch, CommitSha, Passed, Failed, Skipped, Total, Status, ArtifactId, ArtifactReference, StartedUtc, CompletedUtc, CreatedUtc, Name, Summary, Evidence, ObservedUtc FROM TestRuns WHERE ProjectId = $projectId");
            if (query.RepositoryId is not null) sql.Append(" AND RepositoryId = $repositoryId");
            if (query.RepositoryIsNull) sql.Append(" AND RepositoryId IS NULL");
            if (query.Branch is not null) sql.Append(" AND Branch = $branch");
            if (query.CommitSha is not null) sql.Append(" AND CommitSha = $commitSha");
            if (query.Status is not null) sql.Append(" AND Status = $status");
            if (query.CompletedOnly) sql.Append(" AND CompletedUtc IS NOT NULL");
            sql.Append(" ORDER BY COALESCE(CompletedUtc, CreatedUtc) DESC, Id ASC LIMIT $maxResults;");
            await using var command = Command(connection, sql.ToString());
            command.Transaction = transaction;
            Add(command, "$projectId", Id(query.ProjectId));
            if (query.RepositoryId is { } repositoryId) Add(command, "$repositoryId", Id(repositoryId));
            if (query.Branch is not null) Add(command, "$branch", query.Branch);
            if (query.CommitSha is not null) Add(command, "$commitSha", query.CommitSha);
            if (query.Status is { } status) Add(command, "$status", (int)status);
            Add(command, "$maxResults", query.MaxResults);
            return await ReadManyAsync(command, ReadTestRun, token);
        }, cancellationToken);
    }

    private static PhaseRecord ReadPhase(SqliteDataReader reader) => new(
        ReadGuid(reader.GetString(0)), ReadGuid(reader.GetString(1)), ReadNullableGuid(reader, 2), reader.GetString(3), reader.GetString(4), ReadEnum<PhaseStatus>(reader.GetInt32(5)), ReadNullableString(reader, 6), ReadNullableString(reader, 7), ReadNullableTimestamp(reader, 8), ReadNullableTimestamp(reader, 9), ReadTimestamp(reader.GetString(10)), ReadTimestamp(reader.GetString(11)), reader.GetInt32(12), ReadNullableGuid(reader, 13));

    private static ArtifactRecord ReadArtifact(SqliteDataReader reader) => new(
        ReadGuid(reader.GetString(0)), ReadGuid(reader.GetString(1)), ReadNullableGuid(reader, 2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetInt64(7), ReadNullableString(reader, 8), ReadNullableString(reader, 9), ReadTimestamp(reader.GetString(10)), ReadTimestamp(reader.GetString(11)));

    private static TestRunRecord ReadTestRun(SqliteDataReader reader) => new(
        ReadGuid(reader.GetString(0)), ReadGuid(reader.GetString(1)), ReadNullableGuid(reader, 2), reader.GetString(3), ReadNullableString(reader, 4), ReadNullableString(reader, 5), reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8), reader.GetInt64(9), ReadEnum<TestRunStatus>(reader.GetInt32(10)), ReadNullableGuid(reader, 11), ReadNullableString(reader, 12), ReadNullableTimestamp(reader, 13), ReadNullableTimestamp(reader, 14), ReadTimestamp(reader.GetString(15)), ReadNullableString(reader, 16), ReadNullableString(reader, 17), ReadNullableString(reader, 18), ReadNullableTimestamp(reader, 19));

    private static async Task<(Guid ProjectId, Guid? RepositoryId)?> GetArtifactScopeAsync(SqliteConnection connection, SqliteTransaction? transaction, Guid id, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, "SELECT ProjectId, RepositoryId FROM Artifacts WHERE Id = $id;");
        command.Transaction = transaction;
        Add(command, "$id", Id(id));
        await using var reader = await ExecuteReaderAsync(command, cancellationToken);
        return await ReadAsync(reader, cancellationToken) ? (ReadGuid(reader.GetString(0)), ReadNullableGuid(reader, 1)) : null;
    }

    private static void ValidatePhase(PhaseDraft phase, out DateTimeOffset? startedUtc, out DateTimeOffset? completedUtc)
    {
        ArgumentNullException.ThrowIfNull(phase);
        RequireId(phase.Id, "phase id");
        RequireId(phase.ProjectId, "project id");
        RequireOptionalId(phase.RepositoryId, "repository id");
        ValidateRequired(phase.PhaseKey, "phase key", StorageLimits.Name);
        ValidateRequired(phase.Objective, "objective", StorageLimits.Summary);
        ValidateEnum(phase.Status, "phase status");
        ValidateOptional(phase.Branch, "branch", StorageLimits.ShortText);
        ValidateCommit(phase.CommitSha, "commit");
        startedUtc = NormalizeTimestamp(phase.StartedUtc, "started timestamp");
        completedUtc = NormalizeTimestamp(phase.CompletedUtc, "completed timestamp");
        if (startedUtc is not null && completedUtc is not null && completedUtc < startedUtc)
        {
            throw new StorageException(StorageErrorCode.Validation, "Completed timestamp cannot precede started timestamp.");
        }

        if (phase.Version < 1) throw new StorageException(StorageErrorCode.Validation, "phase version is invalid.");
        RequireOptionalId(phase.ContextEntryId, "phase context entry id");
    }

    private static void ValidateArtifact(ArtifactDraft artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        RequireId(artifact.Id, "artifact id");
        RequireId(artifact.ProjectId, "project id");
        RequireOptionalId(artifact.RepositoryId, "repository id");
        ValidateRequired(artifact.ArtifactType, "artifact type", StorageLimits.ShortText);
        ValidateRequired(artifact.Title, "title", StorageLimits.Title);
        ValidateArtifactReference(artifact.Reference);
        ValidateHash(artifact.ContentHash, "content hash");
        if (artifact.SizeBytes < 0)
        {
            throw new StorageException(StorageErrorCode.Validation, "Artifact size cannot be negative.");
        }

        ValidateOptional(artifact.Branch, "branch", StorageLimits.ShortText);
        ValidateCommit(artifact.CommitSha, "commit");
    }

    private static void ValidateTestRun(TestRunDraft testRun, out DateTimeOffset? startedUtc, out DateTimeOffset? completedUtc)
    {
        ArgumentNullException.ThrowIfNull(testRun);
        RequireId(testRun.Id, "test run id");
        RequireId(testRun.ProjectId, "project id");
        RequireOptionalId(testRun.RepositoryId, "repository id");
        ValidateRequired(testRun.Command, "command", StorageLimits.Command);
        ValidateOptional(testRun.Branch, "branch", StorageLimits.ShortText);
        ValidateCommit(testRun.CommitSha, "commit");
        RequireOptionalId(testRun.ArtifactId, "artifact id");
        ValidateOptional(testRun.ArtifactReference, "artifact reference", StorageLimits.Reference);
        ValidateOptional(testRun.Name, "test name", StorageLimits.Title);
        ValidateOptional(testRun.Summary, "test summary", StorageLimits.Summary);
        ValidateOptional(testRun.Evidence, "test evidence", StorageLimits.Content);
        _ = NormalizeTimestamp(testRun.ObservedUtc, "observed timestamp");
        ValidateEnum(testRun.Status, "test status");
        if (testRun.Passed is < 0 or > 1_000_000_000
            || testRun.Failed is < 0 or > 1_000_000_000
            || testRun.Skipped is < 0 or > 1_000_000_000
            || testRun.Total is < 0 or > 3_000_000_000)
        {
            throw new StorageException(StorageErrorCode.Validation, "Test counts are invalid.");
        }

        var total = checked(testRun.Passed + testRun.Failed + testRun.Skipped);
        if (testRun.Total != total)
        {
            throw new StorageException(StorageErrorCode.Validation, "Test counts are invalid.");
        }

        if ((testRun.Status == TestRunStatus.Passed && (testRun.Failed != 0 || testRun.Passed == 0))
            || (testRun.Status == TestRunStatus.Failed && testRun.Failed == 0)
            || (testRun.Status == TestRunStatus.Skipped && (testRun.Passed != 0 || testRun.Failed != 0 || testRun.Skipped == 0))
            || ((testRun.Status is TestRunStatus.Blocked or TestRunStatus.Unknown) && testRun.Total != 0))
        {
            throw new StorageException(StorageErrorCode.Validation, "Test status does not match the summary counts.");
        }

        startedUtc = NormalizeTimestamp(testRun.StartedUtc, "started timestamp");
        completedUtc = NormalizeTimestamp(testRun.CompletedUtc, "completed timestamp");
        if (startedUtc is not null && completedUtc is not null && completedUtc < startedUtc)
        {
            throw new StorageException(StorageErrorCode.Validation, "Completed timestamp cannot precede started timestamp.");
        }
    }

    public async Task<FindingRecord> CreateFindingAsync(FindingDraft finding, CancellationToken cancellationToken = default)
    {
        ValidateFinding(finding);
        return await ExecuteStorageAsync(async (connection, transaction, token) =>
        {
            await EnsureProjectExistsAsync(connection, transaction, finding.ProjectId, token);
            await EnsureRepositoryScopeAsync(connection, transaction, finding.ProjectId, finding.RepositoryId, token);
            var now = UtcNow();
            await InsertFindingAsync(connection, transaction, finding.Id, 1, finding.ProjectId, finding.RepositoryId, finding.Category, finding.Title, finding.Description, finding.Severity, finding.Status, finding.Resolution, finding.ResolutionEvidence, finding.Branch, finding.CommitSha, finding.AuthoritativeReference, finding.Component, finding.Location, finding.Remediation, now, token);
            return new FindingRecord(finding.Id, 1, finding.ProjectId, finding.RepositoryId, finding.Category, finding.Title, finding.Description, finding.Severity, finding.Status, finding.Resolution, finding.ResolutionEvidence, now, now, finding.Branch, finding.CommitSha, finding.AuthoritativeReference, finding.Component, finding.Location, finding.Remediation);
        }, cancellationToken);
    }

    public async Task<FindingRecord?> GetFindingAsync(Guid id, CancellationToken cancellationToken = default)
    {
        RequireId(id, "finding id");
        return await ExecuteStorageAsync((connection, transaction, token) => GetCurrentFindingAsync(connection, transaction, id, token), cancellationToken);
    }

    public async Task<IReadOnlyList<FindingRecord>> ListFindingHistoryAsync(Guid id, int maxResults = 50, CancellationToken cancellationToken = default)
    {
        RequireId(id, "finding id");
        ValidateMaxResults(maxResults);
        return await ExecuteStorageAsync(async (connection, transaction, token) =>
        {
            await using var command = Command(connection, "SELECT Id, Revision, ProjectId, RepositoryId, Category, Title, Description, Severity, Status, Resolution, ResolutionEvidence, Branch, CommitSha, AuthoritativeReference, CreatedUtc, UpdatedUtc, Component, Location, Remediation FROM Findings WHERE Id = $id ORDER BY Revision DESC LIMIT $maxResults;");
            command.Transaction = transaction;
            Add(command, "$id", Id(id));
            Add(command, "$maxResults", maxResults);
            await using var reader = await ExecuteReaderAsync(command, token);
            var history = new List<FindingRecord>();
            while (await ReadAsync(reader, token))
            {
                history.Add(ReadFinding(reader));
            }

            return history;
        }, cancellationToken);
    }

    public async Task<FindingRecord> ReviseFindingAsync(FindingRevision revision, CancellationToken cancellationToken = default)
    {
        ValidateFindingRevision(revision);
        return await ExecuteTransactionalWriteAsync(async (connection, transaction, token) =>
        {
            var current = await GetCurrentFindingAsync(connection, transaction, revision.Id, token)
                ?? throw new StorageException(StorageErrorCode.NotFound, "Finding was not found.");
            if (current.Revision != revision.ExpectedRevision)
            {
                throw new StorageException(StorageErrorCode.Conflict, "Finding revision does not match the current record.");
            }

            var now = UtcNow();
            var branch = revision.Branch ?? current.Branch;
            var commitSha = revision.CommitSha ?? current.CommitSha;
            var authoritativeReference = revision.AuthoritativeReference ?? current.AuthoritativeReference;
            var component = revision.Component ?? current.Component;
            var location = revision.Location ?? current.Location;
            var remediation = revision.Remediation ?? current.Remediation;
            await InsertFindingAsync(connection, transaction, revision.Id, current.Revision + 1, current.ProjectId, current.RepositoryId, revision.Category, revision.Title, revision.Description, revision.Severity, revision.Status, revision.Resolution, revision.ResolutionEvidence, branch, commitSha, authoritativeReference, component, location, remediation, now, token);
            return new FindingRecord(revision.Id, current.Revision + 1, current.ProjectId, current.RepositoryId, revision.Category, revision.Title, revision.Description, revision.Severity, revision.Status, revision.Resolution, revision.ResolutionEvidence, now, now, branch, commitSha, authoritativeReference, component, location, remediation);
        }, cancellationToken);
    }

    public async Task<HandoffRecord> CreateHandoffAsync(HandoffDraft handoff, CancellationToken cancellationToken = default)
    {
        ValidateHandoff(handoff);
        return await ExecuteStorageAsync(async (connection, transaction, token) =>
        {
            await EnsureProjectExistsAsync(connection, transaction, handoff.ProjectId, token);
            await EnsureRepositoryScopeAsync(connection, transaction, handoff.ProjectId, handoff.RepositoryId, token);
            await EnsurePhaseScopeAsync(connection, transaction, handoff.ProjectId, handoff.RepositoryId, handoff.PhaseId, token);
            var now = UtcNow();
            var filesJson = JsonSerializer.Serialize(handoff.RelevantFiles);
            var artifactsJson = JsonSerializer.Serialize(handoff.RelevantArtifacts);
            await using var command = Command(connection, """
                INSERT INTO Handoffs (Id, ProjectId, RepositoryId, Branch, CommitSha, Objective, CompletedWork, ActiveWork, ActiveBlockers, ImportantDecisions, LatestTestStatus, UnresolvedFindings, RelevantFilesJson, RelevantArtifactsJson, RecommendedNextAction, CreatedUtc, UpdatedUtc, PhaseId)
                VALUES ($id, $projectId, $repositoryId, $branch, $commitSha, $objective, $completedWork, $activeWork, $activeBlockers, $importantDecisions, $latestTestStatus, $unresolvedFindings, $relevantFilesJson, $relevantArtifactsJson, $recommendedNextAction, $createdUtc, $updatedUtc, $phaseId);
                """);
            command.Transaction = transaction;
            Add(command, "$id", Id(handoff.Id));
            Add(command, "$projectId", Id(handoff.ProjectId));
            Add(command, "$repositoryId", NullableId(handoff.RepositoryId));
            Add(command, "$branch", handoff.Branch);
            Add(command, "$commitSha", handoff.CommitSha);
            Add(command, "$objective", handoff.Objective);
            Add(command, "$completedWork", handoff.CompletedWork);
            Add(command, "$activeWork", handoff.ActiveWork);
            Add(command, "$activeBlockers", handoff.ActiveBlockers);
            Add(command, "$importantDecisions", handoff.ImportantDecisions);
            Add(command, "$latestTestStatus", handoff.LatestTestStatus);
            Add(command, "$unresolvedFindings", handoff.UnresolvedFindings);
            Add(command, "$relevantFilesJson", filesJson);
            Add(command, "$relevantArtifactsJson", artifactsJson);
            Add(command, "$recommendedNextAction", handoff.RecommendedNextAction);
            Add(command, "$createdUtc", Timestamp(now));
            Add(command, "$updatedUtc", Timestamp(now));
            Add(command, "$phaseId", NullableId(handoff.PhaseId));
            await ExecuteAsync(command, token);
            return new HandoffRecord(handoff.Id, handoff.ProjectId, handoff.RepositoryId, handoff.Branch, handoff.CommitSha, handoff.Objective, handoff.CompletedWork, handoff.ActiveWork, handoff.ActiveBlockers, handoff.ImportantDecisions, handoff.LatestTestStatus, handoff.UnresolvedFindings, handoff.RelevantFiles, handoff.RelevantArtifacts, handoff.RecommendedNextAction, now, now, handoff.PhaseId);
        }, cancellationToken);
    }

    public async Task<HandoffRecord?> GetHandoffAsync(Guid id, CancellationToken cancellationToken = default)
    {
        RequireId(id, "handoff id");
        return await ExecuteStorageAsync(async (connection, transaction, token) =>
        {
            await using var command = Command(connection, "SELECT Id, ProjectId, RepositoryId, Branch, CommitSha, Objective, CompletedWork, ActiveWork, ActiveBlockers, ImportantDecisions, LatestTestStatus, UnresolvedFindings, RelevantFilesJson, RelevantArtifactsJson, RecommendedNextAction, CreatedUtc, UpdatedUtc, PhaseId FROM Handoffs WHERE Id = $id;");
            command.Transaction = transaction;
            Add(command, "$id", Id(id));
            await using var reader = await ExecuteReaderAsync(command, token);
            return await ReadAsync(reader, token) ? ReadHandoff(reader) : null;
        }, cancellationToken);
    }

    public async Task<HandoffRecord?> GetLatestHandoffAsync(Guid projectId, Guid? repositoryId, CancellationToken cancellationToken = default)
    {
        RequireId(projectId, "project id");
        RequireOptionalId(repositoryId, "repository id");
        return await ExecuteStorageAsync(async (connection, transaction, token) =>
        {
            await using var command = Command(connection, "SELECT Id, ProjectId, RepositoryId, Branch, CommitSha, Objective, CompletedWork, ActiveWork, ActiveBlockers, ImportantDecisions, LatestTestStatus, UnresolvedFindings, RelevantFilesJson, RelevantArtifactsJson, RecommendedNextAction, CreatedUtc, UpdatedUtc, PhaseId FROM Handoffs WHERE ProjectId = $projectId AND ((RepositoryId IS NULL AND $repositoryId IS NULL) OR RepositoryId = $repositoryId) ORDER BY CreatedUtc DESC, Id ASC LIMIT 1;");
            command.Transaction = transaction;
            Add(command, "$projectId", Id(projectId));
            Add(command, "$repositoryId", NullableId(repositoryId));
            await using var reader = await ExecuteReaderAsync(command, token);
            return await ReadAsync(reader, token) ? ReadHandoff(reader) : null;
        }, cancellationToken);
    }

    public async Task<McpObservationRecord> CreateMcpObservationAsync(McpObservationDraft observation, CancellationToken cancellationToken = default)
    {
        ValidateMcpObservation(observation);
        return await ExecuteStorageAsync(async (connection, transaction, token) =>
        {
            await EnsureProjectExistsAsync(connection, transaction, observation.ProjectId, token);
            await EnsureRepositoryScopeAsync(connection, transaction, observation.ProjectId, observation.RepositoryId, token);
            if (observation.SourceReference is { } sourceReference)
            {
                await EnsureProjectExistsAsync(connection, transaction, sourceReference.ProjectId, token);
                await EnsureRepositoryScopeAsync(connection, transaction, sourceReference.ProjectId, sourceReference.RepositoryId, token);
            }

            var recordScope = await GetMcpRecordScopeAsync(connection, transaction, observation.RecordKind, observation.RecordId, observation.Revision, token)
                ?? throw new StorageException(StorageErrorCode.NotFound, "Observed record was not found.");
            if (recordScope.ProjectId != observation.ProjectId || recordScope.RepositoryId != observation.RepositoryId)
            {
                throw new StorageException(StorageErrorCode.CrossProjectReference, "Observation must remain within the record scope.");
            }

            await using var command = Command(connection, """
                INSERT INTO McpRecordObservations (RecordKind, RecordId, Revision, ProjectId, RepositoryId, SourceKind, SourceObservedUtc, SourceReferenceKind, SourceReferenceRepositoryId, SourceReferenceId, SourceReferenceRelativePath, SourceReferenceUri, SourceReferenceHash, CanonicalRoot, Branch, Head, WorkingTreeFingerprint, SnapshotObservedUtc, Completeness, RecordObservedUtc)
                VALUES ($recordKind, $recordId, $revision, $projectId, $repositoryId, $sourceKind, $sourceObservedUtc, $sourceReferenceKind, $sourceReferenceRepositoryId, $sourceReferenceId, $sourceReferenceRelativePath, $sourceReferenceUri, $sourceReferenceHash, $canonicalRoot, $branch, $head, $workingTreeFingerprint, $snapshotObservedUtc, $completeness, $recordObservedUtc);
                """);
            command.Transaction = transaction;
            Add(command, "$recordKind", (int)observation.RecordKind);
            Add(command, "$recordId", Id(observation.RecordId));
            Add(command, "$revision", observation.Revision);
            Add(command, "$projectId", Id(observation.ProjectId));
            Add(command, "$repositoryId", NullableId(observation.RepositoryId));
            Add(command, "$sourceKind", observation.SourceKind);
            Add(command, "$sourceObservedUtc", Timestamp(observation.SourceObservedUtc));
            Add(command, "$sourceReferenceKind", observation.SourceReference?.Kind);
            Add(command, "$sourceReferenceRepositoryId", NullableId(observation.SourceReference?.RepositoryId));
            Add(command, "$sourceReferenceId", NullableId(observation.SourceReference?.Id));
            Add(command, "$sourceReferenceRelativePath", observation.SourceReference?.RelativePath);
            Add(command, "$sourceReferenceUri", observation.SourceReference?.Uri);
            Add(command, "$sourceReferenceHash", observation.SourceReference?.Hash);
            Add(command, "$canonicalRoot", observation.CanonicalRoot);
            Add(command, "$branch", observation.Branch);
            Add(command, "$head", observation.Head);
            Add(command, "$workingTreeFingerprint", observation.WorkingTreeFingerprint);
            Add(command, "$snapshotObservedUtc", Timestamp(observation.SnapshotObservedUtc));
            Add(command, "$completeness", observation.Completeness);
            Add(command, "$recordObservedUtc", Timestamp(observation.RecordObservedUtc));
            await ExecuteAsync(command, token);
            return ToMcpObservationRecord(observation);
        }, cancellationToken);
    }

    public async Task<McpObservationRecord?> GetMcpObservationAsync(McpRecordKind recordKind, Guid recordId, int revision = 1, CancellationToken cancellationToken = default)
    {
        ValidateMcpRecordKey(recordKind, recordId, revision);
        return await ExecuteStorageAsync(async (connection, transaction, token) =>
        {
            await using var command = Command(connection, "SELECT RecordKind, RecordId, Revision, ProjectId, RepositoryId, SourceKind, SourceObservedUtc, SourceReferenceKind, SourceReferenceRepositoryId, SourceReferenceId, SourceReferenceRelativePath, SourceReferenceUri, SourceReferenceHash, CanonicalRoot, Branch, Head, WorkingTreeFingerprint, SnapshotObservedUtc, Completeness, RecordObservedUtc FROM McpRecordObservations WHERE RecordKind = $recordKind AND RecordId = $recordId AND Revision = $revision;");
            command.Transaction = transaction;
            Add(command, "$recordKind", (int)recordKind);
            Add(command, "$recordId", Id(recordId));
            Add(command, "$revision", revision);
            await using var reader = await ExecuteReaderAsync(command, token);
            return await ReadAsync(reader, token) ? ReadMcpObservation(reader) : null;
        }, cancellationToken);
    }

    public async Task<McpReferenceRecord> CreateMcpReferenceAsync(McpReferenceDraft reference, CancellationToken cancellationToken = default)
    {
        ValidateMcpReference(reference);
        return await ExecuteStorageAsync(async (connection, transaction, token) =>
        {
            await EnsureProjectExistsAsync(connection, transaction, reference.Reference.ProjectId, token);
            await EnsureRepositoryScopeAsync(connection, transaction, reference.Reference.ProjectId, reference.Reference.RepositoryId, token);
            var observation = await GetMcpObservationAsync(connection, transaction, reference.RecordKind, reference.RecordId, reference.Revision, token);
            if (observation is null)
            {
                throw new StorageException(StorageErrorCode.NotFound, "Record observation was not found.");
            }

            if (!MatchesMcpScope(observation.ProjectId, observation.RepositoryId, reference.Reference.ProjectId, reference.Reference.RepositoryId))
            {
                throw new StorageException(StorageErrorCode.CrossProjectReference, "Record reference must remain within the observation scope.");
            }

            await using var command = Command(connection, """
                INSERT INTO McpRecordReferences (RecordKind, RecordId, Revision, Role, Ordinal, ReferencedProjectId, ReferencedRepositoryId, Kind, ReferenceId, RelativePath, Uri, Hash)
                VALUES ($recordKind, $recordId, $revision, $role, $ordinal, $referencedProjectId, $referencedRepositoryId, $kind, $referenceId, $relativePath, $uri, $hash);
                """);
            command.Transaction = transaction;
            Add(command, "$recordKind", (int)reference.RecordKind);
            Add(command, "$recordId", Id(reference.RecordId));
            Add(command, "$revision", reference.Revision);
            Add(command, "$role", (int)reference.Role);
            Add(command, "$ordinal", reference.Ordinal);
            Add(command, "$referencedProjectId", Id(reference.Reference.ProjectId));
            Add(command, "$referencedRepositoryId", NullableId(reference.Reference.RepositoryId));
            Add(command, "$kind", reference.Reference.Kind);
            Add(command, "$referenceId", NullableId(reference.Reference.Id));
            Add(command, "$relativePath", reference.Reference.RelativePath);
            Add(command, "$uri", reference.Reference.Uri);
            Add(command, "$hash", reference.Reference.Hash);
            await ExecuteAsync(command, token);
            return new McpReferenceRecord(reference.RecordKind, reference.RecordId, reference.Revision, reference.Role, reference.Ordinal, reference.Reference);
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<McpReferenceRecord>> ListMcpReferencesAsync(McpRecordKind recordKind, Guid recordId, int revision = 1, CancellationToken cancellationToken = default)
    {
        ValidateMcpRecordKey(recordKind, recordId, revision);
        return await ExecuteStorageAsync(async (connection, transaction, token) =>
        {
            await using var command = Command(connection, "SELECT RecordKind, RecordId, Revision, Role, Ordinal, ReferencedProjectId, ReferencedRepositoryId, Kind, ReferenceId, RelativePath, Uri, Hash FROM McpRecordReferences WHERE RecordKind = $recordKind AND RecordId = $recordId AND Revision = $revision ORDER BY Role ASC, Ordinal ASC;");
            command.Transaction = transaction;
            Add(command, "$recordKind", (int)recordKind);
            Add(command, "$recordId", Id(recordId));
            Add(command, "$revision", revision);
            return await ReadManyAsync(command, ReadMcpReference, token);
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<McpObservedContextEntry>> ListMcpContextEntriesAsync(McpContextPageQuery query, CancellationToken cancellationToken = default)
    {
        ValidateMcpContextPageQuery(query);
        return await ExecuteStorageAsync(async (connection, transaction, token) =>
        {
            const string observed = "COALESCE(observation.RecordObservedUtc, entry.CreatedUtc)";
            var sql = new StringBuilder($"SELECT entry.Id, entry.ProjectId, entry.RepositoryId, entry.Category, entry.Title, entry.Summary, entry.Content, entry.AuthoritativeReference, entry.Branch, entry.CommitSha, entry.Status, entry.ContentHash, entry.CreatedUtc, entry.UpdatedUtc, entry.SupersededUtc, entry.Tier, entry.Objective, entry.PhaseId, {observed} FROM ContextEntries entry LEFT JOIN McpRecordObservations observation ON observation.RecordKind = {(int)McpRecordKind.ContextEntry} AND observation.RecordId = entry.Id AND observation.Revision = 1 WHERE entry.ProjectId = $projectId");
            if (query.RepositoryId is not null) sql.Append(" AND entry.RepositoryId = $repositoryId");
            if (query.RepositoryIsNull) sql.Append(" AND entry.RepositoryId IS NULL");
            if (query.Category is not null) sql.Append(" AND entry.Category = $category");
            if (query.Branch is not null) sql.Append(" AND entry.Branch = $branch");
            if (query.CommitSha is not null) sql.Append(" AND entry.CommitSha = $commitSha");
            if (query.Status is not null) sql.Append(" AND entry.Status = $status");
            else sql.Append(" AND entry.Status <> $superseded");
            if (query.SinceUtc is not null) sql.Append($" AND {observed} >= $sinceUtc");
            if (query.Text is not null) sql.Append(" AND (entry.Title LIKE $text ESCAPE '\\' OR entry.Summary LIKE $text ESCAPE '\\')");
            if (query.BeforeObservedUtc is not null) sql.Append($" AND ({observed} < $beforeObservedUtc OR ({observed} = $beforeObservedUtc AND entry.Id > $afterRecordId))");
            sql.Append($" ORDER BY {observed} DESC, entry.Id ASC LIMIT $maxResults;");
            await using var command = Command(connection, sql.ToString());
            command.Transaction = transaction;
            Add(command, "$projectId", Id(query.ProjectId));
            if (query.RepositoryId is { } repositoryId) Add(command, "$repositoryId", Id(repositoryId));
            if (query.Category is not null) Add(command, "$category", query.Category);
            if (query.Branch is not null) Add(command, "$branch", query.Branch);
            if (query.CommitSha is not null) Add(command, "$commitSha", query.CommitSha);
            if (query.Status is { } status) Add(command, "$status", (int)status);
            else Add(command, "$superseded", (int)ContextStatus.Superseded);
            if (query.SinceUtc is { } sinceUtc) Add(command, "$sinceUtc", Timestamp(sinceUtc));
            if (query.Text is not null) Add(command, "$text", $"%{EscapeLike(query.Text)}%");
            if (query.BeforeObservedUtc is { } beforeObservedUtc)
            {
                Add(command, "$beforeObservedUtc", Timestamp(beforeObservedUtc));
                Add(command, "$afterRecordId", Id(query.AfterRecordId!.Value));
            }

            Add(command, "$maxResults", query.MaxResults);
            return await ReadManyAsync(command, ReadMcpObservedContextEntry, token);
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<McpObservedDecision>> ListMcpDecisionsAsync(McpDecisionPageQuery query, CancellationToken cancellationToken = default)
    {
        ValidateMcpDecisionPageQuery(query);
        return await ExecuteStorageAsync(async (connection, transaction, token) =>
        {
            const string observed = "COALESCE(observation.RecordObservedUtc, decision.CreatedUtc)";
            var sql = new StringBuilder($"SELECT decision.Id, decision.ProjectId, decision.RepositoryId, decision.Category, decision.Component, decision.Title, decision.DecisionText, decision.Rationale, decision.AuthoritativeReference, decision.ResolutionEvidence, decision.OriginatingCommitSha, decision.Branch, decision.Status, decision.SupersedesDecisionId, decision.SupersededByDecisionId, decision.CreatedUtc, decision.UpdatedUtc, decision.SupersededUtc, decision.Version, {observed} FROM Decisions decision LEFT JOIN McpRecordObservations observation ON observation.RecordKind = {(int)McpRecordKind.Decision} AND observation.RecordId = decision.Id AND observation.Revision = 1 WHERE decision.ProjectId = $projectId");
            if (query.RepositoryId is not null) sql.Append(" AND decision.RepositoryId = $repositoryId");
            if (query.RepositoryIsNull) sql.Append(" AND decision.RepositoryId IS NULL");
            if (query.Statuses is { Count: > 0 })
            {
                sql.Append(" AND decision.Status IN (");
                sql.Append(string.Join(", ", Enumerable.Range(0, query.Statuses.Count).Select(index => $"$status{index}")));
                sql.Append(')');
            }
            else if (!query.IncludeSuperseded)
            {
                sql.Append(" AND decision.Status <> $superseded");
            }

            if (query.BeforeObservedUtc is not null) sql.Append($" AND ({observed} < $beforeObservedUtc OR ({observed} = $beforeObservedUtc AND decision.Id > $afterRecordId))");
            sql.Append($" ORDER BY {observed} DESC, decision.Id ASC LIMIT $maxResults;");
            await using var command = Command(connection, sql.ToString());
            command.Transaction = transaction;
            Add(command, "$projectId", Id(query.ProjectId));
            if (query.RepositoryId is { } repositoryId) Add(command, "$repositoryId", Id(repositoryId));
            if (query.Statuses is { Count: > 0 })
            {
                for (var index = 0; index < query.Statuses.Count; index++) Add(command, $"$status{index}", (int)query.Statuses[index]);
            }
            else if (!query.IncludeSuperseded)
            {
                Add(command, "$superseded", (int)DecisionStatus.Superseded);
            }

            if (query.BeforeObservedUtc is { } beforeObservedUtc)
            {
                Add(command, "$beforeObservedUtc", Timestamp(beforeObservedUtc));
                Add(command, "$afterRecordId", Id(query.AfterRecordId!.Value));
            }

            Add(command, "$maxResults", query.MaxResults);
            return await ReadManyAsync(command, ReadMcpObservedDecision, token);
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<FindingRecord>> ListFindingsAsync(FindingQuery query, CancellationToken cancellationToken = default)
    {
        ValidateFindingQuery(query);
        return await ExecuteStorageAsync(async (connection, transaction, token) =>
        {
            var sql = new StringBuilder("SELECT Id, Revision, ProjectId, RepositoryId, Category, Title, Description, Severity, Status, Resolution, ResolutionEvidence, Branch, CommitSha, AuthoritativeReference, CreatedUtc, UpdatedUtc, Component, Location, Remediation FROM Findings WHERE ProjectId = $projectId AND Revision = (SELECT MAX(currentFinding.Revision) FROM Findings currentFinding WHERE currentFinding.Id = Findings.Id)");
            if (query.RepositoryId is not null) sql.Append(" AND RepositoryId = $repositoryId");
            if (query.RepositoryIsNull) sql.Append(" AND RepositoryId IS NULL");
            if (query.Branch is not null) sql.Append(" AND Branch = $branch");
            if (query.BranchIsNull) sql.Append(" AND Branch IS NULL");
            if (query.CommitSha is not null) sql.Append(" AND CommitSha = $commitSha");
            if (query.Status is not null) sql.Append(" AND Status = $status");
            sql.Append(" ORDER BY Status ASC, Severity ASC, UpdatedUtc DESC, Id ASC LIMIT $maxResults;");
            await using var command = Command(connection, sql.ToString());
            command.Transaction = transaction;
            Add(command, "$projectId", Id(query.ProjectId));
            if (query.RepositoryId is { } repositoryId) Add(command, "$repositoryId", Id(repositoryId));
            if (query.Branch is not null) Add(command, "$branch", query.Branch);
            if (query.CommitSha is not null) Add(command, "$commitSha", query.CommitSha);
            if (query.Status is { } status) Add(command, "$status", (int)status);
            Add(command, "$maxResults", query.MaxResults);
            return await ReadManyAsync(command, ReadFinding, token);
        }, cancellationToken);
    }

    private static async Task InsertFindingAsync(SqliteConnection connection, SqliteTransaction? transaction, Guid id, int revision, Guid projectId, Guid? repositoryId, FindingCategory category, string title, string description, FindingSeverity severity, FindingStatus status, string? resolution, string? resolutionEvidence, string? branch, string? commitSha, string? authoritativeReference, string? component, string? location, string? remediation, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, """
            INSERT INTO Findings (Id, Revision, ProjectId, RepositoryId, Category, Title, Description, Severity, Status, Resolution, ResolutionEvidence, Branch, CommitSha, AuthoritativeReference, CreatedUtc, UpdatedUtc, Component, Location, Remediation)
            VALUES ($id, $revision, $projectId, $repositoryId, $category, $title, $description, $severity, $status, $resolution, $resolutionEvidence, $branch, $commitSha, $authoritativeReference, $createdUtc, $updatedUtc, $component, $location, $remediation);
            """);
        command.Transaction = transaction;
        Add(command, "$id", Id(id));
        Add(command, "$revision", revision);
        Add(command, "$projectId", Id(projectId));
        Add(command, "$repositoryId", NullableId(repositoryId));
        Add(command, "$category", (int)category);
        Add(command, "$title", title);
        Add(command, "$description", description);
        Add(command, "$severity", (int)severity);
        Add(command, "$status", (int)status);
        Add(command, "$resolution", resolution);
        Add(command, "$resolutionEvidence", resolutionEvidence);
        Add(command, "$branch", branch);
        Add(command, "$commitSha", commitSha);
        Add(command, "$authoritativeReference", authoritativeReference);
        Add(command, "$createdUtc", Timestamp(now));
        Add(command, "$updatedUtc", Timestamp(now));
        Add(command, "$component", component);
        Add(command, "$location", location);
        Add(command, "$remediation", remediation);
        await ExecuteAsync(command, cancellationToken);
    }

    private static async Task<FindingRecord?> GetCurrentFindingAsync(SqliteConnection connection, SqliteTransaction? transaction, Guid id, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, "SELECT Id, Revision, ProjectId, RepositoryId, Category, Title, Description, Severity, Status, Resolution, ResolutionEvidence, Branch, CommitSha, AuthoritativeReference, CreatedUtc, UpdatedUtc, Component, Location, Remediation FROM Findings WHERE Id = $id ORDER BY Revision DESC LIMIT 1;");
        command.Transaction = transaction;
        Add(command, "$id", Id(id));
        await using var reader = await ExecuteReaderAsync(command, cancellationToken);
        return await ReadAsync(reader, cancellationToken) ? ReadFinding(reader) : null;
    }

    private static FindingRecord ReadFinding(SqliteDataReader reader) => new(
        ReadGuid(reader.GetString(0)), reader.GetInt32(1), ReadGuid(reader.GetString(2)), ReadNullableGuid(reader, 3), ReadEnum<FindingCategory>(reader.GetInt32(4)), reader.GetString(5), reader.GetString(6), ReadEnum<FindingSeverity>(reader.GetInt32(7)), ReadEnum<FindingStatus>(reader.GetInt32(8)), ReadNullableString(reader, 9), ReadNullableString(reader, 10), ReadTimestamp(reader.GetString(14)), ReadTimestamp(reader.GetString(15)), ReadNullableString(reader, 11), ReadNullableString(reader, 12), ReadNullableString(reader, 13), ReadNullableString(reader, 16), ReadNullableString(reader, 17), ReadNullableString(reader, 18));

    private static HandoffRecord ReadHandoff(SqliteDataReader reader) => new(
        ReadGuid(reader.GetString(0)), ReadGuid(reader.GetString(1)), ReadNullableGuid(reader, 2), ReadNullableString(reader, 3), ReadNullableString(reader, 4), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8), reader.GetString(9), reader.GetString(10), reader.GetString(11), ReadStringList(reader.GetString(12)), ReadStringList(reader.GetString(13)), reader.GetString(14), ReadTimestamp(reader.GetString(15)), ReadTimestamp(reader.GetString(16)), ReadNullableGuid(reader, 17));

    private static void ValidateFinding(FindingDraft finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        RequireId(finding.Id, "finding id");
        RequireId(finding.ProjectId, "project id");
        RequireOptionalId(finding.RepositoryId, "repository id");
        ValidateFindingFields(finding.Category, finding.Title, finding.Description, finding.Severity, finding.Status, finding.Resolution, finding.ResolutionEvidence, finding.Branch, finding.CommitSha, finding.AuthoritativeReference, finding.Component, finding.Location, finding.Remediation);
    }

    private static void ValidateFindingRevision(FindingRevision revision)
    {
        ArgumentNullException.ThrowIfNull(revision);
        RequireId(revision.Id, "finding id");
        if (revision.ExpectedRevision < 1)
        {
            throw new StorageException(StorageErrorCode.Validation, "Expected finding revision is invalid.");
        }

        ValidateFindingFields(revision.Category, revision.Title, revision.Description, revision.Severity, revision.Status, revision.Resolution, revision.ResolutionEvidence, revision.Branch, revision.CommitSha, revision.AuthoritativeReference, revision.Component, revision.Location, revision.Remediation);
    }

    private static void ValidateFindingFields(FindingCategory category, string title, string description, FindingSeverity severity, FindingStatus status, string? resolution, string? resolutionEvidence, string? branch, string? commitSha, string? authoritativeReference, string? component, string? location, string? remediation)
    {
        ValidateEnum(category, "finding category");
        ValidateRequired(title, "title", StorageLimits.Title);
        ValidateRequired(description, "description", StorageLimits.Summary);
        ValidateEnum(severity, "finding severity");
        ValidateEnum(status, "finding status");
        ValidateOptional(resolution, "resolution", StorageLimits.Rationale);
        ValidateOptional(resolutionEvidence, "resolution evidence", StorageLimits.Rationale);
        ValidateOptional(branch, "branch", StorageLimits.ShortText);
        ValidateCommit(commitSha, "commit");
        ValidateOptional(authoritativeReference, "authoritative reference", StorageLimits.Reference);
        ValidateOptional(component, "component", StorageLimits.ShortText);
        ValidateOptional(location, "location", StorageLimits.Reference);
        ValidateOptional(remediation, "remediation", StorageLimits.Rationale);
        if ((status is FindingStatus.Resolved or FindingStatus.Superseded) && string.IsNullOrWhiteSpace(resolutionEvidence))
        {
            throw new StorageException(StorageErrorCode.Validation, "Resolved and superseded findings require resolution evidence.");
        }
    }

    private static void ValidateHandoff(HandoffDraft handoff)
    {
        ArgumentNullException.ThrowIfNull(handoff);
        RequireId(handoff.Id, "handoff id");
        RequireId(handoff.ProjectId, "project id");
        RequireOptionalId(handoff.RepositoryId, "repository id");
        RequireOptionalId(handoff.PhaseId, "phase id");
        ValidateOptional(handoff.Branch, "branch", StorageLimits.ShortText);
        ValidateCommit(handoff.CommitSha, "commit");
        ValidateRequired(handoff.Objective, "objective", StorageLimits.HandoffField);
        ValidateRequired(handoff.CompletedWork, "completed work", StorageLimits.HandoffField);
        ValidateRequired(handoff.ActiveWork, "active work", StorageLimits.HandoffField);
        ValidateRequired(handoff.ActiveBlockers, "active blockers", StorageLimits.HandoffField);
        ValidateRequired(handoff.ImportantDecisions, "important decisions", StorageLimits.HandoffField);
        ValidateRequired(handoff.LatestTestStatus, "latest test status", StorageLimits.HandoffField);
        ValidateRequired(handoff.UnresolvedFindings, "unresolved findings", StorageLimits.HandoffField);
        ValidateRequired(handoff.RecommendedNextAction, "recommended next action", StorageLimits.HandoffField);
        ValidateCollection(handoff.RelevantFiles, "relevant files");
        ValidateCollection(handoff.RelevantArtifacts, "relevant artifacts");
        var aggregate = handoff.Objective.Length + handoff.CompletedWork.Length + handoff.ActiveWork.Length + handoff.ActiveBlockers.Length + handoff.ImportantDecisions.Length + handoff.LatestTestStatus.Length + handoff.UnresolvedFindings.Length + handoff.RecommendedNextAction.Length
            + handoff.RelevantFiles.Sum(value => value.Length) + handoff.RelevantArtifacts.Sum(value => value.Length);
        if (aggregate > StorageLimits.HandoffAggregate)
        {
            throw new StorageException(StorageErrorCode.LimitExceeded, "Handoff content exceeds the aggregate limit.");
        }
    }

    private static void ValidateMcpObservation(McpObservationDraft observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ValidateMcpRecordKey(observation.RecordKind, observation.RecordId, observation.Revision);
        RequireId(observation.ProjectId, "observation project id");
        RequireOptionalId(observation.RepositoryId, "observation repository id");
        ValidateRequired(observation.SourceKind, "source kind", StorageLimits.ShortText);
        ValidateMcpReferenceValue(observation.SourceReference, "source reference");
        if (observation.SourceReference is { } source && !MatchesMcpScope(observation.ProjectId, observation.RepositoryId, source.ProjectId, source.RepositoryId))
        {
            throw new StorageException(StorageErrorCode.CrossProjectReference, "Source reference must remain within the record scope.");
        }

        ValidateRequired(observation.CanonicalRoot, "snapshot canonical root", StorageLimits.Identifier);
        ValidateOptional(observation.Branch, "snapshot branch", StorageLimits.ShortText);
        ValidateCommit(observation.Head, "snapshot head");
        ValidateOptional(observation.WorkingTreeFingerprint, "snapshot working tree fingerprint", StorageLimits.Identifier);
        ValidateHash(observation.WorkingTreeFingerprint, "snapshot working tree fingerprint");
        ValidateRequired(observation.Completeness, "snapshot completeness", StorageLimits.ShortText);
        if (NormalizeTimestamp(observation.SourceObservedUtc, "source observed timestamp") is null
            || NormalizeTimestamp(observation.SnapshotObservedUtc, "snapshot observed timestamp") is null
            || NormalizeTimestamp(observation.RecordObservedUtc, "record observed timestamp") is null)
        {
            throw new StorageException(StorageErrorCode.Validation, "Observation timestamp is invalid.");
        }
    }

    private static void ValidateMcpReference(McpReferenceDraft reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ValidateMcpRecordKey(reference.RecordKind, reference.RecordId, reference.Revision);
        ValidateEnum(reference.Role, "reference role");
        if (reference.Ordinal is < 0 or > StorageLimits.CollectionCount)
        {
            throw new StorageException(StorageErrorCode.LimitExceeded, "Reference ordinal is invalid.");
        }

        ValidateMcpReferenceValue(reference.Reference, "reference");
    }

    private static void ValidateMcpRecordKey(McpRecordKind recordKind, Guid recordId, int revision)
    {
        ValidateEnum(recordKind, "record kind");
        RequireId(recordId, "record id");
        if (revision < 1) throw new StorageException(StorageErrorCode.Validation, "record revision is invalid.");
    }

    private static void ValidateMcpReferenceValue(McpSourceReference? reference, string field)
    {
        if (reference is null)
        {
            return;
        }

        RequireId(reference.ProjectId, $"{field} project id");
        RequireOptionalId(reference.RepositoryId, $"{field} repository id");
        ValidateRequired(reference.Kind, $"{field} kind", StorageLimits.ShortText);
        RequireOptionalId(reference.Id, $"{field} id");
        ValidateOptional(reference.RelativePath, $"{field} relative path", StorageLimits.Reference);
        ValidateOptional(reference.Uri, $"{field} URI", StorageLimits.Reference);
        ValidateHash(reference.Hash, $"{field} hash");
        if (reference.Id is null && reference.RelativePath is null && reference.Uri is null && reference.Hash is null)
        {
            throw new StorageException(StorageErrorCode.Validation, $"{field} has no locator.");
        }
    }

    private static McpObservationRecord ToMcpObservationRecord(McpObservationDraft observation) => new(
        observation.RecordKind, observation.RecordId, observation.Revision, observation.ProjectId, observation.RepositoryId, observation.SourceKind, observation.SourceReference, observation.SourceObservedUtc.ToUniversalTime(),
        observation.CanonicalRoot, observation.Branch, observation.Head, observation.WorkingTreeFingerprint, observation.SnapshotObservedUtc.ToUniversalTime(), observation.Completeness, observation.RecordObservedUtc.ToUniversalTime());

    private static McpObservationRecord ReadMcpObservation(SqliteDataReader reader)
    {
        var sourceReference = reader.IsDBNull(7)
            ? null
            : new McpSourceReference(ReadGuid(reader.GetString(3)), ReadNullableGuid(reader, 8), reader.GetString(7), ReadNullableGuid(reader, 9), ReadNullableString(reader, 10), ReadNullableString(reader, 11), ReadNullableString(reader, 12));
        return new McpObservationRecord(
            ReadEnum<McpRecordKind>(reader.GetInt32(0)), ReadGuid(reader.GetString(1)), reader.GetInt32(2), ReadGuid(reader.GetString(3)), ReadNullableGuid(reader, 4), reader.GetString(5), sourceReference, ReadTimestamp(reader.GetString(6)),
            reader.GetString(13), ReadNullableString(reader, 14), ReadNullableString(reader, 15), ReadNullableString(reader, 16), ReadTimestamp(reader.GetString(17)), reader.GetString(18), ReadTimestamp(reader.GetString(19)));
    }

    private static McpReferenceRecord ReadMcpReference(SqliteDataReader reader) => new(
        ReadEnum<McpRecordKind>(reader.GetInt32(0)), ReadGuid(reader.GetString(1)), reader.GetInt32(2), ReadEnum<McpReferenceRole>(reader.GetInt32(3)), reader.GetInt32(4),
        new McpSourceReference(ReadGuid(reader.GetString(5)), ReadNullableGuid(reader, 6), reader.GetString(7), ReadNullableGuid(reader, 8), ReadNullableString(reader, 9), ReadNullableString(reader, 10), ReadNullableString(reader, 11)));

    private static bool MatchesMcpScope(Guid ownerProjectId, Guid? ownerRepositoryId, Guid referencedProjectId, Guid? referencedRepositoryId) =>
        ownerProjectId == referencedProjectId && (referencedRepositoryId is null || referencedRepositoryId == ownerRepositoryId);

    private static async Task<McpObservationRecord?> GetMcpObservationAsync(SqliteConnection connection, SqliteTransaction? transaction, McpRecordKind recordKind, Guid recordId, int revision, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, "SELECT RecordKind, RecordId, Revision, ProjectId, RepositoryId, SourceKind, SourceObservedUtc, SourceReferenceKind, SourceReferenceRepositoryId, SourceReferenceId, SourceReferenceRelativePath, SourceReferenceUri, SourceReferenceHash, CanonicalRoot, Branch, Head, WorkingTreeFingerprint, SnapshotObservedUtc, Completeness, RecordObservedUtc FROM McpRecordObservations WHERE RecordKind = $recordKind AND RecordId = $recordId AND Revision = $revision;");
        command.Transaction = transaction;
        Add(command, "$recordKind", (int)recordKind);
        Add(command, "$recordId", Id(recordId));
        Add(command, "$revision", revision);
        await using var reader = await ExecuteReaderAsync(command, cancellationToken);
        return await ReadAsync(reader, cancellationToken) ? ReadMcpObservation(reader) : null;
    }

    private static async Task<PhaseRecord?> GetPhaseAsync(SqliteConnection connection, SqliteTransaction? transaction, Guid id, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, "SELECT Id, ProjectId, RepositoryId, PhaseKey, Objective, Status, Branch, CommitSha, StartedUtc, CompletedUtc, CreatedUtc, UpdatedUtc, Version, ContextEntryId FROM Phases WHERE Id = $id;");
        command.Transaction = transaction;
        Add(command, "$id", Id(id));
        await using var reader = await ExecuteReaderAsync(command, cancellationToken);
        return await ReadAsync(reader, cancellationToken) ? ReadPhase(reader) : null;
    }

    private static async Task EnsurePhaseScopeAsync(SqliteConnection connection, SqliteTransaction? transaction, Guid projectId, Guid? repositoryId, Guid? phaseId, CancellationToken cancellationToken)
    {
        if (phaseId is not { } id)
        {
            return;
        }

        var phase = await GetPhaseAsync(connection, transaction, id, cancellationToken)
            ?? throw new StorageException(StorageErrorCode.NotFound, "Phase was not found.");
        if (phase.ProjectId != projectId || phase.RepositoryId != repositoryId)
        {
            throw new StorageException(StorageErrorCode.CrossProjectReference, "Phase must remain within the record scope.");
        }
    }

    private static async Task EnsureContextEntryScopeAsync(SqliteConnection connection, SqliteTransaction? transaction, Guid projectId, Guid? repositoryId, Guid? contextEntryId, CancellationToken cancellationToken)
    {
        if (contextEntryId is not { } id)
        {
            return;
        }

        await using var command = Command(connection, "SELECT ProjectId, RepositoryId FROM ContextEntries WHERE Id = $id;");
        command.Transaction = transaction;
        Add(command, "$id", Id(id));
        await using var reader = await ExecuteReaderAsync(command, cancellationToken);
        if (!await ReadAsync(reader, cancellationToken))
        {
            throw new StorageException(StorageErrorCode.NotFound, "Phase context entry was not found.");
        }

        if (ReadGuid(reader.GetString(0)) != projectId || ReadNullableGuid(reader, 1) != repositoryId)
        {
            throw new StorageException(StorageErrorCode.CrossProjectReference, "Phase context entry must remain within the phase scope.");
        }
    }

    private static async Task<(Guid ProjectId, Guid? RepositoryId)?> GetMcpRecordScopeAsync(SqliteConnection connection, SqliteTransaction? transaction, McpRecordKind recordKind, Guid recordId, int revision, CancellationToken cancellationToken)
    {
        if (recordKind is not McpRecordKind.Finding && revision != 1)
        {
            return null;
        }

        var sql = recordKind switch
        {
            McpRecordKind.ContextEntry => "SELECT ProjectId, RepositoryId FROM ContextEntries WHERE Id = $id;",
            McpRecordKind.Decision => "SELECT ProjectId, RepositoryId FROM Decisions WHERE Id = $id;",
            McpRecordKind.TestRun => "SELECT ProjectId, RepositoryId FROM TestRuns WHERE Id = $id;",
            McpRecordKind.Finding => "SELECT ProjectId, RepositoryId FROM Findings WHERE Id = $id AND Revision = $revision;",
            McpRecordKind.Handoff => "SELECT ProjectId, RepositoryId FROM Handoffs WHERE Id = $id;",
            _ => throw new StorageException(StorageErrorCode.Validation, "Record kind is invalid.")
        };
        await using var command = Command(connection, sql);
        command.Transaction = transaction;
        Add(command, "$id", Id(recordId));
        if (recordKind == McpRecordKind.Finding) Add(command, "$revision", revision);
        await using var reader = await ExecuteReaderAsync(command, cancellationToken);
        return await ReadAsync(reader, cancellationToken) ? (ReadGuid(reader.GetString(0)), ReadNullableGuid(reader, 1)) : null;
    }

    private static async Task EnsureRepositoryScopeAsync(SqliteConnection connection, SqliteTransaction? transaction, Guid projectId, Guid? repositoryId, CancellationToken cancellationToken)
    {
        if (repositoryId is not { } id)
        {
            return;
        }

        await using var command = Command(connection, "SELECT ProjectId FROM Repositories WHERE Id = $id;");
        command.Transaction = transaction;
        Add(command, "$id", Id(id));
        var owner = await ExecuteScalarAsync(command, cancellationToken) as string;
        if (owner is null)
        {
            throw new StorageException(StorageErrorCode.NotFound, "Repository was not found.");
        }

        if (ReadGuid(owner) != projectId)
        {
            throw new StorageException(StorageErrorCode.CrossProjectReference, "Repository does not belong to the project.");
        }
    }

    private static void RequireId(Guid id, string field)
    {
        if (id == Guid.Empty)
        {
            throw new StorageException(StorageErrorCode.Validation, $"{field} is required.");
        }
    }

    private static void RequireOptionalId(Guid? id, string field)
    {
        if (id == Guid.Empty)
        {
            throw new StorageException(StorageErrorCode.Validation, $"{field} is invalid.");
        }
    }

    private static void ValidateRequired(string? value, string field, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new StorageException(StorageErrorCode.Validation, $"{field} is required.");
        }

        ValidateOptional(value, field, maximumLength);
    }

    private static void ValidateOptional(string? value, string field, int maximumLength)
    {
        if (value is null)
        {
            return;
        }

        if (value.Length > maximumLength)
        {
            throw new StorageException(StorageErrorCode.LimitExceeded, $"{field} exceeds its limit.");
        }

        if (SecretValueClassifier.IsSecretShaped(value))
        {
            throw new StorageException(StorageErrorCode.SecretRejected, "Record content contains secret-shaped data.");
        }
    }

    private static void ValidateCommit(string? commitSha, string field)
    {
        ValidateOptional(commitSha, field, StorageLimits.Identifier);
        if (commitSha is not null && !CommitPattern.IsMatch(commitSha))
        {
            throw new StorageException(StorageErrorCode.Validation, $"{field} is malformed.");
        }
    }

    private static void ValidateHash(string? hash, string field)
    {
        if (hash is null)
        {
            return;
        }

        ValidateOptional(hash, field, StorageLimits.Identifier);
        if (!HashPattern.IsMatch(hash))
        {
            throw new StorageException(StorageErrorCode.Validation, $"{field} is malformed.");
        }
    }

    private static void ValidateArtifactReference(string reference)
    {
        ValidateRequired(reference, "artifact reference", StorageLimits.Reference);
        if (Path.IsPathRooted(reference)
            || reference.StartsWith("\\\\", StringComparison.Ordinal)
            || reference.StartsWith("\\\\?\\", StringComparison.Ordinal)
            || reference.StartsWith("\\\\.\\", StringComparison.Ordinal)
            || reference.Contains(':', StringComparison.Ordinal)
            || reference.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).Any(segment => segment is "." or ".."))
        {
            throw new StorageException(StorageErrorCode.Validation, "Artifact reference must be a safe relative path.");
        }
    }

    private static void ValidateCollection(IReadOnlyList<string>? values, string field)
    {
        if (values is null || values.Count > StorageLimits.CollectionCount)
        {
            throw new StorageException(StorageErrorCode.LimitExceeded, $"{field} exceeds its limit.");
        }

        foreach (var value in values)
        {
            ValidateRequired(value, field, StorageLimits.Reference);
        }
    }

    private static void ValidateContextEntryQuery(ContextEntryQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateMcpPageScope(query.ProjectId, query.RepositoryId, query.MaxResults);
        if (query.RepositoryIsNull && query.RepositoryId is not null) throw new StorageException(StorageErrorCode.Validation, "Repository filters conflict.");
        ValidateOptional(query.Category, "category", StorageLimits.ShortText);
        ValidateOptional(query.Branch, "branch", StorageLimits.ShortText);
        ValidateCommit(query.CommitSha, "commit");
        if (query.Status is { } status) ValidateEnum(status, "context status");
        ValidateOptional(query.Text, "text", StorageLimits.Title);
        _ = NormalizeTimestamp(query.SinceUtc, "recency timestamp");
    }

    private static void ValidateMcpContextPageQuery(McpContextPageQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateMcpPageScope(query.ProjectId, query.RepositoryId, query.MaxResults);
        if (query.RepositoryIsNull && query.RepositoryId is not null) throw new StorageException(StorageErrorCode.Validation, "Repository filters conflict.");
        ValidateOptional(query.Category, "category", StorageLimits.ShortText);
        ValidateOptional(query.Branch, "branch", StorageLimits.ShortText);
        ValidateCommit(query.CommitSha, "commit");
        if (query.Status is { } status) ValidateEnum(status, "context status");
        ValidateOptional(query.Text, "text", StorageLimits.Title);
        _ = NormalizeTimestamp(query.SinceUtc, "recency timestamp");
        ValidateMcpCursor(query.BeforeObservedUtc, query.AfterRecordId);
    }

    private static void ValidateMcpDecisionPageQuery(McpDecisionPageQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateMcpPageScope(query.ProjectId, query.RepositoryId, query.MaxResults);
        if (query.RepositoryIsNull && query.RepositoryId is not null) throw new StorageException(StorageErrorCode.Validation, "Repository filters conflict.");
        if (query.Statuses is { } statuses)
        {
            if (statuses.Count > StorageLimits.CollectionCount) throw new StorageException(StorageErrorCode.LimitExceeded, "Decision statuses exceed their limit.");
            foreach (var status in statuses) ValidateEnum(status, "decision status");
        }

        ValidateMcpCursor(query.BeforeObservedUtc, query.AfterRecordId);
    }

    private static void ValidateMcpCursor(DateTimeOffset? beforeObservedUtc, Guid? afterRecordId)
    {
        if ((beforeObservedUtc is null) != (afterRecordId is null))
        {
            throw new StorageException(StorageErrorCode.Validation, "Cursor keyset is incomplete.");
        }

        _ = NormalizeTimestamp(beforeObservedUtc, "cursor timestamp");
        RequireOptionalId(afterRecordId, "cursor record id");
    }

    private static void ValidateDecisionQuery(DecisionQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateQueryScope(query.ProjectId, query.RepositoryId, query.MaxResults);
        if (query.RepositoryIsNull && query.RepositoryId is not null) throw new StorageException(StorageErrorCode.Validation, "Repository filters conflict.");
        ValidateOptional(query.Category, "category", StorageLimits.ShortText);
        ValidateOptional(query.Component, "component", StorageLimits.ShortText);
        ValidateOptional(query.Branch, "branch", StorageLimits.ShortText);
        if (query.BranchIsNull && query.Branch is not null) throw new StorageException(StorageErrorCode.Validation, "Branch filters conflict.");
        ValidateCommit(query.CommitSha, "commit");
        if (query.Status is { } status) ValidateEnum(status, "decision status");
        _ = NormalizeTimestamp(query.SinceUtc, "recency timestamp");
    }

    private static void ValidatePhaseQuery(PhaseQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateQueryScope(query.ProjectId, query.RepositoryId, query.MaxResults);
        if (query.RepositoryIsNull && query.RepositoryId is not null) throw new StorageException(StorageErrorCode.Validation, "Repository filters conflict.");
        if (query.Status is { } status) ValidateEnum(status, "phase status");
        ValidateOptional(query.Branch, "branch", StorageLimits.ShortText);
        if (query.BranchIsNull && query.Branch is not null) throw new StorageException(StorageErrorCode.Validation, "Branch filters conflict.");
        ValidateCommit(query.CommitSha, "commit");
    }

    private static void ValidateTestRunQuery(TestRunQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateQueryScope(query.ProjectId, query.RepositoryId, query.MaxResults);
        if (query.RepositoryIsNull && query.RepositoryId is not null) throw new StorageException(StorageErrorCode.Validation, "Repository filters conflict.");
        ValidateOptional(query.Branch, "branch", StorageLimits.ShortText);
        ValidateCommit(query.CommitSha, "commit");
        if (query.Status is { } status) ValidateEnum(status, "test status");
    }

    private static void ValidateFindingQuery(FindingQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateQueryScope(query.ProjectId, query.RepositoryId, query.MaxResults);
        if (query.RepositoryIsNull && query.RepositoryId is not null) throw new StorageException(StorageErrorCode.Validation, "Repository filters conflict.");
        ValidateOptional(query.Branch, "branch", StorageLimits.ShortText);
        if (query.BranchIsNull && query.Branch is not null) throw new StorageException(StorageErrorCode.Validation, "Branch filters conflict.");
        ValidateCommit(query.CommitSha, "commit");
        if (query.Status is { } status) ValidateEnum(status, "finding status");
    }

    private static void ValidateQueryScope(Guid projectId, Guid? repositoryId, int maxResults)
    {
        RequireId(projectId, "project id");
        RequireOptionalId(repositoryId, "repository id");
        ValidateMaxResults(maxResults);
    }

    private static void ValidateMcpPageScope(Guid projectId, Guid? repositoryId, int maxResults)
    {
        RequireId(projectId, "project id");
        RequireOptionalId(repositoryId, "repository id");
        if (maxResults is < 1 or > StorageLimits.CollectionCount + 1)
        {
            throw new StorageException(StorageErrorCode.LimitExceeded, "Requested result count exceeds its limit.");
        }
    }

    private static string EscapeLike(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    private static void ValidateMaxResults(int maxResults)
    {
        if (maxResults is < 1 or > StorageLimits.CollectionCount)
        {
            throw new StorageException(StorageErrorCode.LimitExceeded, "Requested result count exceeds its limit.");
        }
    }

    private static void ValidateEnum<T>(T value, string field) where T : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new StorageException(StorageErrorCode.Validation, $"{field} is invalid.");
        }
    }

    private static DateTimeOffset? NormalizeTimestamp(DateTimeOffset? timestamp, string field)
    {
        if (timestamp is null)
        {
            return null;
        }

        if (timestamp.Value == default)
        {
            throw new StorageException(StorageErrorCode.Validation, $"{field} is invalid.");
        }

        return timestamp.Value.ToUniversalTime();
    }

    private static string NormalizeOperatorPath(string? path, string kind)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new StorageException(StorageErrorCode.Validation, $"{kind} path is required.");
        }

        if (!Path.IsPathFullyQualified(path)
            || path.StartsWith("\\\\", StringComparison.Ordinal)
            || path.StartsWith("\\\\?\\", StringComparison.Ordinal)
            || path.StartsWith("\\\\.\\", StringComparison.Ordinal))
        {
            throw new StorageException(StorageErrorCode.Validation, $"{kind} path must be an absolute local path.");
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            if (fullPath.Length < 3 || fullPath[1] != ':' || fullPath.AsSpan(2).Contains(':'))
            {
                throw new StorageException(StorageErrorCode.Validation, $"{kind} path is invalid.");
            }

            return fullPath;
        }
        catch (StorageException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new StorageException(StorageErrorCode.Validation, $"{kind} path is invalid.");
        }
    }

    private void EnsureSafeDatabaseTarget()
    {
        var directory = Path.GetDirectoryName(_databasePath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            throw new StorageException(StorageErrorCode.DatabaseUnavailable, "Database directory is unavailable.");
        }

        EnsureNoReparseAncestors(directory);
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
        {
            EnsureNotReparsePoint(_databasePath + suffix);
        }
    }

    private static void EnsureNoReparseAncestors(string directory)
    {
        var root = Path.GetPathRoot(directory) ?? throw new StorageException(StorageErrorCode.DatabaseUnavailable, "Database directory is unavailable.");
        var current = root;
        EnsureNotReparsePoint(current);
        foreach (var segment in directory[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!Directory.Exists(current))
            {
                throw new StorageException(StorageErrorCode.DatabaseUnavailable, "Database directory is unavailable.");
            }

            EnsureNotReparsePoint(current);
        }
    }

    private static void EnsureNotReparsePoint(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new StorageException(StorageErrorCode.DatabaseUnavailable, "Storage path contains a reparse point.");
            }
        }
        catch (StorageException)
        {
            throw;
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (UnauthorizedAccessException)
        {
            throw new StorageException(StorageErrorCode.DatabaseUnavailable, "Storage path cannot be inspected.");
        }
        catch (IOException)
        {
            throw new StorageException(StorageErrorCode.DatabaseUnavailable, "Storage path cannot be inspected.");
        }
    }

    private static StorageException MapException(SqliteException exception) => exception.SqliteErrorCode switch
    {
        5 or 6 => new StorageException(StorageErrorCode.DatabaseBusy, "Database is busy; retry the operation."),
        11 or 26 => new StorageException(StorageErrorCode.DatabaseCorrupt, "Database is corrupt or malformed."),
        14 => new StorageException(StorageErrorCode.DatabaseUnavailable, "Database cannot be opened."),
        19 when exception.SqliteExtendedErrorCode == 787 => new StorageException(StorageErrorCode.CrossProjectReference, "Database relationship constraint was rejected."),
        19 => new StorageException(StorageErrorCode.Duplicate, "A record with the same durable identity already exists."),
        _ => new StorageException(StorageErrorCode.DatabaseCorrupt, "Database operation failed.")
    };

    private static string Id(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);
    private static string? NullableId(Guid? id) => id?.ToString("D", CultureInfo.InvariantCulture);
    private static DateTimeOffset UtcNow() => DateTimeOffset.UtcNow;
    private static string Timestamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static string? NullableTimestamp(DateTimeOffset? value) => value is null ? null : Timestamp(value.Value);
    private static string? ReadNullableString(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static Guid? ReadNullableGuid(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : ReadGuid(reader.GetString(ordinal));
    private static DateTimeOffset? ReadNullableTimestamp(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : ReadTimestamp(reader.GetString(ordinal));

    private static Guid ReadGuid(string value) => Guid.TryParse(value, out var id)
        ? id
        : throw new StorageException(StorageErrorCode.DatabaseCorrupt, "Database identifier is malformed.");

    private static T ReadEnum<T>(int value) where T : struct, Enum
    {
        var result = (T)Enum.ToObject(typeof(T), value);
        return Enum.IsDefined(result)
            ? result
            : throw new StorageException(StorageErrorCode.DatabaseCorrupt, "Database enum value is malformed.");
    }

    private static DateTimeOffset ReadTimestamp(string value)
    {
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp))
        {
            throw new StorageException(StorageErrorCode.DatabaseCorrupt, "Database timestamp is malformed.");
        }

        return timestamp.ToUniversalTime();
    }

    private static IReadOnlyList<string> ReadStringList(string value)
    {
        try
        {
            var items = JsonSerializer.Deserialize<string[]>(value) ?? throw new JsonException();
            ValidateCollection(items, "stored reference list");
            return items;
        }
        catch (StorageException exception) when (exception.Code is StorageErrorCode.LimitExceeded or StorageErrorCode.SecretRejected or StorageErrorCode.Validation)
        {
            throw new StorageException(StorageErrorCode.DatabaseCorrupt, "Stored reference list is malformed.");
        }
        catch (JsonException)
        {
            throw new StorageException(StorageErrorCode.DatabaseCorrupt, "Stored reference list is malformed.");
        }
    }
}
