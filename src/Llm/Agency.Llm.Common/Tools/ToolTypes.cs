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
    /// <summary>
    /// Registers <paramref name="tool"/> in the registry, overwriting any existing tool with the
    /// same <see cref="ToolDefinition.Name"/>. Used by lifecycle hooks to add per-session tools
    /// after the registry is created.
    /// </summary>
    /// <param name="tool">The tool to register.</param>
    void Register(ITool tool);

    /// <summary>Returns the definitions of all registered tools.</summary>
    IReadOnlyList<ToolDefinition> ListDefinitions();

    /// <summary>
    /// Retrieves a read-only list of all available tool definitions along with their enabled status.
    /// </summary>
    /// <remarks>The returned list includes both enabled and disabled tool definitions. The Boolean value in
    /// each tuple is <see langword="true"/> if the tool is enabled; otherwise, <see langword="false"/>.</remarks>
    /// <returns>A read-only list of tuples, each containing a Boolean value indicating whether the tool is enabled and the
    /// corresponding tool definition.</returns>
    IReadOnlyList<(bool Enabled, ToolDefinition Definition)> ListAllDefinitions();

    /// <summary>
    /// Disables the tool with the given name, preventing it from being invoked until re-enabled. Tools can be disabled
    /// by
    /// </summary>
    /// <param name="name"></param>
    void DisabledToolBySystem(string name);

    /// <summary>
    /// Enables the tool with the given name if it was previously disabled. Tools can be enabled or disabled by both the
    /// </summary>
    /// <param name="name"></param>
    void EnableToolBySystem(string name);

    /// <summary>
    /// Disables the specified tool for the current user.
    /// </summary>
    /// <param name="name">The name of the tool to disable. Cannot be null or empty.</param>
    void DisableToolByUser(string name);

    /// <summary>
    /// Enables the specified tool for the current user if it was previously disabled.
    /// </summary>
    /// <param name="name"></param>
    void EnableToolByUser(string name);

    /// <summary>Invokes the named tool with the given JSON input.</summary>
    Task<ToolResult> InvokeAsync(string name, JsonElement input, CancellationToken ct);
}
