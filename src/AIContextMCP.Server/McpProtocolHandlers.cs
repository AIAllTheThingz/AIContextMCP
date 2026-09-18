using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AIContextMCP.Server;

internal static class McpProtocolHandlers
{
    public static ValueTask<ListToolsResult> ListToolsAsync(RequestContext<ListToolsRequestParams> context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var adapter = (context.Services ?? throw new InvalidOperationException("MCP services are unavailable.")).GetRequiredService<McpToolAdapter>();
        return ValueTask.FromResult(new ListToolsResult
        {
            Tools = adapter.Tools.Select(tool => new Tool
            {
                Name = tool.Name,
                Description = tool.Description,
                InputSchema = tool.InputSchema,
                OutputSchema = tool.OutputSchema,
                Annotations = new ToolAnnotations
                {
                    ReadOnlyHint = tool.ReadOnly,
                    DestructiveHint = false,
                    IdempotentHint = tool.ReadOnly,
                    OpenWorldHint = false
                }
            }).ToList()
        });
    }

    public static async ValueTask<CallToolResult> CallToolAsync(RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken)
    {
        var request = context.Params;
        IReadOnlyDictionary<string, JsonElement>? arguments = request?.Arguments is { } supplied
            ? new Dictionary<string, JsonElement>(supplied, StringComparer.Ordinal)
            : null;
        var adapter = (context.Services ?? throw new InvalidOperationException("MCP services are unavailable.")).GetRequiredService<McpToolAdapter>();
        var result = await adapter.InvokeAsync(request?.Name, arguments, cancellationToken);
        using var document = JsonDocument.Parse(result.Json);
        return new CallToolResult
        {
            IsError = result.IsError,
            StructuredContent = document.RootElement.Clone(),
            Content = [new TextContentBlock { Text = result.Json }]
        };
    }
}
