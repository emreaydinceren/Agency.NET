using System.Collections.Concurrent;
using Agency.Acp.Errors;
using dotacp.protocol;

namespace Agency.Acp.Sessions;

/// <summary>
/// Owns every live <see cref="SessionState"/> for the process (spec §6.3, §7.1). Concurrent
/// <c>session/new</c> calls are safe — the backing store is a <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// </summary>
/// <remarks>
/// Disposal of a session's object graph is reachable from three independent triggers:
/// <c>session/close</c> (<see cref="CloseAsync"/>), transport disconnect, and process shutdown
/// (both drained via <see cref="DisposeAllAsync"/>). Spec E-15 records that the first real client
/// never sends <c>session/close</c>, so <see cref="DisposeAllAsync"/> — not <see cref="CloseAsync"/> —
/// is the mechanism that must run unconditionally; <see cref="CloseAsync"/> is only an optimisation
/// that releases resources sooner. <see cref="SessionState.DisposeAsync"/> is itself idempotent, so
/// a session closed explicitly and later swept by <see cref="DisposeAllAsync"/> disposes exactly once.
/// </remarks>
internal sealed class SessionRegistry
{
    private readonly ConcurrentDictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);

    /// <summary>Registers a newly created session.</summary>
    public void Add(SessionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        this._sessions[state.SessionId] = state;
    }

    /// <summary>Looks up a live session by id.</summary>
    public bool TryGet(string sessionId, out SessionState? state) =>
        this._sessions.TryGetValue(sessionId, out state);

    /// <summary>
    /// Looks up a live session by id, throwing the JSON-RPC error the spec requires (§8.4:
    /// "Unknown session id" → <see cref="ErrorCode.InvalidParams"/>, message names the id) when it
    /// is not found.
    /// </summary>
    public SessionState GetRequired(string sessionId)
    {
        if (!this.TryGet(sessionId, out SessionState? state))
        {
            throw new AcpJsonRpcException(ErrorCode.InvalidParams, $"Unknown session id '{sessionId}'.");
        }

        return state!;
    }

    /// <summary>
    /// Removes and disposes the named session. Throws the same "unknown session id" error as
    /// <see cref="GetRequired"/> when no session with that id is registered.
    /// </summary>
    public async Task CloseAsync(string sessionId)
    {
        if (!this._sessions.TryRemove(sessionId, out SessionState? state))
        {
            throw new AcpJsonRpcException(ErrorCode.InvalidParams, $"Unknown session id '{sessionId}'.");
        }

        await state.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Disposes every currently registered session and clears the registry. Called on transport
    /// disconnect and process shutdown so a client that never sends <c>session/close</c> cannot
    /// leak an <see cref="Agency.Harness.Tools.McpClientPool"/> or DI scope for the life of the process.
    /// </summary>
    public async Task DisposeAllAsync()
    {
        foreach (string sessionId in this._sessions.Keys.ToArray())
        {
            if (this._sessions.TryRemove(sessionId, out SessionState? state))
            {
                await state.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
