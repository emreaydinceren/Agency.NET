using System.Text.Json;

namespace Agency.Harness.Tools;

/// <summary>
/// Decorator over <see cref="IToolRegistry"/> that applies progressive disclosure by tool
/// <em>origin</em>: <em>MCP</em> tools (whose names are supplied at construction) have their schema
/// fully withheld behind the bare <c>{"type":"object"}</c> placeholder and their description reduced
/// to a one-line summary — the system prompt instructs the model to call <c>tool_help</c> to retrieve
/// the full schema before invoking them. <em>Native/internal</em> tools are advertised in full
/// (complete schema and description) so the model can call them without a <c>tool_help</c> round-trip.
/// The full detail of every tool is always recoverable via <c>tool_help</c>, which reads the
/// undecorated inner registry. As a safety net, when a call arrives missing required arguments,
/// <see cref="InvokeAsync"/> folds the full schema into the error so the retry self-heals. Tool
/// invocations are unchanged — real tools dispatch through the inner registry by their real names.
/// </summary>
public sealed class ProgressiveDiscoveryToolRegistry : IToolRegistry, IProgressiveDiscovery
{
    private readonly IToolRegistry _inner;
    private readonly ToolHelpTool _help;
    private readonly IReadOnlySet<string> _mcpToolNames;

    private static readonly JsonElement EmptyObjectSchema =
        JsonDocument.Parse(@"{ ""type"": ""object"" }").RootElement.Clone();

    /// <param name="inner">The registry being decorated.</param>
    /// <param name="mcpToolNames">
    /// Names of the MCP-originated tools. Only these are advertised progressively (schema withheld,
    /// description summarized); every other (native/internal) tool is revealed in full. Pass an empty
    /// set when no MCP tools are present.
    /// </param>
    public ProgressiveDiscoveryToolRegistry(IToolRegistry inner, IReadOnlySet<string> mcpToolNames)
    {
        this._inner = inner;
        this._help = new ToolHelpTool(inner);
        this._mcpToolNames = mcpToolNames;
    }

    /// <inheritdoc/>
    public IReadOnlyList<ToolDefinition> ListDefinitions()
    {
        IReadOnlyList<ToolDefinition> innerDefs = this._inner.ListDefinitions();
        var result = new List<ToolDefinition>(innerDefs.Count + 1);
        foreach (ToolDefinition def in innerDefs)
        {
            // Origin-based disclosure. MCP tools are the large, numerous, often verbose tools where
            // context savings matter, so their schema is fully withheld behind the {"type":"object"}
            // placeholder (the system prompt tells the model to tool_help before calling them) and
            // their description is reduced to a one-line summary. Native/internal tools are revealed
            // in full so the model can invoke them without a tool_help round-trip.
            if (this._mcpToolNames.Contains(def.Name))
            {
                result.Add(def with
                {
                    Description = AppendToolHelpDirective(Summarize(def.Description), def.Name),
                    InputSchema = EmptyObjectSchema,
                });
            }
            else
            {
                result.Add(def);
            }
        }
        result.Add(this._help.Definition);
        return result;
    }

    /// <summary>
    /// Reduces a verbose tool description to a one-line catalog summary: the first non-empty line.
    /// Any leading provenance prefix (e.g. <c>"Notion | "</c>) is preserved, because for MCP tools
    /// whose operationId-derived names (e.g. <c>API-get-self</c>) carry no server signal, that prefix
    /// is the model's only inline cue to which server a tool belongs — dropping it lets the model pick
    /// a wrong-server tool for an ambiguous request. The full description stays available via
    /// <c>tool_help</c>, which reads the undecorated inner registry.
    /// </summary>
    private static string Summarize(string description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return description;
        }

