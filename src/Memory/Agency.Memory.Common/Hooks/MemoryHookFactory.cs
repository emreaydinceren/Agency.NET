using Agency.Harness.Contexts;
using Agency.Harness.Hooks;

namespace Agency.Memory.Common.Hooks;

/// <summary>
/// Produces the baseline <see cref="AgentHooks"/> that wire the memory retrieval
/// engine and inactivity timer into the agent loop.
/// </summary>
/// <remarks>
/// By default the baseline hooks run FIRST; user hooks are composed after via
/// <see cref="AgentHooksExtensions.Compose"/>. Callers who need pre-retrieval context
/// may use <see cref="AgentHooksExtensions.ComposeBefore"/> as an explicit escape hatch.
/// <para>
/// <b>Reference direction note (IQ-1):</b> <c>Agency.Memory.Common</c> cannot reference
/// <c>Agency.Memory.Retrieval</c> because <c>Retrieval</c> already references <c>Common</c>,
/// which would create a circular dependency. Therefore <see cref="Build"/> accepts pre-built
/// callbacks for retrieval and timer restart. The DI extension (<c>AddAgencyMemory</c>) in
/// <c>Agency.Memory.Distiller</c> supplies these callbacks from <c>RetrievalEngine</c> and
/// <c>InactivityTimerService</c>, both of which are visible to the Distiller project.
/// </para>
/// </remarks>
public static class MemoryHookFactory
{
    /// <summary>
    /// Builds a baseline <see cref="AgentHooks"/> that wires the provided retrieval callback
    /// to <see cref="AgentHooks.OnPreIteration"/> and the timer-restart callback to
    /// <see cref="AgentHooks.OnAssistantTurn"/>.
    /// </summary>
    /// <param name="retrievalCallback">
    /// The gated retrieval function to run before each iteration. Should internally check the
    /// retrieval gate (Spec §8.1) and, if the gate passes, perform vector search and update
    /// <see cref="Context.Knowledge"/>, <see cref="Context.Memory"/>, and
    /// <see cref="Context.MemoryLastRetrievedAt"/>.
    /// </param>
    /// <param name="timerRestartCallback">
    /// Called after each assistant turn to restart the per-session inactivity timer.
    /// Per Spec §14.9, this is the ONLY side effect of <see cref="AgentHooks.OnAssistantTurn"/>.
    /// </param>
    /// <param name="sessionStartedCallback">
    /// Optional callback fired once when a session starts. Used to register the agent's live
    /// conversation manager with the conversation manager registry so the distiller can read
    /// session turns. When <see langword="null"/>, <see cref="AgentHooks.OnSessionStarted"/> is
    /// left null.
    /// </param>
    /// <returns>
    /// A baseline <see cref="AgentHooks"/> instance with <see cref="AgentHooks.OnPreIteration"/>
    /// and <see cref="AgentHooks.OnAssistantTurn"/> wired. All other hooks are null.
    /// </returns>
    public static AgentHooks Build(
        Func<Context, CancellationToken, Task> retrievalCallback,
        Func<Agency.Harness.Hooks.AssistantTurnHookContext, CancellationToken, Task> timerRestartCallback,
        Func<Agency.Harness.Hooks.SessionStartedHookContext, CancellationToken, Task>? sessionStartedCallback = null) =>
        new()
        {
            OnSessionStarted = sessionStartedCallback,
            OnPreIteration = retrievalCallback,
            OnAssistantTurn = timerRestartCallback,
        };

    /// <summary>
    /// Builds an empty baseline <see cref="AgentHooks"/> with stub delegates wired for
    /// <see cref="AgentHooks.OnPreIteration"/> and <see cref="AgentHooks.OnAssistantTurn"/>.
    /// Full production wiring requires the real services (store, embedder, timer) from
    /// <c>AddAgencyMemory</c>; this method exists for unit testing the composition shape.
    /// </summary>
    /// <returns>A baseline <see cref="AgentHooks"/> instance.</returns>
    public static AgentHooks BuildEmpty() =>
        new()
        {
            OnPreIteration = (_, _) => Task.CompletedTask,
            OnAssistantTurn = (_, _) => Task.CompletedTask,
        };
}
