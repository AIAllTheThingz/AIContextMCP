using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using AIContextMCP.Core;

namespace AIContextMCP.Server;

public sealed record McpEnvelope(bool Ok, JsonElement? Data, McpError? Error, int SchemaVersion, string CorrelationId);
public sealed record McpError(string Code, string Message, string CorrelationId, bool Retryable);

public sealed record Snapshot(
    string SnapshotToken,
    string CanonicalRoot,
    string? WorkingTreeFingerprint,
    DateTimeOffset ObservedUtc,
    string Completeness,
    string? Branch = null,
    string? Head = null);

public sealed record Reference(
    string ProjectId,
    string Kind,
    string? RepositoryId = null,
    string? Id = null,
    string? RelativePath = null,
    string? Uri = null,
    string? Hash = null);

public sealed record Source(string Kind, DateTimeOffset ObservedUtc, Reference? Reference = null);

public sealed record ProjectRef(
    string? RequestId = null,
    string? ProjectId = null,
    string? RepositoryPath = null,
    string? Remote = null,
    bool IncludeWorkingTree = true,
    bool Register = false);

public sealed record ContextSearch(
    string ProjectId,
    string? RepositoryId = null,
    string? Category = null,
    string? Branch = null,
    string? Commit = null,
    ContextStatus? Status = null,
    DateTimeOffset? RecencySinceUtc = null,
    string? Query = null,
    int MaxResults = 20,
    int MaxBytes = 16 * 1024,
    string? Cursor = null,
    string? FindingId = null);

public sealed record ContextRecord(
    string RequestId,
    string ProjectId,
    string Category,
    string Summary,
    Source Source,
    Snapshot ObservedSnapshot,
    string? RepositoryId = null,
    string? PhaseId = null,
    string? Name = null,
    string? Objective = null,
    PhaseStatus? PhaseStatus = null,
    int Tier = 2,
    string? Content = null,
    string? ArtifactRef = null,
    string? SupersedesId = null,
    int? ExpectedVersion = null,
    string? ExpectedSnapshotToken = null);

public sealed record DecisionList(
    string ProjectId,
    string? RepositoryId = null,
    DecisionStatus[]? Statuses = null,
    bool IncludeSuperseded = false,
    int MaxResults = 50,
    string? Cursor = null);

public sealed record DecisionRecord(
    string RequestId,
    string ProjectId,
    string Title,
    string Decision,
    string Rationale,
    Source Source,
    Snapshot ObservedSnapshot,
    string? RepositoryId = null,
    DecisionStatus Status = DecisionStatus.Accepted,
    string? SupersedesId = null,
    string? ResolutionEvidence = null,
    int? ExpectedVersion = null,
    string? ExpectedSnapshotToken = null);

public sealed record TestRecord(
    string RequestId,
    string ProjectId,
    string Name,
    TestRunStatus Status,
    long Passed,
    long Failed,
    long Skipped,
    string Summary,
    string CommandData,
    Source Source,
    Snapshot RunSnapshot,
    DateTimeOffset ObservedUtc,
    string? RepositoryId = null,
    long? DurationMs = null,
    string? Evidence = null,
    string? ArtifactRef = null,
    string? ExpectedSnapshotToken = null,
    string? SupersedesId = null);

public sealed record FindingRecord(
    string RequestId,
    string ProjectId,
    string Title,
    FindingSeverity Severity,
    FindingStatus Status,
    string Description,
    Source Source,
    Snapshot ObservedSnapshot,
    string? RepositoryId = null,
    string? FindingId = null,
    string? Component = null,
    string? Location = null,
    string? Remediation = null,
    string? ResolutionEvidence = null,
    int? ExpectedVersion = null,
    string? ExpectedSnapshotToken = null);

public sealed record HandoffCreate(
    string RequestId,
    string ProjectId,
    string Objective,
    string CompletedWork,
    string ActiveWork,
    Reference[] Blockers,
    Reference[] Decisions,
    Reference[] Tests,
    Reference[] Findings,
    Reference[] RelevantFiles,
    Reference[] Artifacts,
    string NextAction,
    Snapshot ObservedSnapshot,
    string? RepositoryId = null,
    string? PhaseId = null,
    string? ExpectedSnapshotToken = null);

public sealed record RecordReceipt(string EntryId, string State, DateTimeOffset CreatedAtUtc, int Version);
public sealed record DecisionReceipt(string DecisionId, string State, DateTimeOffset CreatedAtUtc, int Version);
public sealed record TestReceipt(string TestRunId, string State, DateTimeOffset CreatedAtUtc, int Version, string? SupersedesId = null);
public sealed record FindingReceipt(string FindingId, string State, DateTimeOffset CreatedAtUtc, int Version);
public sealed record HandoffReceipt(string HandoffId, string State, DateTimeOffset CreatedAtUtc, int Version);
public sealed record HandoffSummary(string HandoffId, string Objective, string NextAction, string? PhaseId, DateTimeOffset CreatedAtUtc, RecordFreshness Freshness);

