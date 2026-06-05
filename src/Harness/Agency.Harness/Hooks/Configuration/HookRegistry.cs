using Agency.Harness.Contexts;
using Agency.Harness.Hooks.Configuration.Handlers;
using Microsoft.Extensions.Logging;

namespace Agency.Harness.Hooks.Configuration;

internal sealed class HookRegistry
{
    private readonly Dictionary<HookEventName, List<(HookMatcher Matcher, IReadOnlyList<IHookHandler> Handlers)>> _byEvent;
    private readonly ILogger? _logger;

    internal static readonly HookRegistry Empty = new(new HooksOptions(), NullFactory.Instance, null);

    internal HookRegistry(HooksOptions options, IHookHandlerFactory factory, ILogger? logger)
    {
        this._logger = logger;
        this._byEvent = new Dictionary<HookEventName, List<(HookMatcher, IReadOnlyList<IHookHandler>)>>();

        foreach (var (eventName, groups) in options.Hooks)
        {
            var compiled = groups
                .Select(g => (
                    HookMatcher.Create(g.Matcher),
                    (IReadOnlyList<IHookHandler>)g.Hooks.Select(factory.Create).ToArray()))
                .ToList();

            this._byEvent[eventName] = compiled;
        }
    }

    internal AgentHooks ToAgentHooks()
    {
        return new AgentHooks
        {
            OnPreToolUse        = Has(HookEventName.PreToolUse)       ? this.BuildPreToolUse()                                  : null,
            OnPostToolUse       = Has(HookEventName.PostToolUse)      ? this.BuildFireAwaitTool(HookEventName.PostToolUse)      : null,
            OnSessionStarted    = Has(HookEventName.SessionStart)     ? this.BuildFireAwait(HookEventName.SessionStart)         : null,
            OnUserPromptSubmit  = Has(HookEventName.UserPromptSubmit) ? this.BuildFireAwaitPrompt(HookEventName.UserPromptSubmit) : null,
            OnPreIteration      = Has(HookEventName.PreIteration)     ? this.BuildFireAwaitContext(HookEventName.PreIteration)  : null,
            OnPostToolBatch     = Has(HookEventName.PostToolBatch)    ? this.BuildFireAwaitBatch(HookEventName.PostToolBatch)   : null,
            OnAssistantTurn     = Has(HookEventName.AssistantTurn)    ? this.BuildFireAwaitAssistant(HookEventName.AssistantTurn) : null,
            OnStop              = Has(HookEventName.Stop)             ? this.BuildFireAwaitStop(HookEventName.Stop)             : null,
            OnSessionEnd        = Has(HookEventName.SessionEnd)       ? this.BuildFireAwaitSessionEnd(HookEventName.SessionEnd) : null,
        };
    }

    private bool Has(HookEventName eventName) => this._byEvent.ContainsKey(eventName);

    private List<IHookHandler> MatchingHandlers(HookEventName eventName, string subject)
    {
        return this._byEvent[eventName]
            .Where(g => g.Matcher.IsMatch(subject))
            .SelectMany(g => g.Handlers)
            .ToList();
    }

    // ── PreToolUse ───────────────────────────────────────────────────────────

    private Func<PreToolUseHookContext, CancellationToken, Task<PreToolUseDecision>> BuildPreToolUse()
    {
        return async (ctx, ct) =>
        {
            var handlers = this.MatchingHandlers(HookEventName.PreToolUse, ctx.ToolName);
            if (handlers.Count == 0)
            {
                return PreToolUseDecision.Allowed;
            }

            var payload = HookPayloadFactory.ForPreToolUse(ctx);
            var outputs = await Task.WhenAll(handlers.Select(h => h.InvokeAsync(payload, ct)));
            return AggregateDecision(outputs, ctx.Input);
        };
    }

    // ── PostToolUse ──────────────────────────────────────────────────────────

    private Func<PostToolUseHookContext, CancellationToken, Task> BuildFireAwaitTool(HookEventName eventName)
    {
        return async (ctx, ct) =>
        {
            var handlers = this.MatchingHandlers(eventName, ctx.ToolName);
            if (handlers.Count == 0)
            {
                return;
            }

            var payload = HookPayloadFactory.ForPostToolUse(ctx);
            await Task.WhenAll(handlers.Select(h => h.InvokeAsync(payload, ct)));
        };
    }

    // ── SessionStarted ───────────────────────────────────────────────────────

    private Func<SessionStartedHookContext, CancellationToken, Task> BuildFireAwait(HookEventName eventName)
    {
        return async (ctx, ct) =>
        {
            var handlers = this.MatchingHandlers(eventName, "*");
            if (handlers.Count == 0)
            {
                return;
            }

            var payload = HookPayloadFactory.ForSessionStarted(ctx);
            await Task.WhenAll(handlers.Select(h => h.InvokeAsync(payload, ct)));
        };
    }

    // ── UserPromptSubmit ─────────────────────────────────────────────────────

    private Func<Context, CancellationToken, Task> BuildFireAwaitPrompt(HookEventName eventName)
    {
        return async (ctx, ct) =>
        {
            var handlers = this.MatchingHandlers(eventName, "*");
            if (handlers.Count == 0)
            {
                return;
            }

            var hookCtx = new UserPromptSubmitHookContext(ctx.Query.Prompt, ctx);
            var payload = HookPayloadFactory.ForUserPromptSubmit(hookCtx);
            await Task.WhenAll(handlers.Select(h => h.InvokeAsync(payload, ct)));
        };
    }

    // ── PreIteration ─────────────────────────────────────────────────────────

