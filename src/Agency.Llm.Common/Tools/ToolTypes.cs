using System.Text.Json;

namespace Agency.Llm.Common.Tools;

/// <summary>The JSON-schema description of a tool exposed to the LLM.</summary>
public sealed record ToolDefinition(
    string Name,
    string Description,
    JsonElement InputSchema);

/// <summary>The result returned by a tool invocation.</summary>
public sealed record ToolResult(string Content, bool IsError = false);

/// <summary>A callable tool that can be registered with an <see cref="IToolRegistry"/>.</summary>
public interface ITool
{
    /// <summary>Gets the tool's definition, including its JSON schema.</summary>
    ToolDefinition Definition { get; }

    /// <summary>Invokes the tool with the given JSON input.</summary>
    Task<ToolResult> InvokeAsync(JsonElement input, CancellationToken ct);
}

/// <summary>Provides a catalogue of available tools and dispatches invocations by name.</summary>
public interface IToolRegistry
{
    /// <summary>Returns the definitions of all registered tools.</summary>
    IReadOnlyList<ToolDefinition> ListDefinitions();

    /// <summary>Invokes the named tool with the given JSON input.</summary>
    Task<ToolResult> InvokeAsync(string name, JsonElement input, CancellationToken ct);
}
