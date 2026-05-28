using System.Text.Json;
using Agency.Llm.Common.Tools;

namespace Agency.Memory.Consolidator.Tools;

/// <summary>
/// Consolidator tool that signals the sub-agent has finished the consolidation pass
/// (Spec §6.3 — <c>Memory_Done</c>).
/// </summary>
/// <remarks>
/// Invoking this tool sets a flag that the <c>ConsolidatorBackgroundService</c> watches.
/// The agent loop terminates as soon as this tool is called.
/// </remarks>
internal sealed class MemoryDoneTool : ITool
{
    private static readonly JsonElement _inputSchema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {}
        }
        """).RootElement;

    private readonly Action _onDone;

    /// <summary>
    /// Initialises a new <see cref="MemoryDoneTool"/>.
    /// </summary>
    /// <param name="onDone">
    /// Callback invoked when the tool is called; used by the consolidation loop
    /// to detect termination and cancel the sub-agent.
    /// </param>
    internal MemoryDoneTool(Action onDone)
    {
        this._onDone = onDone ?? throw new ArgumentNullException(nameof(onDone));
    }

    /// <inheritdoc/>
    public ToolDefinition Definition => new(
        Name: "Memory_Done",
        Description: "Signal that you are finished consolidating. ALWAYS call this last. " +
                     "The consolidation pass ends as soon as you call this tool.",
        InputSchema: _inputSchema);

    /// <inheritdoc/>
    public Task<ToolResult> InvokeAsync(JsonElement input, CancellationToken ct)
    {
        this._onDone();
        return Task.FromResult(new ToolResult("Consolidation pass complete."));
    }
}
