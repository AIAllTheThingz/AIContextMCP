using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using AIContextMCP.Core;

namespace AIContextMCP.Server;

internal sealed record McpToolDescriptor(string Name, string Description, bool ReadOnly, JsonElement InputSchema, JsonElement OutputSchema);

internal static class McpToolCatalog
{
    private static readonly IReadOnlyList<McpToolDescriptor> Items =
    [
        Tool<ProjectRef, Bootstrap>("project.bootstrap", "Inspect a registered repository and optionally register it with a durable receipt.", false),
        Tool<ContextSearch, SearchResult>("context.search", "Search bounded project context records in observed-time order.", true),
        Tool<ContextRecord, RecordReceipt>("context.record", "Record bounded repository context and its verified observation.", false),
        Tool<DecisionList, DecisionListResult>("decision.list", "List bounded project decisions in observed-time order.", true),
        Tool<DecisionRecord, DecisionReceipt>("decision.record", "Record a versioned decision and verified observation.", false),
        Tool<TestRecord, TestReceipt>("test.record", "Record historical test evidence without executing a command.", false),
        Tool<FindingRecord, FindingReceipt>("finding.record", "Create or revise an immutable finding history with a verified observation.", false),
        Tool<HandoffCreate, HandoffReceipt>("handoff.create", "Create a bounded handoff with same-scope references and a verified observation.", false)
    ];

    public static IReadOnlyList<McpToolDescriptor> All => Items;

    public static bool Contains(string? name) => Items.Any(item => string.Equals(item.Name, name, StringComparison.Ordinal));

    private static McpToolDescriptor Tool<TInput, TOutput>(string name, string description, bool readOnly) where TOutput : class
    {
        return new McpToolDescriptor(name, description, readOnly, Schema<TInput>(), Schema<ToolEnvelope<TOutput>>());
    }

    private static JsonElement Schema<T>()
    {
        var schema = McpJson.SchemaOptions.GetJsonSchemaAsNode(typeof(T), new JsonSchemaExporterOptions { TreatNullObliviousAsNonNullable = true })?.AsObject()
            ?? throw new InvalidOperationException("Schema could not be generated.");
        Constrain(schema, null);
        return JsonSerializer.SerializeToElement<JsonNode>(schema, McpJson.Options);
    }

    private static void Constrain(JsonNode? node, string? name)
    {
        if (node is JsonArray array)
        {
            foreach (var item in array) Constrain(item, name);
            return;
        }

        if (node is not JsonObject value) return;
        if (value["properties"] is JsonObject) value["additionalProperties"] = false;
        if (value["type"] is JsonValue type && type.TryGetValue<string>(out var typeName))
        {
            if (typeName == "string" && value["maxLength"] is null) value["maxLength"] = StringLimit(name);
            if (typeName == "array" && value["maxItems"] is null) value["maxItems"] = StorageLimits.CollectionCount;
        }

        if (name is "maxResults")
        {
            value["minimum"] = 1;
            value["maximum"] = StorageLimits.CollectionCount;
        }
        else if (name is "maxBytes")
        {
            value["minimum"] = 1;
            value["maximum"] = McpJson.ResponseBytes;
        }
        else if (name is "tier")
        {
            value["minimum"] = 1;
            value["maximum"] = 3;
        }
        else if (name is "durationMs")
        {
            value["minimum"] = 0;
            value["maximum"] = 604_800_000;
        }
        else if (name is "passed" or "failed" or "skipped")
        {
            value["minimum"] = 0;
            value["maximum"] = 1_000_000_000;
        }
        else if (name is "expectedVersion")
        {
            value["minimum"] = 1;
        }

        foreach (var property in value.ToArray()) Constrain(property.Value, property.Key);
    }

    private static int StringLimit(string? name) => name switch
    {
        "title" or "name" => StorageLimits.Title,
        "summary" or "objective" or "completedWork" or "activeWork" or "nextAction" => StorageLimits.HandoffField,
        "content" or "description" or "decision" or "evidence" or "remediation" or "resolutionEvidence" => StorageLimits.Content,
        "rationale" => StorageLimits.Rationale,
        "commandData" => StorageLimits.Command,
        "query" => 256,
        "projectId" or "repositoryId" or "phaseId" or "supersedesId" or "findingId" or "requestId" or "repositoryPath" or "remote" or "relativePath" or "uri" or "hash" => McpJson.Identifier,
        _ => McpJson.DefaultString
    };

    private sealed record ToolEnvelope<T>(bool Ok, T? Data, McpError? Error, int SchemaVersion, string CorrelationId) where T : class;
}
