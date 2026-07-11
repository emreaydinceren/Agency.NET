using System.Text.Json;

namespace Agency.Harness.Tools;

// ITool, IToolRegistry, ToolDefinition, ToolResult come from Agency.Llm.Common.Tools via global usings.

/// <summary>No-op registry used when no tools are registered.</summary>
internal sealed class EmptyToolRegistry : IToolRegistry
{
    public static EmptyToolRegistry Instance { get; } = new();
    public void Register(ITool tool) { }
    public IReadOnlyList<ToolDefinition> ListDefinitions() => [];
    public Task<ToolResult> InvokeAsync(string name, JsonElement input, CancellationToken ct)
        => Task.FromResult(new ToolResult($"No tool registered with name '{name}'.", IsError: true));

    public IReadOnlyList<(bool Enabled, ToolDefinition Definition)> ListAllDefinitions()=> [];

    public void DisabledToolBySystem(string name)
    {
    }

    public void EnableToolBySystem(string name)
    {
    }

    public void DisableToolByUser(string name)
    {
    }

    public void EnableToolByUser(string name)
    {
    }
}

/// <summary>
/// Default in-memory <see cref="IToolRegistry"/> backed by a name-keyed dictionary. Tool invocations are dispatched by
/// name; unknown names return an error result.
/// </summary>
public sealed class ToolRegistry : IToolRegistry
{
    /// <summary>Gets a shared no-op <see cref="IToolRegistry"/> instance used when no tools are registered.</summary>
    public static readonly IToolRegistry Empty = EmptyToolRegistry.Instance;

    private HashSet<string> _disabledByUserToolNames = new();

    private HashSet<string> _disabledBySystemToolNames = new();

    private readonly Dictionary<string, (ITool Tool, ToolDefinition Definition)> _tools;

    /// <summary>Creates a registry pre-populated with <paramref name="tools"/>.</summary>
    /// <param name="tools">The tools to register.</param>
    public ToolRegistry(IEnumerable<ITool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);

        this._tools = [];
        foreach (ITool tool in tools)
        {
            this.Register(tool);
        }
    }

    /// <summary>Creates an empty registry with no tools registered.</summary>
    public ToolRegistry() : this([]) { }

    /// <inheritdoc/>
    public void Register(ITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        ValueTask<ToolDefinition> vt = tool.GetDefinitionAsync();
        if (!vt.IsCompleted)
        {
            throw new InvalidOperationException(
                $"Tool '{tool.Definition.Name}' requires async registration; use RegisterAsync.");
        }
        ToolDefinition def = vt.GetAwaiter().GetResult();
        this._tools[def.Name] = (tool, def);
    }

    /// <inheritdoc/>
    public async ValueTask RegisterAsync(ITool tool, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tool);

        ToolDefinition def = await tool.GetDefinitionAsync(ct).ConfigureAwait(false);
        this._tools[def.Name] = (tool, def);
    }

    /// <inheritdoc/>
    public IReadOnlyList<ToolDefinition> ListDefinitions()
    {
        return this._tools.Values.Where(e => IsToolDisabled(e.Definition.Name) == false).Select(e => e.Definition).ToList();
    }

    /// <inheritdoc/>
    public IReadOnlyList<(bool Enabled, ToolDefinition Definition)> ListAllDefinitions()
    {
        return this._tools.Values.Where(e => this._disabledBySystemToolNames.Contains(e.Definition.Name) == false)
            .Select(e => (this._disabledByUserToolNames.Contains(e.Definition.Name) == false, e.Definition)).ToList();
    }

    /// <inheritdoc/>
    public async Task<ToolResult> InvokeAsync(string name, JsonElement input, CancellationToken ct)
    {
        if (IsToolDisabled(name))
        {
            return new ToolResult("Tool is disabled.", IsError: true);
        }

        if (!this._tools.TryGetValue(name, out (ITool Tool, ToolDefinition Definition) entry))
        {
            return new ToolResult($"No tool registered with name '{name}'.", IsError: true);
        }

        return await entry.Tool.InvokeAsync(input, ct);
    }

    private bool IsToolDisabled(string name) =>
        this._disabledBySystemToolNames.Contains(name) || this._disabledByUserToolNames.Contains(name);

    /// <inheritdoc/>
    public void DisabledToolBySystem(string name) => this._disabledBySystemToolNames.Add(name);

    /// <inheritdoc/>
    public void EnableToolBySystem(string name) => this._disabledBySystemToolNames.Remove(name);

    /// <inheritdoc/>
    public void DisableToolByUser(string name) => this._disabledByUserToolNames.Add(name);

    /// <inheritdoc/>
    public void EnableToolByUser(string name) => this._disabledByUserToolNames.Remove(name);
}