public sealed record Bootstrap(
    ProjectIdentity Project,
    RepositoryIdentity Repository,
    Snapshot Git,
    BootstrapItem? Phase,
    IReadOnlyList<BootstrapItem> Blockers,
    IReadOnlyList<BootstrapItem> Findings,
    BootstrapValidation? Validation,
    IReadOnlyList<BootstrapItem> Decisions,
    IReadOnlyList<BootstrapReference> References,
    HandoffSummary? Handoff,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> States);

public sealed record McpContextItem(
    string EntryId,
    string Category,
    string Title,
    string Summary,
    string? Branch,
    string? Commit,
    ContextStatus Status,
    int Tier,
    string? Objective,
    string? PhaseId,
    DateTimeOffset ObservedUtc,
    RecordFreshness Freshness,
    Source? Source = null,
    IReadOnlyList<Reference>? References = null);

public sealed record McpFindingItem(
    string FindingId,
    int Revision,
    string Title,
    FindingSeverity Severity,
    FindingStatus Status,
    string Description,
    string? Remediation,
    string? ResolutionEvidence,
    string? Branch,
    string? Commit,
    RecordFreshness Freshness,
    string? Provenance);

public sealed record McpDecisionItem(
    string DecisionId,
    string Title,
    string Decision,
    DecisionStatus Status,
    int Version,
    string? SupersedesId,
    string? SupersededById,
    string? ResolutionEvidence,
    DateTimeOffset ObservedUtc,
    RecordFreshness Freshness,
    Source? Source = null,
    IReadOnlyList<Reference>? References = null);

public sealed record SearchResult(IReadOnlyList<McpContextItem> Entries, string? NextCursor, bool Truncated, IReadOnlyList<string> Warnings, IReadOnlyList<McpFindingItem> Findings)
{
    public SearchResult(IReadOnlyList<McpContextItem> entries, string? nextCursor, bool truncated, IReadOnlyList<string> warnings)
        : this(entries, nextCursor, truncated, warnings, []) { }

    public void Deconstruct(out IReadOnlyList<McpContextItem> entries, out string? nextCursor, out bool truncated, out IReadOnlyList<string> warnings) =>
        (entries, nextCursor, truncated, warnings) = (Entries, NextCursor, Truncated, Warnings);
}
public sealed record DecisionListResult(IReadOnlyList<McpDecisionItem> Decisions, string? NextCursor, bool Truncated);

