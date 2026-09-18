using Agency.Harness;
using Agency.Harness.Tools;
using Agency.Llm.Common;
using Microsoft.Extensions.DependencyInjection;

namespace Agency.Acp.Sessions;

/// <summary>
/// The full per-session object graph (spec §7.2): the session's own <see cref="Harness.ChatSession"/>,
/// a per-session <see cref="AgentOptions"/> instance, its <see cref="McpClientPool"/>, and the DI
/// scope that owns them.
/// </summary>
/// <remarks>
/// Disposal is idempotent (see <see cref="DisposeAsync"/>) because it is reachable from three
/// independent triggers — <c>session/close</c>, transport disconnect, and process shutdown (spec
/// §6.3, E-15) — and at most one of them may actually do the disposal work.
/// </remarks>
internal sealed class SessionState : IAsyncDisposable
{
    private int _disposed;
    private int _inFlight;

    /// <summary>Gets the id generated for this session at <c>session/new</c>.</summary>
    public required string SessionId { get; init; }

    /// <summary>Gets the session's own <see cref="Harness.ChatSession"/>.</summary>
    public required ChatSession ChatSession { get; init; }

    /// <summary>
    /// Gets the per-session <see cref="AgentOptions"/> instance — a clone of the process-wide
    /// options with only <see cref="AgentOptions.ContextWindowSize"/> varied — so differing
    /// context-window sizes across sessions never bleed into one another.
    /// </summary>
    public required AgentOptions Options { get; init; }

    /// <summary>Gets the pool of MCP clients connected for this session's <c>mcpServers</c>.</summary>
    public required McpClientPool McpPool { get; init; }

    /// <summary>Gets the DI scope this session's services were resolved from.</summary>
    public required IServiceScope Scope { get; init; }

    /// <summary>
    /// Gets the working directory recorded from <c>session/new</c>. Diagnostics only: no tool can
    /// observe it (spec §6.5), so it is recorded here rather than silently dropped (spec §6.3).
    /// </summary>
    public required string Cwd { get; init; }

    /// <summary>
    /// Gets the model catalogue resolved at <c>session/new</c> (spec §8.1 step 2), already filtered
    /// to exclude known embedding models. Kept so <c>session/set_config_option</c> can rebuild the
    /// full <see cref="dotacp.protocol.SessionConfigOption"/> set without re-fetching the catalogue.
    /// </summary>
    public IReadOnlyList<Model> Catalogue { get; init; } = [];

    /// <summary>Gets or sets the model id selected via <c>session/new</c> or <c>session/set_config_option</c>.</summary>
    public string? ModelId { get; set; }

    /// <summary>
    /// Gets the Persona identity parsed from <c>_meta.systemPrompt</c> at <c>session/new</c> (spec
    /// §7.2), or <see langword="null"/> when the runtime's default identity line applies. Recorded
    /// here — beyond having already been passed into <see cref="ChatSession"/> — for two reasons:
    /// diagnostics ("which identity is this session running?" is otherwise unanswerable), and
    /// rebuild survival ("session/set_config_option" rebuilds the client and <see cref="Agent"/> and
    /// calls <see cref="Harness.ChatSession.SetAgent"/>, which preserves the existing
    /// <see cref="Agency.Harness.Contexts.Context"/> — and therefore the identity — for free;
    /// holding it here too makes that invariant inspectable).
    /// </summary>
    public string? IdentityPrompt { get; init; }

    /// <summary>Gets or sets the effort id selected via <c>session/set_config_option</c>.</summary>
    public string? EffortId { get; set; }

    /// <summary>Gets or sets the cancellation source for the turn currently in flight, if any.</summary>
    public CancellationTokenSource? TurnCts { get; set; }

    /// <summary>
    /// Attempts to mark this session busy for the one-prompt-in-flight guard (spec §6.4). Returns
    /// <see langword="false"/> when a turn is already in flight.
    /// </summary>
    public bool TryEnterTurn() => Interlocked.CompareExchange(ref this._inFlight, 1, 0) == 0;

    /// <summary>Clears the in-flight guard set by <see cref="TryEnterTurn"/>.</summary>
    public void ExitTurn() => Interlocked.Exchange(ref this._inFlight, 0);

    /// <summary>
    /// Disposes the session's <see cref="ChatSession"/> (firing <c>OnSessionEnd</c> once), its
    /// <see cref="McpClientPool"/>, and its DI <see cref="Scope"/>, in that reverse-construction
    /// order. Safe to call more than once — and concurrently — from any combination of
    /// <c>session/close</c>, transport disconnect, and process shutdown; only the first caller
    /// performs the work.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this._disposed, 1) != 0)
        {
            return;
        }

        this.TurnCts?.Cancel();
        this.TurnCts?.Dispose();

        await this.ChatSession.DisposeAsync().ConfigureAwait(false);
        await this.McpPool.DisposeAsync().ConfigureAwait(false);
        this.Scope.Dispose();
    }
}
