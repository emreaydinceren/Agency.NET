using System.Text.Json;

using Agency.Harness.Loop;

namespace Agency.Harness.Test.Loop;

/// <summary>
/// Phase 2 / T-TOOL-*: unit tests for <see cref="GoalState"/>, <see cref="EnableGoalkeeperTool"/>,
/// and <see cref="DisableGoalkeeperTool"/>.
/// </summary>
public sealed class GoalStateAndToolsTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Returns a JSON input element from an anonymous object.</summary>
    private static JsonElement Json(object? value) =>
        JsonSerializer.SerializeToElement(value);

    // ── GoalState ─────────────────────────────────────────────────────────────

    /// <summary>A freshly created <see cref="GoalState"/> has no armed goal.</summary>
    [Fact]
    public void GoalState_StartsNull_NotArmed()
    {
        var state = new GoalState();

        Assert.Null(state.Active);
        Assert.False(state.IsArmed);
    }

    /// <summary>After <see cref="GoalState.Arm"/>, <c>Active</c> holds the spec and <c>IsArmed</c> is true.</summary>
    [Fact]
    public void GoalState_Arm_SetsActiveAndIsArmed()
    {
        var state = new GoalState();
        var spec = new GoalSpec { Condition = "build passes" };

        state.Arm(spec);

        Assert.Same(spec, state.Active);
        Assert.True(state.IsArmed);
    }

    /// <summary>After <see cref="GoalState.Clear"/>, <c>Active</c> is null and <c>IsArmed</c> is false.</summary>
    [Fact]
    public void GoalState_Clear_ResetsToNull()
    {
        var state = new GoalState();
        state.Arm(new GoalSpec { Condition = "tests pass" });

        state.Clear();

        Assert.Null(state.Active);
        Assert.False(state.IsArmed);
    }

    // ── T-TOOL-1: enable_goalkeeper arms the goal ─────────────────────────────

    /// <summary>
    /// T-TOOL-1: invoking <c>enable_goalkeeper</c> with a condition (and optional cap overrides)
    /// calls <see cref="GoalState.Arm"/> with a correctly-parsed <see cref="GoalSpec"/>.
    /// </summary>
    [Fact]
    public async Task EnableGoalkeeper_WithConditionAndOptionalCaps_ArmsGoalState()
    {
        var state = new GoalState();
        var tool = new EnableGoalkeeperTool(state);

        ToolResult result = await tool.InvokeAsync(
            Json(new { condition = "all tests green", maxTurns = 5, tokenBudget = 50_000L }),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.True(state.IsArmed);
        Assert.NotNull(state.Active);
        Assert.Equal("all tests green", state.Active!.Condition);
        Assert.Equal(5, state.Active.MaxTurns);
        Assert.Equal(50_000L, state.Active.TokenBudget);
    }

    // ── T-TOOL-2: idempotent replace — newest goal wins ───────────────────────

    /// <summary>
    /// T-TOOL-2 / E-10: a second call to <c>enable_goalkeeper</c> replaces the first goal;
    /// the newest goal wins (matches §6.4 idempotent-replace rule).
    /// </summary>
    [Fact]
    public async Task EnableGoalkeeper_CalledTwice_ReplacesFirstGoal()
    {
        var state = new GoalState();
        var tool = new EnableGoalkeeperTool(state);

        await tool.InvokeAsync(
            Json(new { condition = "first condition" }),
            CancellationToken.None);

        await tool.InvokeAsync(
            Json(new { condition = "second condition" }),
            CancellationToken.None);

        Assert.True(state.IsArmed);
        Assert.Equal("second condition", state.Active!.Condition);
    }

    // ── T-TOOL-3: validation — missing/empty condition is an error ────────────

    /// <summary>
    /// T-TOOL-3: when <c>condition</c> is missing from the input, the tool returns
    /// a <see cref="ToolResult"/> with <c>IsError = true</c> and leaves <see cref="GoalState"/> unchanged.
    /// </summary>
    [Fact]
    public async Task EnableGoalkeeper_MissingCondition_ReturnsError_LeavesStateUnchanged()
    {
        var state = new GoalState();
        var tool = new EnableGoalkeeperTool(state);

        ToolResult result = await tool.InvokeAsync(
            Json(new { }),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.False(state.IsArmed);
    }

    /// <summary>
    /// T-TOOL-3 (empty variant): when <c>condition</c> is an empty string, the tool returns
    /// a <see cref="ToolResult"/> with <c>IsError = true</c> and leaves <see cref="GoalState"/> unchanged.
    /// </summary>
    [Fact]
    public async Task EnableGoalkeeper_EmptyCondition_ReturnsError_LeavesStateUnchanged()
    {
        var state = new GoalState();
        var tool = new EnableGoalkeeperTool(state);

        ToolResult result = await tool.InvokeAsync(
            Json(new { condition = "" }),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.False(state.IsArmed);
    }

    // ── T-TOOL-4: disable_goalkeeper clears the goal ─────────────────────────

    /// <summary>
    /// T-TOOL-4: invoking <c>disable_goalkeeper</c> when a goal is armed calls
    /// <see cref="GoalState.Clear"/> and returns a non-error <see cref="ToolResult"/>.
    /// </summary>
    [Fact]
    public async Task DisableGoalkeeper_WhenArmed_ClearsGoalState_ReturnsSuccess()
    {
        var state = new GoalState();
        state.Arm(new GoalSpec { Condition = "some condition" });

        var tool = new DisableGoalkeeperTool(state);

        ToolResult result = await tool.InvokeAsync(
            Json(new { }),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.False(state.IsArmed);
    }

    /// <summary>
    /// T-TOOL-4 (no-op variant): invoking <c>disable_goalkeeper</c> when nothing is armed
    /// is a no-op that still returns a non-error <see cref="ToolResult"/>.
    /// </summary>
    [Fact]
    public async Task DisableGoalkeeper_WhenNotArmed_IsNoOp_ReturnsSuccess()
    {
        var state = new GoalState();
        var tool = new DisableGoalkeeperTool(state);

        ToolResult result = await tool.InvokeAsync(
            Json(new { }),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.False(state.IsArmed);
    }

    // ── Tool definition shape ─────────────────────────────────────────────────

    /// <summary>
    /// <c>enable_goalkeeper</c> exposes the correct name and a schema with <c>condition</c> required
    /// and <c>maxTurns</c> / <c>tokenBudget</c> optional.
    /// </summary>
    [Fact]
    public void EnableGoalkeeperTool_Definition_HasExpectedSchemaShape()
    {
        var state = new GoalState();
        var tool = new EnableGoalkeeperTool(state);

        Assert.Equal("enable_goalkeeper", tool.Definition.Name);

        JsonElement schema = tool.Definition.InputSchema;
        Assert.Equal("object", schema.GetProperty("type").GetString());

        // condition is required
        JsonElement required = schema.GetProperty("required");
        Assert.Contains("condition",
            Enumerable.Range(0, required.GetArrayLength()).Select(i => required[i].GetString()));

        // properties exist for all three fields
        JsonElement props = schema.GetProperty("properties");
        Assert.True(props.TryGetProperty("condition", out _));
        Assert.True(props.TryGetProperty("maxTurns", out _));
        Assert.True(props.TryGetProperty("tokenBudget", out _));
    }

    /// <summary><c>disable_goalkeeper</c> exposes the correct name.</summary>
    [Fact]
    public void DisableGoalkeeperTool_Definition_HasExpectedName()
    {
        var state = new GoalState();
        var tool = new DisableGoalkeeperTool(state);

        Assert.Equal("disable_goalkeeper", tool.Definition.Name);
    }
}
