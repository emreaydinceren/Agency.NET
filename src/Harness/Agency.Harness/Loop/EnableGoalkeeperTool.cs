using System.Text.Json;

namespace Agency.Harness.Loop;

/// <summary>
/// Tool <c>enable_goalkeeper</c>: arms the <see cref="GoalState"/> with a new <see cref="GoalSpec"/>.
/// Idempotent — a second call replaces the prior goal (newest goal wins, §6.4 / E-10).
/// A missing or empty <c>condition</c> returns an error result and leaves <see cref="GoalState"/> unchanged.
/// </summary>
internal sealed class EnableGoalkeeperTool : ITool
{
    private static readonly ToolDefinition ToolDef = new(
        Name: "enable_goalkeeper",
        Description: "Arms the loop goalkeeper with a verifiable done-condition. " +
                     "The goalkeeper evaluates the condition after every turn and stops the loop when satisfied. " +
                     "A second call replaces the current goal (newest goal wins).",
        InputSchema: JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "condition": {
                        "type": "string",
                        "description": "The verifiable end state expressed in plain language (required)."
                    },
                    "maxTurns": {
                        "type": "integer",
                        "description": "Hard cap on continuation turns (optional, default 12)."
                    },
                    "tokenBudget": {
                        "type": "integer",
                        "description": "Total-token ceiling across the full loop (optional, null = off)."
                    }
                },
                "required": ["condition"]
            }
            """).RootElement);

    private readonly GoalState _goalState;

    /// <summary>
    /// Initializes a new instance of <see cref="EnableGoalkeeperTool"/> with the session-scoped
    /// <paramref name="goalState"/> it will mutate on invocation.
    /// </summary>
    /// <param name="goalState">The session-scoped goal holder shared with the <c>LoopRunner</c>.</param>
    public EnableGoalkeeperTool(GoalState goalState)
    {
        this._goalState = goalState;
    }

    /// <inheritdoc/>
    public ToolDefinition Definition => ToolDef;

    /// <inheritdoc/>
    public Task<ToolResult> InvokeAsync(JsonElement input, CancellationToken ct)
    {
        // Parse condition (required string)
        string? condition = null;
        if (input.TryGetProperty("condition", out JsonElement conditionEl) &&
            conditionEl.ValueKind == JsonValueKind.String)
        {
            condition = conditionEl.GetString();
        }

        if (string.IsNullOrWhiteSpace(condition))
        {
            return Task.FromResult(new ToolResult(
                "Parameter 'condition' is required and must be a non-empty string.",
                IsError: true));
        }

        // Parse optional maxTurns (int)
        int maxTurns = 12;
        if (input.TryGetProperty("maxTurns", out JsonElement maxTurnsEl) &&
            maxTurnsEl.ValueKind == JsonValueKind.Number &&
            maxTurnsEl.TryGetInt32(out int parsedMaxTurns))
        {
            maxTurns = parsedMaxTurns;
        }

        // Parse optional tokenBudget (long)
        long? tokenBudget = null;
        if (input.TryGetProperty("tokenBudget", out JsonElement tokenBudgetEl) &&
            tokenBudgetEl.ValueKind == JsonValueKind.Number &&
            tokenBudgetEl.TryGetInt64(out long parsedTokenBudget))
        {
            tokenBudget = parsedTokenBudget;
        }

        var spec = new GoalSpec
        {
            Condition = condition,
            MaxTurns = maxTurns,
            TokenBudget = tokenBudget,
        };

        this._goalState.Arm(spec);

        return Task.FromResult(new ToolResult($"Goalkeeper armed. Condition: {condition}"));
    }
}
