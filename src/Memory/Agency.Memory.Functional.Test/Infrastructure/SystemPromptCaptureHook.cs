using Agency.Agentic;
using Agency.Agentic.Contexts;
using Agency.Agentic.Hooks;
using System.Collections.Concurrent;

namespace Agency.Memory.Functional.Test.Infrastructure;

/// <summary>
/// A test-only <see cref="AgentHooks.OnPreIteration"/> hook that snapshots
/// <see cref="Context.Knowledge"/> and <see cref="Context.Memory"/> into a
/// thread-safe collection keyed by <c>(sessionId, iterationIndex)</c>.
/// </summary>
/// <remarks>
/// <para>
/// Compose this hook <em>after</em> the baseline memory hooks so that the snapshot
/// reflects the enriched context that the memory retrieval engine wrote during the
/// same <c>OnPreIteration</c> pass:
/// <code>
/// AgentHooks combined = fixture.BaselineHooks.Compose(captureHook.AsHooks(sessionId));
/// </code>
/// </para>
/// <para>
/// E2E tests assert "the system prompt contains X" by inspecting
/// <see cref="GetSnapshot"/> rather than parsing the raw system-prompt string.
/// This decouples the assertions from the exact prompt formatting.
/// </para>
/// </remarks>
internal sealed class SystemPromptCaptureHook
{
    private readonly ConcurrentDictionary<(string SessionId, int IterationIndex), ContextSnapshot>
        _snapshots = new();

    // ── Snapshot access ───────────────────────────────────────────────────────

    /// <summary>
    /// Returns the captured <see cref="ContextSnapshot"/> for a specific
    /// <paramref name="sessionId"/> and <paramref name="iterationIndex"/>,
    /// or <see langword="null"/> if no snapshot has been captured for that key.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="iterationIndex">
    /// The zero-based agent-loop iteration index (<see cref="Context.IterationCount"/>
    /// at the time the hook fired).
    /// </param>
    /// <returns>The captured snapshot, or <see langword="null"/> if not present.</returns>
    internal ContextSnapshot? GetSnapshot(string sessionId, int iterationIndex) =>
        this._snapshots.TryGetValue((sessionId, iterationIndex), out ContextSnapshot? snap)
            ? snap
            : null;

    /// <summary>
    /// Returns all captured snapshots for the given <paramref name="sessionId"/>,
    /// ordered by ascending <c>iterationIndex</c>.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <returns>All snapshots for the session, ordered by iteration index.</returns>
    internal IReadOnlyList<ContextSnapshot> GetAllSnapshots(string sessionId) =>
        this._snapshots
            .Where(kv => kv.Key.SessionId == sessionId)
            .OrderBy(kv => kv.Key.IterationIndex)
            .Select(kv => kv.Value)
            .ToList();

    // ── Hook wiring ───────────────────────────────────────────────────────────

    /// <summary>
    /// Returns an <see cref="AgentHooks"/> instance with <see cref="AgentHooks.OnPreIteration"/>
    /// wired to snapshot the context for the given <paramref name="sessionId"/>.
    /// </summary>
    /// <remarks>
    /// Compose with the baseline hooks using
    /// <see cref="AgentHooksExtensions.Compose"/> so the snapshot occurs <em>after</em>
    /// the retrieval engine has populated <see cref="Context.Knowledge"/> and
    /// <see cref="Context.Memory"/>.
    /// </remarks>
    /// <param name="sessionId">The session identifier used as the snapshot key.</param>
    /// <returns>
    /// An <see cref="AgentHooks"/> instance with only <see cref="AgentHooks.OnPreIteration"/>
    /// set; all other hook delegates are <see langword="null"/>.
    /// </returns>
    internal AgentHooks AsHooks(string sessionId) =>
        new()
        {
            OnPreIteration = (ctx, ct) =>
            {
                int iteration = ctx.IterationCount;
                var key = (sessionId, iteration);

                var snapshot = new ContextSnapshot(
                    SessionId: sessionId,
                    IterationIndex: iteration,
                    Knowledge: ctx.Knowledge,
                    Memory: ctx.Memory);

                this._snapshots[key] = snapshot;
                return Task.CompletedTask;
            },
        };
}

/// <summary>
/// An immutable snapshot of <see cref="Context.Knowledge"/> and
/// <see cref="Context.Memory"/> captured by <see cref="SystemPromptCaptureHook"/>
/// at a specific agent-loop iteration.
/// </summary>
/// <param name="SessionId">The session identifier.</param>
/// <param name="IterationIndex">
/// The zero-based agent-loop iteration index at which the snapshot was captured.
/// </param>
/// <param name="Knowledge">The captured knowledge context.</param>
/// <param name="Memory">The captured memory context.</param>
internal sealed record ContextSnapshot(
    string SessionId,
    int IterationIndex,
    KnowledgeContext Knowledge,
    MemoryContext Memory);
