using AIContextMCP.Core;
using AIContextMCP.Storage.Sqlite;
using Xunit;

namespace AIContextMCP.Storage.Sqlite.Tests;

public sealed class MutationReceiptTests
{
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string OtherHash = "abcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcd";

    [Fact]
    public async Task CallbackTransactionReplayAndPayloadConflictAreDurable()
    {
        await using var database = TemporaryDatabase.Create();
        await database.Storage.InitializeAsync();
        var request = Request();
        var project = new ProjectDraft(Guid.NewGuid(), "replay", "Replay");
        var callbacks = 0;
        var first = await database.Storage.ExecuteMutationAsync(request, async (storage, token) =>
        {
            callbacks++;
            var created = await storage.CreateProjectAsync(project, token);
            Assert.NotNull(await storage.GetProjectAsync(created.Id, token));
            return """{"ok":true,"snapshotToken":"opaque-server-token"}""";
        });

        var reopened = Reopen(database);
        var replay = await reopened.ExecuteMutationAsync(request, (_, _) => throw new InvalidOperationException("Replay invoked its callback."));
        var conflict = await Assert.ThrowsAsync<StorageException>(() => reopened.ExecuteMutationAsync(request with { CanonicalPayloadHash = OtherHash }, (_, _) => Task.FromResult("{}")));

        Assert.Equal(first, replay);
        Assert.Equal(1, callbacks);
        Assert.Equal(StorageErrorCode.Conflict, conflict.Code);
        Assert.NotNull(await reopened.GetProjectAsync(project.Id));
    }