internal sealed class McpInputException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public static class McpJson
{
    public const int RequestBytes = 64 * 1024;
    public const int ResponseBytes = 32 * 1024;
    public const int BootstrapResponseBytes = 16 * 1024;
    public const int MinimumResponseBytes = 8 * 1024;
    public const int DefaultString = StorageLimits.ShortText;
    public const int Identifier = StorageLimits.Identifier;
    public const int Cursor = StorageLimits.ShortText;

    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = false,
        MaxDepth = 32,
        NumberHandling = JsonNumberHandling.Strict,
        PropertyNameCaseInsensitive = false,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false
    };

    internal static readonly JsonSerializerOptions SchemaOptions = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = false,
        MaxDepth = 32,
        NumberHandling = JsonNumberHandling.Strict,
        PropertyNameCaseInsensitive = false,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        WriteIndented = false
    };

    static McpJson()
    {
        Options.Converters.Add(new StrictEnumConverterFactory());
        SchemaOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public static T Deserialize<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new McpInputException("InvalidInput", "Request arguments are required.");
        if (Encoding.UTF8.GetByteCount(json) > RequestBytes) throw new McpInputException("LimitExceeded", "Request exceeds 64 KiB.");
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 32 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new McpInputException("InvalidInput", "Request arguments must be an object.");
            RejectDuplicateProperties(document.RootElement);
            return document.RootElement.Deserialize<T>(Options) ?? throw new McpInputException("InvalidInput", "Request arguments cannot be null.");
        }
        catch (McpInputException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw new McpInputException("InvalidInput", "Request JSON is invalid.");
        }
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static string ArgumentsJson(IReadOnlyDictionary<string, JsonElement>? arguments)
    {
        var json = JsonSerializer.Serialize(arguments ?? new Dictionary<string, JsonElement>(), Options);
        if (Encoding.UTF8.GetByteCount(json) > RequestBytes) throw new McpInputException("LimitExceeded", "Request exceeds 64 KiB.");
        return json;
    }

    public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, Options);

    public static string CanonicalPayloadHash(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteCanonical(writer, value);
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    public static void Required(string? value, int maximum, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl))
        {
            throw new McpInputException(value is { Length: > 0 } && value.Length > maximum ? "LimitExceeded" : "InvalidInput", $"{name} is invalid.");
        }

        RejectSecret(value, name);
    }

    public static void Optional(string? value, int maximum, string name)
    {
        if (value is null) return;
        Required(value, maximum, name);
    }

    public static void RequiredText(string? value, int maximum, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
        {
            throw new McpInputException(value is { Length: > 0 } && value.Length > maximum ? "LimitExceeded" : "InvalidInput", $"{name} is invalid.");
        }

        RejectSecret(value, name);
    }

    public static void OptionalText(string? value, int maximum, string name)
    {
        if (value is not null) RequiredText(value, maximum, name);
    }

    public static Guid RequiredId(string? value, string name)
    {
        Required(value, Identifier, name);
        if (!Guid.TryParse(value, out var id) || id == Guid.Empty) throw new McpInputException("InvalidInput", $"{name} is invalid.");
        return id;
    }

    public static Guid? OptionalId(string? value, string name) => value is null ? null : RequiredId(value, name);

    public static void Timestamp(DateTimeOffset value, string name)
    {
        if (value.Offset != TimeSpan.Zero) throw new McpInputException("InvalidInput", $"{name} must be UTC.");
    }

    public static void SnapshotShape(Snapshot snapshot, string name)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Required(snapshot.SnapshotToken, Cursor, $"{name} token");
        Required(snapshot.CanonicalRoot, Identifier, $"{name} root");
        Optional(snapshot.Branch, Identifier, $"{name} branch");
        Optional(snapshot.Head, Identifier, $"{name} head");
        Optional(snapshot.WorkingTreeFingerprint, Identifier, $"{name} fingerprint");
        Timestamp(snapshot.ObservedUtc, $"{name} observed time");
        if (snapshot.Completeness is not ("Clean" or "Dirty" or "Unknown")) throw new McpInputException("InvalidInput", $"{name} completeness is invalid.");
    }

    public static void SourceShape(Source source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Required(source.Kind, DefaultString, "source kind");
        Timestamp(source.ObservedUtc, "source observed time");
        if (source.Reference is not null) ReferenceShape(source.Reference);
    }

    public static void ReferenceShape(Reference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        RequiredId(reference.ProjectId, "reference project id");
        OptionalId(reference.RepositoryId, "reference repository id");
        Required(reference.Kind, DefaultString, "reference kind");
        OptionalId(reference.Id, "reference id");
        Optional(reference.RelativePath, Identifier, "reference relative path");
        Optional(reference.Uri, Identifier, "reference uri");
        Optional(reference.Hash, Identifier, "reference hash");
        if (reference.Kind is not ("repository-document" or "artifact" or "record" or "external"))
        {
            throw new McpInputException("InvalidInput", "Reference kind is invalid.");
        }
    }

    public static void References(IReadOnlyList<Reference>? references, string name)
    {
        if (references is null || references.Count > StorageLimits.CollectionCount)
        {
            throw new McpInputException("LimitExceeded", $"{name} exceeds its limit.");
        }

        foreach (var reference in references) ReferenceShape(reference);
    }

    public static void RequestId(string? requestId) => Required(requestId, Identifier, "request id");

    public static void ExpectedVersion(int? expectedVersion)
    {
        if (expectedVersion is <= 0) throw new McpInputException("InvalidInput", "Expected version is invalid.");
    }

    public static void Limit(int value, string name)
    {
        if (value is < 1 or > StorageLimits.CollectionCount) throw new McpInputException("LimitExceeded", $"{name} exceeds its limit.");
    }

    public static void MaximumBytes(int value)
    {
        if (value is < MinimumResponseBytes or > ResponseBytes) throw new McpInputException("LimitExceeded", $"maxBytes must be between {MinimumResponseBytes} and {ResponseBytes} bytes.");
    }

    private static void RejectSecret(string value, string name)
    {
        if (SecretValueClassifier.IsSecretShaped(value)) throw new McpInputException("SecretRejected", $"{name} contains secret-shaped content.");
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new McpInputException("InvalidInput", "Request JSON contains duplicate properties.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
        }
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }

    private sealed class StrictEnumConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(typeof(StrictEnumConverter<>).MakeGenericType(typeToConvert))!;
    }

    private sealed class StrictEnumConverter<TEnum> : JsonConverter<TEnum> where TEnum : struct, Enum
    {
        public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String) throw new JsonException("Enum values must be strings.");
            var value = reader.GetString();
            if (value is null || !Enum.GetNames<TEnum>().Contains(value, StringComparer.Ordinal))
            {
                throw new JsonException("Enum value is invalid.");
            }

            return Enum.Parse<TEnum>(value, ignoreCase: false);
        }

        public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
    }
}
