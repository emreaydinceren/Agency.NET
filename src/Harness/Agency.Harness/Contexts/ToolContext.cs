
using Agency.Harness.Tools;

namespace Agency.Harness.Contexts;
/// <summary>The tool registry available to the agent during this session.</summary>
public sealed record ToolContext
{
    /// <summary>Gets a shared empty tool context with no registered tools.</summary>
    public static ToolContext Empty { get; } = new();

    /// <summary>Gets the tool registry to dispatch tool calls against.</summary>
    public IToolRegistry Registry { get; init; } = ToolRegistry.Empty;

}
