using System.Text.Json;
using AIContextMCP.Server;
using Xunit;

namespace AIContextMCP.Server.Tests;

public sealed class McpContractTests
{
    [Theory]
    [InlineData("""{"projectId":"p","maxResults":20}""")]
    [InlineData("""{"projectId":"p","maxResults":1}""")]
    public void Strict_json_accepts_valid_requests(string json) => Assert.NotNull(McpJson.Deserialize<ContextSearch>(json));

    [Theory]
    [InlineData("""{"projectId":"p","unknown":true}""")]
    [InlineData("""{"projectId":"p","maxResults":1.5}""")]
    public void Strict_json_rejects_unknown_or_numeric_shape(string json) => Assert.Throws<McpInputException>(() => McpJson.Deserialize<ContextSearch>(json));

    [Fact]
    public void Payload_limit_is_enforced()
    {
        var json = "{\"projectId\":\"" + new string('x', 64 * 1024) + "\"}";
        Assert.Throws<McpInputException>(() => McpJson.Deserialize<ContextSearch>(json));
    }

    [Fact]
    public void Strict_json_rejects_combined_enum_names()
    {
        Assert.Throws<McpInputException>(() => McpJson.Deserialize<DecisionList>("""{"projectId":"p","statuses":["Accepted,Rejected"]}"""));
    }

    [Fact]
    public void Catalog_has_eight_typed_strict_input_and_output_schemas()
    {
        var contracts = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["project.bootstrap"] = [],
            ["context.search"] = ["projectId"],
            ["context.record"] = ["requestId", "projectId", "category", "summary", "source", "observedSnapshot"],
            ["decision.list"] = ["projectId"],
            ["decision.record"] = ["requestId", "projectId", "title", "decision", "rationale", "source", "observedSnapshot"],
            ["test.record"] = ["requestId", "projectId", "name", "status", "passed", "failed", "skipped", "summary", "commandData", "source", "runSnapshot", "observedUtc"],
            ["finding.record"] = ["requestId", "projectId", "title", "severity", "status", "description", "source", "observedSnapshot"],
            ["handoff.create"] = ["requestId", "projectId", "objective", "completedWork", "activeWork", "blockers", "decisions", "tests", "findings", "relevantFiles", "artifacts", "nextAction", "observedSnapshot"]
        };
        var tools = McpToolCatalog.All;
        Assert.Equal(contracts.Count, tools.Count);
        foreach (var (name, required) in contracts)
        {
            var tool = Assert.Single(tools, tool => tool.Name == name);
            Assert.False(string.IsNullOrWhiteSpace(tool.Description));
            Assert.Equal("object", tool.InputSchema.GetProperty("type").GetString());
            Assert.Equal(required, RequiredProperties(tool.InputSchema));
            Assert.False(tool.InputSchema.GetProperty("additionalProperties").GetBoolean());
            Assert.Equal("object", tool.OutputSchema.GetProperty("type").GetString());
            Assert.False(tool.OutputSchema.GetProperty("additionalProperties").GetBoolean());
        }

        var context = Assert.Single(tools, tool => tool.Name == "context.record");
        Assert.Equal(8_192, context.InputSchema.GetProperty("properties").GetProperty("summary").GetProperty("maxLength").GetInt32());
        Assert.Equal(3, context.InputSchema.GetProperty("properties").GetProperty("tier").GetProperty("maximum").GetInt32());
        Assert.Equal(2, context.InputSchema.GetProperty("properties").GetProperty("tier").GetProperty("default").GetInt32());
        Assert.False(FindObjectWithProperty(context.InputSchema, "kind").GetProperty("additionalProperties").GetBoolean());
        Assert.Contains("Active", context.InputSchema.GetRawText(), StringComparison.Ordinal);