    [Fact]
    public async Task FailureAndCancellationRollbackDataAndReceipt()
    {
        await using var database = TemporaryDatabase.Create();
        await database.Storage.InitializeAsync();

        var failedRequest = Request();
        var failedProject = new ProjectDraft(Guid.NewGuid(), "failure", "Failure");
        await Assert.ThrowsAsync<InvalidOperationException>(() => database.Storage.ExecuteMutationAsync(failedRequest, async (storage, token) =>
        {
            await storage.CreateProjectAsync(failedProject, token);
            throw new InvalidOperationException("failure");
        }));
        Assert.Null(await database.Storage.GetProjectAsync(failedProject.Id));
        await database.Storage.ExecuteMutationAsync(failedRequest, (storage, token) => CreateProjectReceiptAsync(storage, failedProject, token));
        Assert.NotNull(await database.Storage.GetProjectAsync(failedProject.Id));

        var canceledRequest = Request();
        var canceledProject = new ProjectDraft(Guid.NewGuid(), "canceled", "Canceled");
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => database.Storage.ExecuteMutationAsync(canceledRequest, async (storage, token) =>
        {
            await storage.CreateProjectAsync(canceledProject, token);
            cancellation.Cancel();
            return "{}";
        }, cancellation.Token));
        Assert.Null(await database.Storage.GetProjectAsync(canceledProject.Id));
        await database.Storage.ExecuteMutationAsync(canceledRequest, (storage, token) => CreateProjectReceiptAsync(storage, canceledProject, token));
        Assert.NotNull(await database.Storage.GetProjectAsync(canceledProject.Id));
    }

    [Fact]
    public async Task ConcurrentInstancesRejectBusyThenReplaySameKeyOnce()
    {
        await using var database = TemporaryDatabase.Create();
        await database.Storage.InitializeAsync();
        var second = Reopen(database);
        var request = Request();
        var project = new ProjectDraft(Guid.NewGuid(), "concurrent", "Concurrent");
        var callbackEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbacks = 0;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var first = database.Storage.ExecuteMutationAsync(request, async (storage, token) =>
        {
            Interlocked.Increment(ref callbacks);
            callbackEntered.TrySetResult(true);
            await release.Task.WaitAsync(token);
            return await CreateProjectReceiptAsync(storage, project, token);
        }, cancellation.Token);
        await callbackEntered.Task.WaitAsync(cancellation.Token);
        try
        {
            // Hold the first transaction until contention is observed, then retry the same request.
            var busy = await Assert.ThrowsAsync<StorageException>(() => second.ExecuteMutationAsync(request, async (storage, token) =>
            {
                Interlocked.Increment(ref callbacks);
                return await CreateProjectReceiptAsync(storage, project, token);
            }, cancellation.Token));
            Assert.Equal(StorageErrorCode.DatabaseBusy, busy.Code);
        }
        finally
        {
            release.TrySetResult(true);
            await first.WaitAsync(cancellation.Token);
        }

        var replay = await second.ExecuteMutationAsync(request, (_, _) => throw new InvalidOperationException("Replay invoked its callback."), cancellation.Token);
        Assert.Equal(await first, replay);
        Assert.Equal(1, callbacks);
        Assert.NotNull(await second.GetProjectAsync(project.Id));
    }

    [Fact]
    public async Task RequestWindowAndSecretReceiptsDoNotApplyMutation()
    {
        await using var database = TemporaryDatabase.Create();
        await database.Storage.InitializeAsync();
        var called = false;
        var malformed = await Assert.ThrowsAsync<StorageException>(() => database.Storage.ExecuteMutationAsync(Request("not-an-id"), (_, _) =>
        {
            called = true;
            return Task.FromResult("{}");
        }));
        var expired = await Assert.ThrowsAsync<StorageException>(() => database.Storage.ExecuteMutationAsync(Request("1700000000:01234567-89ab-cdef-0123-456789abcdef"), (_, _) =>
        {
            called = true;
            return Task.FromResult("{}");
        }));
        var future = await Assert.ThrowsAsync<StorageException>(() => database.Storage.ExecuteMutationAsync(Request($"{DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 120}:{Guid.NewGuid():D}"), (_, _) =>
        {
            called = true;
            return Task.FromResult("{}");
        }));
        var secretScope = await Assert.ThrowsAsync<StorageException>(() => database.Storage.ExecuteMutationAsync(Request() with { Scope = "password=synthetic" }, (_, _) =>
        {
            called = true;
            return Task.FromResult("{}");
        }));
        Assert.Equal(StorageErrorCode.Validation, malformed.Code);
        Assert.Equal(StorageErrorCode.Conflict, expired.Code);
        Assert.Equal(StorageErrorCode.Conflict, future.Code);
        Assert.Equal(StorageErrorCode.SecretRejected, secretScope.Code);
        Assert.False(called);

        var secretProject = new ProjectDraft(Guid.NewGuid(), "secret", "Secret");
        var secret = await Assert.ThrowsAsync<StorageException>(() => database.Storage.ExecuteMutationAsync(Request(), async (storage, token) =>
        {
            await storage.CreateProjectAsync(secretProject, token);
            return """{"apiKey":"synthetic-secret"}""";
        }));
        Assert.Equal(StorageErrorCode.SecretRejected, secret.Code);
        Assert.Null(await database.Storage.GetProjectAsync(secretProject.Id));
    }

    [Fact]
    public async Task NearFutureRequestAndNumericCredentialReceiptRemainSafe()
    {
        await using var database = TemporaryDatabase.Create();
        await database.Storage.InitializeAsync();
        var request = Request($"{DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 30}:{Guid.NewGuid():D}");
        var first = await database.Storage.ExecuteMutationAsync(request, (_, _) => Task.FromResult("""{"ok":true,"snapshotToken":"opaque-server-token"}"""));
        var replay = await database.Storage.ExecuteMutationAsync(request, (_, _) => throw new InvalidOperationException("Replay invoked its callback."));
        Assert.Equal(first, replay);

        var project = new ProjectDraft(Guid.NewGuid(), "numeric-secret", "Numeric secret");
        var secret = await Assert.ThrowsAsync<StorageException>(() => database.Storage.ExecuteMutationAsync(Request(), async (storage, token) =>
        {
            await storage.CreateProjectAsync(project, token);
            return """{"password":123456}""";
        }));
        Assert.Equal(StorageErrorCode.SecretRejected, secret.Code);
        Assert.Null(await database.Storage.GetProjectAsync(project.Id));
    }

    private static MutationRequest Request(string? requestId = null) => new(
        "project-scope",
        "context.record",
        requestId ?? $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}:{Guid.NewGuid():D}",
        Hash);

    private static SqliteContextStorage Reopen(TemporaryDatabase database) => new(new SqliteStorageOptions
    {
        DatabasePath = database.Storage.DatabasePath,
        ArtifactRoot = database.Storage.ArtifactRoot
    });

    private static async Task<string> CreateProjectReceiptAsync(IAIContextStorage storage, ProjectDraft project, CancellationToken cancellationToken)
    {
        await storage.CreateProjectAsync(project, cancellationToken);
        return """{"ok":true,"id":"receipt"}""";
    }
}
