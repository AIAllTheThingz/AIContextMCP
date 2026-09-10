using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIContextMCP.Core;

namespace AIContextMCP.Server;

internal sealed class SnapshotTokens
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    private readonly byte[] _key;
    private readonly TimeProvider _timeProvider;

    public SnapshotTokens() : this(RandomNumberGenerator.GetBytes(32), TimeProvider.System)
    {
    }

    internal SnapshotTokens(byte[] key, TimeProvider timeProvider)
    {
        if (key is not { Length: 32 }) throw new ArgumentException("Snapshot signing key must be 32 bytes.", nameof(key));
        _key = key.ToArray();
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public Snapshot CreateSnapshot(ProjectIdentity project, RepositoryIdentity repository, GitRepositoryState git, bool includeWorkingTree)
    {
        var snapshot = new Snapshot(
            string.Empty,
            git.CanonicalPath,
            includeWorkingTree ? git.WorkingTreeFingerprint : null,
            _timeProvider.GetUtcNow(),
            includeWorkingTree ? git.WorkingTree.ToString() : "Unknown",
            git.Branch,
            git.HeadCommitSha);
        var payload = NewPayload("snapshot", project.Id, repository.Id, SnapshotDigest(snapshot), StateDigest(snapshot));
        return snapshot with { SnapshotToken = Sign(payload) };
    }

    public void VerifySnapshot(Snapshot snapshot, Guid projectId, Guid? repositoryId)
    {
        McpJson.SnapshotShape(snapshot, "snapshot");
        var payload = Verify(snapshot.SnapshotToken, "snapshot", projectId, repositoryId);
        if (!FixedEquals(payload.SnapshotDigest, SnapshotDigest(snapshot)))
        {
            throw new McpInputException("StaleState", "Snapshot token does not match the supplied observation.");
        }
    }

    public Guid? VerifySnapshotForProject(Snapshot snapshot, Guid projectId)
    {
        McpJson.SnapshotShape(snapshot, "snapshot");
        var payload = Verify(snapshot.SnapshotToken, "snapshot", projectId, null, requireRepository: false);
        if (!FixedEquals(payload.SnapshotDigest, SnapshotDigest(snapshot)))
        {
            throw new McpInputException("StaleState", "Snapshot token does not match the supplied observation.");
        }

        if (payload.RepositoryId is null) return null;
        if (!Guid.TryParseExact(payload.RepositoryId, "N", out var repositoryId)) throw new McpInputException("StaleState", "Snapshot token is invalid or expired.");
        return repositoryId;
    }

    public void VerifyExpectedSnapshot(string? token, Guid projectId, Guid? repositoryId, Snapshot current)
    {
        McpJson.Required(token, McpJson.Cursor, "expected snapshot token");
        var payload = Verify(token!, "snapshot", projectId, repositoryId);
        if (current.Completeness != "Clean" || current.WorkingTreeFingerprint is null || !FixedEquals(payload.StateDigest, StateDigest(current)))
        {
            throw new McpInputException("StaleState", "Expected snapshot is no longer current and clean.");
        }
    }

    public string CreateCursor(Guid projectId, Guid? repositoryId, string filterHash, DateTimeOffset observedUtc, Guid recordId)
    {
        var payload = NewPayload("cursor", projectId, repositoryId, null, null) with
        {
            FilterHash = filterHash,
            ObservedUtcTicks = observedUtc.UtcTicks,
            RecordId = recordId.ToString("N")
        };
        return Sign(payload);
    }

    public (DateTimeOffset ObservedUtc, Guid RecordId) ReadCursor(string cursor, Guid projectId, Guid? repositoryId, string filterHash)
    {
        McpJson.Required(cursor, McpJson.Cursor, "cursor");
        var payload = Verify(cursor, "cursor", projectId, repositoryId);
        if (!FixedEquals(payload.FilterHash, filterHash)
            || payload.ObservedUtcTicks is null
            || !Guid.TryParseExact(payload.RecordId, "N", out var recordId))
        {
            throw new McpInputException("InvalidInput", "Cursor does not match the requested scope and filters.");
        }

        try
        {
            return (new DateTimeOffset(payload.ObservedUtcTicks.Value, TimeSpan.Zero), recordId);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new McpInputException("InvalidInput", "Cursor is invalid.");
        }
    }

    private SignedPayload NewPayload(string kind, Guid projectId, Guid? repositoryId, string? snapshotDigest, string? stateDigest)
    {
        var issued = _timeProvider.GetUtcNow();
        return new SignedPayload(
            kind,
            projectId.ToString("N"),
            repositoryId?.ToString("N"),
            snapshotDigest,
            stateDigest,
            issued.ToUnixTimeSeconds(),
            issued.Add(Lifetime).ToUnixTimeSeconds(),
            null,
            null,
            null);
    }

    private SignedPayload Verify(string token, string expectedKind, Guid projectId, Guid? repositoryId, bool requireRepository = true)
    {
        var pieces = token.Split('.', StringSplitOptions.None);
        if (pieces.Length != 2 || pieces[0].Length > 900 || pieces[1].Length > 100)
        {
            throw new McpInputException("StaleState", "Snapshot token is invalid or expired.");
        }

        byte[] payloadBytes;
        byte[] suppliedSignature;
        try
        {
            payloadBytes = FromBase64Url(pieces[0]);
            suppliedSignature = FromBase64Url(pieces[1]);
        }
        catch (FormatException)
        {
            throw new McpInputException("StaleState", "Snapshot token is invalid or expired.");
        }

        var expectedSignature = HMACSHA256.HashData(_key, payloadBytes);
        if (!CryptographicOperations.FixedTimeEquals(suppliedSignature, expectedSignature))
        {
            throw new McpInputException("StaleState", "Snapshot token is invalid or expired.");
        }

        SignedPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<SignedPayload>(payloadBytes, McpJson.Options);
        }
        catch (JsonException)
        {
            throw new McpInputException("StaleState", "Snapshot token is invalid or expired.");
        }

        var now = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        if (payload is null
            || payload.Kind != expectedKind
            || !string.Equals(payload.ProjectId, projectId.ToString("N"), StringComparison.Ordinal)
            || (requireRepository && !string.Equals(payload.RepositoryId, repositoryId?.ToString("N"), StringComparison.Ordinal))
            || payload.ExpiresUtc <= now
            || payload.ExpiresUtc < payload.IssuedUtc
            || payload.IssuedUtc > now + 60)
        {
            throw new McpInputException("StaleState", "Snapshot token is invalid or expired.");
        }

        return payload;
    }

    private string Sign(SignedPayload payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, McpJson.Options);
        var signature = HMACSHA256.HashData(_key, bytes);
        return $"{ToBase64Url(bytes)}.{ToBase64Url(signature)}";
    }

    private static string SnapshotDigest(Snapshot snapshot) => Digest(
        snapshot.CanonicalRoot,
        snapshot.Branch,
        snapshot.Head,
        snapshot.WorkingTreeFingerprint,
        snapshot.ObservedUtc.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture),
        snapshot.Completeness);

    private static string StateDigest(Snapshot snapshot) => Digest(
        snapshot.CanonicalRoot,
        snapshot.Branch,
        snapshot.Head,
        snapshot.WorkingTreeFingerprint,
        snapshot.Completeness);

    private static string Digest(params string?[] values)
    {
        var joined = string.Join("\0", values.Select(value => value ?? "<null>"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined))).ToLowerInvariant();
    }

    private static bool FixedEquals(string? left, string? right) => left is not null && right is not null && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right));

    private static string ToBase64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized = normalized.PadRight(normalized.Length + (4 - normalized.Length % 4) % 4, '=');
        return Convert.FromBase64String(normalized);
    }

    private sealed record SignedPayload(
        string Kind,
        string ProjectId,
        string? RepositoryId,
        string? SnapshotDigest,
        string? StateDigest,
        long IssuedUtc,
        long ExpiresUtc,
        string? FilterHash,
        long? ObservedUtcTicks,
        string? RecordId);
}
