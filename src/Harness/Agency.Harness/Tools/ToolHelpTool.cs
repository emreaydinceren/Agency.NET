using System.Text.Json;

namespace Agency.Harness.Tools;

/// <summary>
/// A meta-tool that returns a named tool's full parameter schema and description on demand.
/// The LLM should call this before invoking any tool whose parameters it does not know.
/// </summary>
internal sealed class ToolHelpTool : ITool
{
    private readonly IToolRegistry _inner;

    private static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true };

    private static readonly JsonElement InputSchema = JsonDocument.Parse(@"{
        ""type"": ""object"",
        ""properties"": {
            ""name"": {
                ""type"": ""string"",
                ""description"": ""The exact name of the tool to get parameter details for.""
            }
        },
        ""required"": [""name""]
    }").RootElement.Clone();

    internal ToolHelpTool(IToolRegistry inner)
    {
        this._inner = inner;
    }

    public ToolDefinition Definition => new(
        "tool_help",
        "Reveals the full parameter schema and description for a named tool. " +
        "Call this before invoking a tool whose parameters you do not know. " +
        "Pass the exact tool name in the 'name' parameter.",
        InputSchema);

    public Task<ToolResult> InvokeAsync(JsonElement input, CancellationToken ct)
    {
        if (!input.TryGetProperty("name", out JsonElement nameEl) ||
            nameEl.ValueKind != JsonValueKind.String ||
            string.IsNullOrEmpty(nameEl.GetString()))
        {
            return Task.FromResult(new ToolResult("Parameter 'name' is required.", IsError: true));
        }

        string name = nameEl.GetString()!;

        IReadOnlyList<ToolDefinition> defs = this._inner.ListDefinitions();
        ToolDefinition? found = null;
        foreach (ToolDefinition def in defs)
        {
            if (def.Name == name)
            {
                found = def;
                break;
            }
        }

        if (found is null)
        {
            string available = string.Join(", ", defs.Select(static d => d.Name));
            return Task.FromResult(new ToolResult(
                $"No tool named '{name}'. Available tools: {available}",
                IsError: true));
        }

        string schemaJson = JsonSerializer.Serialize(found.InputSchema, IndentedOptions);
        string content = $"{found.Description}\n\n{schemaJson}";
        return Task.FromResult(new ToolResult(content, IsError: false));
    }
}
