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
        var tools = McpToolCatalog.All;
        Assert.Equal(8, tools.Count);
        Assert.Equal(8, tools.Select(tool => tool.Name).Distinct(StringComparer.Ordinal).Count());

        var context = Assert.Single(tools, tool => tool.Name == "context.record");
        Assert.Equal("object", context.InputSchema.GetProperty("type").GetString());
        Assert.False(context.InputSchema.GetProperty("additionalProperties").GetBoolean());
        Assert.Contains("requestId", context.InputSchema.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(8_192, context.InputSchema.GetProperty("properties").GetProperty("summary").GetProperty("maxLength").GetInt32());
        Assert.Equal(3, context.InputSchema.GetProperty("properties").GetProperty("tier").GetProperty("maximum").GetInt32());
        Assert.False(FindObjectWithProperty(context.InputSchema, "kind").GetProperty("additionalProperties").GetBoolean());
        Assert.Contains("Active", context.InputSchema.GetRawText(), StringComparison.Ordinal);
        var bootstrap = Assert.Single(tools, tool => tool.Name == "project.bootstrap");
        Assert.Contains("Unknown", bootstrap.OutputSchema.GetRawText(), StringComparison.Ordinal);
        Assert.False(context.OutputSchema.GetProperty("additionalProperties").GetBoolean());
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
}