        var bootstrap = Assert.Single(tools, tool => tool.Name == "project.bootstrap");
        var bootstrapProperties = bootstrap.InputSchema.GetProperty("properties");
        Assert.Contains("register=false", bootstrap.Description, StringComparison.Ordinal);
        Assert.True(AllowsNull(bootstrapProperties.GetProperty("requestId")));
        Assert.False(AllowsNull(bootstrapProperties.GetProperty("register")));
        Assert.True(bootstrapProperties.GetProperty("includeWorkingTree").GetProperty("default").GetBoolean());
        Assert.False(bootstrapProperties.GetProperty("register").GetProperty("default").GetBoolean());
        var requestIdDescription = bootstrapProperties.GetProperty("requestId").GetProperty("description").GetString();
        Assert.Contains("<unix-seconds>:<lowercase-D-UUID>", requestIdDescription, StringComparison.Ordinal);
        Assert.Contains("-30d/+60s", requestIdDescription, StringComparison.Ordinal);
        var conditional = bootstrap.InputSchema.GetProperty("allOf")[0];
        Assert.Contains("register", conditional.GetProperty("if").GetProperty("required").EnumerateArray().Select(value => value.GetString()));
        Assert.Contains("requestId", conditional.GetProperty("then").GetProperty("required").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal("^(?:0|[1-9][0-9]{0,10}):[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}$", conditional.GetProperty("then").GetProperty("properties").GetProperty("requestId").GetProperty("pattern").GetString());

        var search = Assert.Single(tools, tool => tool.Name == "context.search").InputSchema.GetProperty("properties");
        Assert.True(AllowsNull(search.GetProperty("repositoryId")));
        Assert.False(AllowsNull(search.GetProperty("maxResults")));
        Assert.Equal(20, search.GetProperty("maxResults").GetProperty("default").GetInt32());
        Assert.Equal(16 * 1024, search.GetProperty("maxBytes").GetProperty("default").GetInt32());

        var decisions = Assert.Single(tools, tool => tool.Name == "decision.list").InputSchema.GetProperty("properties");
        Assert.True(AllowsNull(decisions.GetProperty("statuses")));
        Assert.False(AllowsNull(decisions.GetProperty("includeSuperseded")));
        Assert.False(decisions.GetProperty("includeSuperseded").GetProperty("default").GetBoolean());
        Assert.Equal(50, decisions.GetProperty("maxResults").GetProperty("default").GetInt32());

        var decisionRecord = Assert.Single(tools, tool => tool.Name == "decision.record").InputSchema.GetProperty("properties");
        Assert.True(AllowsNull(decisionRecord.GetProperty("expectedVersion")));
        Assert.Equal("Accepted", decisionRecord.GetProperty("status").GetProperty("default").GetString());
        Assert.Contains("Unknown", bootstrap.OutputSchema.GetRawText(), StringComparison.Ordinal);
        Assert.False(FindObjectWithProperty(Assert.Single(tools, tool => tool.Name == "context.search").OutputSchema, "tier").GetProperty("properties").GetProperty("tier").TryGetProperty("default", out _));
    }

    [Fact]
    public void Defaults_and_nullability_match_deserialization()
    {
        var bootstrap = McpJson.Deserialize<ProjectRef>("""{"repositoryPath":"repo"}""");
        Assert.False(bootstrap.Register);
        Assert.True(bootstrap.IncludeWorkingTree);
        Assert.Null(bootstrap.RequestId);

        var search = McpJson.Deserialize<ContextSearch>("""{"projectId":"p","repositoryId":null}""");
        Assert.Null(search.RepositoryId);
        Assert.Equal(20, search.MaxResults);
        Assert.Equal(16 * 1024, search.MaxBytes);

        var decisions = McpJson.Deserialize<DecisionList>("""{"projectId":"p","statuses":null}""");
        Assert.Null(decisions.Statuses);
        Assert.False(decisions.IncludeSuperseded);
        Assert.Equal(50, decisions.MaxResults);

        var decision = McpJson.Deserialize<DecisionRecord>("""{"requestId":"r","projectId":"p","title":"t","decision":"d","rationale":"r","source":{"kind":"test","observedUtc":"2026-01-01T00:00:00Z"},"observedSnapshot":{"snapshotToken":"token","canonicalRoot":"root","workingTreeFingerprint":null,"observedUtc":"2026-01-01T00:00:00Z","completeness":"Clean"},"expectedVersion":null}""");
        Assert.Null(decision.ExpectedVersion);

        Assert.Throws<McpInputException>(() => McpJson.Deserialize<ProjectRef>("""{"repositoryPath":"repo","register":null}"""));
        Assert.Throws<McpInputException>(() => McpJson.Deserialize<ContextSearch>("""{"projectId":"p","maxResults":null}"""));
        Assert.Throws<McpInputException>(() => McpJson.Deserialize<ContextSearch>("""{"projectId":1}"""));
    }

    private static JsonElement FindObjectWithProperty(JsonElement value, string property)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("properties", out var properties) && properties.TryGetProperty(property, out _)) return value;
            foreach (var item in value.EnumerateObject())
            {
                var match = TryFind(item.Value, property);
                if (match is { } found) return found;
            }
        }

        throw new Xunit.Sdk.XunitException($"No schema object contains '{property}'.");
    }

    private static JsonElement? TryFind(JsonElement value, string property)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("properties", out var properties) && properties.TryGetProperty(property, out _)) return value;
            foreach (var item in value.EnumerateObject())
            {
                var match = TryFind(item.Value, property);
                if (match is not null) return match;
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                var match = TryFind(item, property);
                if (match is not null) return match;
            }
        }

        return null;
    }

    private static string[] RequiredProperties(JsonElement schema) => schema.TryGetProperty("required", out var required)
        ? required.EnumerateArray().Select(value => value.GetString()!).ToArray()
        : [];

    private static bool AllowsNull(JsonElement schema) =>
        schema.TryGetProperty("type", out var type)
            && (type.ValueKind == JsonValueKind.String ? type.GetString() == "null" : type.EnumerateArray().Any(value => value.GetString() == "null"))
        || schema.TryGetProperty("enum", out var values) && values.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.Null);
}
