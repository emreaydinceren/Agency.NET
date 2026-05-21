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
        OnPreToolUse = CombinePreToolUse(first.OnPreToolUse, second.OnPreToolUse),
        OnPostToolUse = Combine(first.OnPostToolUse, second.OnPostToolUse),
        OnAssistantTurn = Combine(first.OnAssistantTurn, second.OnAssistantTurn),
        OnStop = Combine(first.OnStop, second.OnStop),
    };

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
}