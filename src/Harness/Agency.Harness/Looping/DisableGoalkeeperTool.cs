using System.Text.Json;

namespace Agency.Harness.Looping;

/// <summary>
/// Tool <c>disable_goalkeeper</c>: disarms the <see cref="GoalState"/>, stopping the loop after
/// the current turn completes. Safe to call when no goal is armed (no-op). Mirrors <c>/goal clear</c>.
/// </summary>
internal sealed class DisableGoalkeeperTool : ITool
{
    private static readonly ToolDefinition ToolDef = new(
        Name: "disable_goalkeeper",
        Description: "Disarms the loop goalkeeper, stopping goal-driven looping after the current turn. " +
                     "Safe to call when no goal is currently armed (no-op).",
        InputSchema: JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {}
            }
            """).RootElement);

    private readonly GoalState _goalState;

    /// <summary>
    /// Initializes a new instance of <see cref="DisableGoalkeeperTool"/> with the session-scoped
    /// <paramref name="goalState"/> it will mutate on invocation.
    /// </summary>
    /// <param name="goalState">The session-scoped goal holder shared with the <c>LoopRunner</c>.</param>
    public DisableGoalkeeperTool(GoalState goalState)
    {
        this._goalState = goalState;
    }

    /// <inheritdoc/>
    public ToolDefinition Definition => ToolDef;

    /// <inheritdoc/>
    public Task<ToolResult> InvokeAsync(JsonElement input, CancellationToken ct)
    {
        bool wasArmed = this._goalState.IsArmed;
        this._goalState.Clear();

        string message = wasArmed
            ? "Goalkeeper disarmed. The loop will stop after the current turn."
            : "Goalkeeper was not armed; nothing to disarm.";

        return Task.FromResult(new ToolResult(message));
    }
}
