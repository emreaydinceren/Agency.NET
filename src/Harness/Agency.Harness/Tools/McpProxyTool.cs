using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Agency.Harness.Tools;

/// <summary>
/// Adapts an MCP SDK <see cref="McpClientTool"/> to the Agency framework's <see cref="ITool"/> interface,
/// enabling MCP server tools to be invoked transparently by the agent loop.
/// </summary>
internal sealed class McpProxyTool : ITool
{
    private readonly McpClientTool _mcpTool;
    private readonly ToolDefinition _definition;

    internal McpProxyTool(McpClientTool mcpTool)
    {
        this._mcpTool = mcpTool;
        this._definition = new ToolDefinition(
            this._mcpTool.Name,
            this._mcpTool.Description ?? string.Empty,
            this._mcpTool.ProtocolTool.InputSchema);
    }

    public ToolDefinition Definition => this._definition;

    public async Task<ToolResult> InvokeAsync(JsonElement input, CancellationToken ct)
    {
        Dictionary<string, object?> args = JsonSerializer.Deserialize<Dictionary<string, object?>>(input) ?? [];
        CallToolResult result = await this._mcpTool.CallAsync(args, cancellationToken: ct);
        string content = string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        return new ToolResult(content, result.IsError is true);
    }
}