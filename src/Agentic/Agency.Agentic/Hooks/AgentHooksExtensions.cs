namespace Agency.Agentic.Hooks;

/// <summary>Extension methods for composing <see cref="AgentHooks"/> instances.</summary>
public static class AgentHooksExtensions
{
    /// <summary>
    /// Returns a new <see cref="AgentHooks"/> where <paramref name="second"/> runs after
    /// <paramref name="first"/>. For <c>OnPreToolUse</c>, the most restrictive decision wins
    /// (Deny &gt; Rewrite &gt; Allow). All other delegates run sequentially.
    /// </summary>
    public static AgentHooks Compose(this AgentHooks first, AgentHooks second) => new()
    {
        OnSessionStarted = Combine(first.OnSessionStarted, second.OnSessionStarted),
        OnUserPromptSubmit = Combine(first.OnUserPromptSubmit, second.OnUserPromptSubmit),
        OnPreIteration = Combine(first.OnPreIteration, second.OnPreIteration),
        OnPreToolUse = CombinePreToolUse(first.OnPreToolUse, second.OnPreToolUse),
        OnPostToolUse = Combine(first.OnPostToolUse, second.OnPostToolUse),
        OnPostToolBatch = CombinePostToolBatch(first.OnPostToolBatch, second.OnPostToolBatch),
        OnAssistantTurn = Combine(first.OnAssistantTurn, second.OnAssistantTurn),
        OnStop = Combine(first.OnStop, second.OnStop),
    };

    /// <summary>
    /// Returns a new <see cref="AgentHooks"/> where <paramref name="first"/> runs before
    /// <paramref name="self"/> (the baseline). This is the escape hatch for callers who need
    /// to see the un-enriched <see cref="Agency.Agentic.Contexts.Context"/> before the baseline hooks
    /// (e.g., the retrieval engine) have run.
    /// </summary>
    /// <param name="self">The baseline hooks (run second).</param>
    /// <param name="first">The user hooks to run first.</param>
    /// <returns>Composed hooks with <paramref name="first"/> executing before <paramref name="self"/>.</returns>
    public static AgentHooks ComposeBefore(this AgentHooks self, AgentHooks first) => first.Compose(self);

    private static Func<T, CancellationToken, Task>? Combine<T>(
        Func<T, CancellationToken, Task>? a,
        Func<T, CancellationToken, Task>? b) =>
        (a, b) switch
        {
            (null, null) => null,
            (not null, null) => a,
            (null, not null) => b,
            _ => async (ctx, ct) => { await a(ctx, ct); await b(ctx, ct); },
        };

    private static Func<PreToolUseHookContext, CancellationToken, Task<PreToolUseDecision>>?
        CombinePreToolUse(
            Func<PreToolUseHookContext, CancellationToken, Task<PreToolUseDecision>>? a,
            Func<PreToolUseHookContext, CancellationToken, Task<PreToolUseDecision>>? b) =>
        (a, b) switch
        {
            (null, null) => null,
            (not null, null) => a,
            (null, not null) => b,
            _ => async (ctx, ct) =>
            {
                PreToolUseDecision[] results = await Task.WhenAll(a(ctx, ct), b(ctx, ct));
                PreToolUseDecision da = results[0], db = results[1];
                if (da is PreToolUseDecision.Deny) { return da; }
                if (db is PreToolUseDecision.Deny) { return db; }
                if (da is PreToolUseDecision.Rewrite) { return da; }
                return db;
            },
        };

    private static Func<IReadOnlyList<ToolInvokedEvent>, Agency.Agentic.Contexts.Context, CancellationToken, Task>?
        CombinePostToolBatch(
            Func<IReadOnlyList<ToolInvokedEvent>, Agency.Agentic.Contexts.Context, CancellationToken, Task>? a,
            Func<IReadOnlyList<ToolInvokedEvent>, Agency.Agentic.Contexts.Context, CancellationToken, Task>? b) =>
        (a, b) switch
        {
            (null, null) => null,
            (not null, null) => a,
            (null, not null) => b,
            _ => async (events, ctx, ct) => { await a(events, ctx, ct); await b(events, ctx, ct); },
        };
}