        string[] lines = description.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        return lines.Length > 0 ? lines[0].Trim() : description.Trim();
    }

    /// <summary>
    /// Appends an explicit, per-tool <c>tool_help</c> directive to a withheld MCP tool's summary. The
    /// single global instruction in the system prompt is easy for a weaker model to overlook, so the
    /// directive is repeated inline on every tool whose schema is withheld — naming the tool — so the
    /// model sees, right beside the tool it is about to call, that it must fetch the real schema first
    /// and must not invoke with empty or guessed arguments (the exact failure this guards against).
    /// </summary>
    private static string AppendToolHelpDirective(string summary, string name) =>
        $"{summary}\n\n[Schema withheld] Call tool_help(name: \"{name}\") to load this tool's parameters BEFORE invoking it. Do not call it with empty or guessed arguments.";

    /// <inheritdoc/>
    public async Task<ToolResult> InvokeAsync(string name, JsonElement input, CancellationToken ct)
    {
        if (name == "tool_help")
        {
            return await this._help.InvokeAsync(input, ct);
        }

        ToolResult result = await this._inner.InvokeAsync(name, input, ct);

        // Self-healing discovery: when a call fails *because* required arguments are missing — the
        // classic symptom of the model calling a withheld MCP tool without first reading its schema via
        // tool_help, or guessing the wrong keys — fold the tool's full schema into the error so the very
        // next retry has the shape, without needing a separate tool_help round-trip. Purely additive: a
        // successful call (or one the tool recovered via its own Postel's-law fallback) is returned
        // untouched, and a genuine runtime error with valid arguments (e.g. file-not-found) is left alone.
        if (result.IsError && this.MissingRequiredArguments(name, input))
        {
            ToolResult help = await this._help.InvokeAsync(
                JsonSerializer.SerializeToElement(new { name }), ct);
            if (!help.IsError)
            {
                return result with { Content = $"{result.Content}\n\n{help.Content}" };
            }
        }

        return result;
    }

    /// <summary>
    /// Returns true when <paramref name="input"/> omits at least one parameter the named tool's real
    /// schema marks as required. Distinguishes "the model sent the wrong shape" (heal with the schema)
    /// from a genuine runtime error on a well-formed call (leave untouched).
    /// </summary>
    private bool MissingRequiredArguments(string name, JsonElement input)
    {
        ToolDefinition? def = null;
        foreach (ToolDefinition d in this._inner.ListDefinitions())
        {
            if (d.Name == name)
            {
                def = d;
                break;
            }
        }

        if (def is null
            || def.InputSchema.ValueKind != JsonValueKind.Object
            || !def.InputSchema.TryGetProperty("required", out JsonElement required)
            || required.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (JsonElement r in required.EnumerateArray())
        {
            if (r.ValueKind == JsonValueKind.String && !HasNonEmpty(input, r.GetString()!))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when <paramref name="input"/> is an object carrying a non-empty value at <paramref name="key"/>.</summary>
    private static bool HasNonEmpty(JsonElement input, string key)
    {
        if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty(key, out JsonElement val))
        {
            return false;
        }

        return val.ValueKind switch
        {
            JsonValueKind.Null => false,
            JsonValueKind.String => !string.IsNullOrEmpty(val.GetString()),
            _ => true,
        };
    }

    /// <inheritdoc/>
    public void Register(ITool tool) => this._inner.Register(tool);

    /// <inheritdoc/>
    public ValueTask RegisterAsync(ITool tool, CancellationToken ct = default) =>
        this._inner.RegisterAsync(tool, ct);

    /// <inheritdoc/>
    public IReadOnlyList<(bool Enabled, ToolDefinition Definition)> ListAllDefinitions() =>
        this._inner.ListAllDefinitions();

    /// <inheritdoc/>
    public void DisabledToolBySystem(string name) => this._inner.DisabledToolBySystem(name);

    /// <inheritdoc/>
    public void EnableToolBySystem(string name) => this._inner.EnableToolBySystem(name);

    /// <inheritdoc/>
    public void DisableToolByUser(string name) => this._inner.DisableToolByUser(name);

    /// <inheritdoc/>
    public void EnableToolByUser(string name) => this._inner.EnableToolByUser(name);
}