    private Func<Context, CancellationToken, Task> BuildFireAwaitContext(HookEventName eventName)
    {
        return async (ctx, ct) =>
        {
            var handlers = this.MatchingHandlers(eventName, "*");
            if (handlers.Count == 0)
            {
                return;
            }

            var hookCtx = new PreIterationHookContext(ctx);
            var payload = HookPayloadFactory.ForPreIteration(hookCtx);
            await Task.WhenAll(handlers.Select(h => h.InvokeAsync(payload, ct)));
        };
    }

    // ── PostToolBatch ────────────────────────────────────────────────────────

    private Func<IReadOnlyList<ToolInvokedEvent>, Context, CancellationToken, Task> BuildFireAwaitBatch(HookEventName eventName)
    {
        return async (events, ctx, ct) =>
        {
            var handlers = this.MatchingHandlers(eventName, "*");
            if (handlers.Count == 0)
            {
                return;
            }

            var hookCtx = new PostToolBatchHookContext(events, ctx);
            var payload = HookPayloadFactory.ForPostToolBatch(hookCtx);
            await Task.WhenAll(handlers.Select(h => h.InvokeAsync(payload, ct)));
        };
    }

    // ── AssistantTurn ────────────────────────────────────────────────────────

    private Func<AssistantTurnHookContext, CancellationToken, Task> BuildFireAwaitAssistant(HookEventName eventName)
    {
        return async (ctx, ct) =>
        {
            var handlers = this.MatchingHandlers(eventName, "*");
            if (handlers.Count == 0)
            {
                return;
            }

            var payload = HookPayloadFactory.ForAssistantTurn(ctx);
            await Task.WhenAll(handlers.Select(h => h.InvokeAsync(payload, ct)));
        };
    }

    // ── Stop ─────────────────────────────────────────────────────────────────

    private Func<StopHookContext, CancellationToken, Task> BuildFireAwaitStop(HookEventName eventName)
    {
        return async (ctx, ct) =>
        {
            var handlers = this.MatchingHandlers(eventName, "*");
            if (handlers.Count == 0)
            {
                return;
            }

            var payload = HookPayloadFactory.ForStop(ctx);
            await Task.WhenAll(handlers.Select(h => h.InvokeAsync(payload, ct)));
        };
    }

    // ── SessionEnd ───────────────────────────────────────────────────────────

    private Func<SessionEndedHookContext, CancellationToken, Task> BuildFireAwaitSessionEnd(HookEventName eventName)
    {
        return async (ctx, ct) =>
        {
            var handlers = this.MatchingHandlers(eventName, "*");
            if (handlers.Count == 0)
            {
                return;
            }

            var payload = HookPayloadFactory.ForSessionEnded(ctx);
            await Task.WhenAll(handlers.Select(h => h.InvokeAsync(payload, ct)));
        };
    }

    // ── Decision aggregation ─────────────────────────────────────────────────

    internal static PreToolUseDecision AggregateDecision(
        IEnumerable<HookHandlerOutput> outputs,
        System.Text.Json.JsonElement _)
    {
        var decisions = outputs.Select(MapToDecision).ToList();

        var firstDeny = decisions.OfType<PreToolUseDecision.Deny>().FirstOrDefault();
        if (firstDeny is not null)
        {
            return firstDeny;
        }

        var firstRewrite = decisions.OfType<PreToolUseDecision.Rewrite>().FirstOrDefault();
        if (firstRewrite is not null)
        {
            return firstRewrite;
        }

        return PreToolUseDecision.Allowed;
    }

    internal static PreToolUseDecision MapToDecision(HookHandlerOutput output)
    {
        if (output.ExitCode == HookExitCodes.BlockingDeny)
        {
            string reason = output.RawStdout
                ?? output.RawStderr
                ?? TryGetPermissionDecisionReason(output.Json)
                ?? string.Empty;
            return new PreToolUseDecision.Deny(reason);
        }

        if (output.Json.HasValue && TryGetPermissionDecision(output.Json.Value) == "deny")
        {
            string reason = TryGetPermissionDecisionReason(output.Json) ?? string.Empty;
            return new PreToolUseDecision.Deny(reason);
        }

        if (output.Json.HasValue && output.Json.Value.TryGetProperty("tool_input", out var toolInput)
            && toolInput.ValueKind != System.Text.Json.JsonValueKind.Null)
        {
            return new PreToolUseDecision.Rewrite(toolInput);
        }

        if (output.ExitCode == HookExitCodes.Ok)
        {
            return PreToolUseDecision.Allowed;
        }

        // Non-zero, non-2 exit code: non-blocking error — fail open
        return PreToolUseDecision.Allowed;
    }

    private static string? TryGetPermissionDecision(System.Text.Json.JsonElement json)
    {
        if (json.TryGetProperty("hookSpecificOutput", out var hso)
            && hso.TryGetProperty("permissionDecision", out var pd))
        {
            return pd.GetString();
        }

        return null;
    }

    private static string? TryGetPermissionDecisionReason(System.Text.Json.JsonElement? json)
    {
        if (json.HasValue
            && json.Value.TryGetProperty("hookSpecificOutput", out var hso)
            && hso.TryGetProperty("permissionDecisionReason", out var pdr))
        {
            return pdr.GetString();
        }

        return null;
    }

    // ── NullFactory ──────────────────────────────────────────────────────────

    private sealed class NullFactory : IHookHandlerFactory
    {
        internal static readonly NullFactory Instance = new();
        private NullFactory() { }

        public IHookHandler Create(HookHandlerConfig config) =>
            throw new InvalidOperationException("HookRegistry.Empty should never have handlers created.");
    }
}
