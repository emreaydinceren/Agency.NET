namespace Agency.Agentic.Hooks;

/// <summary>Lifecycle hook delegates for an <see cref="Agency.Agentic.Agent"/> run.</summary>
public sealed record AgentHooks
{
    /// <summary>Fires once before the first iteration of the agent loop.</summary>
    public Func<SessionStartedHookContext, CancellationToken, Task>? OnSessionStarted { get; init; }

    /// <summary>
    /// Fires before each tool invocation. Return a <see cref="PreToolUseDecision"/>
    /// to allow, block, or rewrite the call.
    /// </summary>
    public Func<PreToolUseHookContext, CancellationToken, Task<PreToolUseDecision>>? OnPreToolUse { get; init; }

    /// <summary>Fires after each tool invocation, whether it succeeded or errored.</summary>
    public Func<PostToolUseHookContext, CancellationToken, Task>? OnPostToolUse { get; init; }

    /// <summary>Fires after the LLM emits a response and it has been appended to the conversation.</summary>
    public Func<AssistantTurnHookContext, CancellationToken, Task>? OnAssistantTurn { get; init; }

    /// <summary>Fires just before the agent emits <see cref="Agency.Agentic.AgentResultEvent"/> and stops.</summary>
    public Func<StopHookContext, CancellationToken, Task>? OnStop { get; init; }

    /// <summary>Empty hooks — all delegates are null.</summary>
    public static AgentHooks None { get; } = new();
}