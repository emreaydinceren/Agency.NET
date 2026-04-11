using System.Text.Json;

namespace Agency.Agentic.Tools;
// ITool, IToolRegistry, ToolDefinition, ToolResult come from Agency.Llm.Common.Tools via global usings.

/// <summary>No-op registry used when no tools are registered.</summary>
internal sealed class EmptyToolRegistry : IToolRegistry
{
    public static EmptyToolRegistry Instance { get; } = new();
    public IReadOnlyList<ToolDefinition> ListDefinitions() => [];
    public Task<ToolResult> InvokeAsync(string name, JsonElement input, CancellationToken ct)
        => Task.FromResult(new ToolResult($"No tool registered with name '{name}'.", IsError: true));
}

/// <summary>
/// Default in-memory <see cref="IToolRegistry"/> backed by a name-keyed dictionary.
/// Tool invocations are dispatched by name; unknown names return an error result.
/// </summary>
public sealed class ToolRegistry : IToolRegistry
{
    private readonly Dictionary<string, ITool> _tools;

    /// <param name="tools">The tools to register.</param>
    public ToolRegistry(IEnumerable<ITool> tools)
    {
        this._tools = tools.ToDictionary(t => t.Definition.Name);
    }

    /// <inheritdoc/>
    public IReadOnlyList<ToolDefinition> ListDefinitions() =>
        this._tools.Values.Select(t => t.Definition).ToList();

    /// <inheritdoc/>
    public async Task<ToolResult> InvokeAsync(string name, JsonElement input, CancellationToken ct)
    {
        if (!this._tools.TryGetValue(name, out ITool? tool))
        {
            return new ToolResult($"No tool registered with name '{name}'.", IsError: true);
        }

        return await tool.InvokeAsync(input, ct);
    }
}
