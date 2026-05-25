
using Agency.Agentic.Contexts;
using System.Runtime.CompilerServices;

namespace Agency.Agentic;
/// <summary>
/// Maintains the stateful context for a multi-turn conversation and provides a simple
/// <see cref="SendAsync"/> surface that any host — console REPL, ASP.NET Core endpoint,
/// SignalR hub — can consume without knowing about <see cref="Context"/> or
/// <see cref="Agent.ChatAsync"/> directly.
/// </summary>
/// <remarks>
/// A <see cref="ChatSession"/> is not thread-safe. Create one instance per user or per
/// logical connection and ensure that at most one <see cref="SendAsync"/> call is in
/// flight at a time.
/// </remarks>
public sealed class ChatSession
{
    private Agent _agent;
    private readonly AgentOptions _options;
    private readonly ToolContext _toolContext;
    private Context? _ctx;
    private int _turnCount;

    /// <summary>
    /// Initialises a new session bound to the supplied <paramref name="agent"/>.
    /// </summary>
    /// <param name="agent">The agent that will process each turn.</param>
    /// <param name="options">Agent options forwarded to <see cref="Agent.ChatAsync"/> on every turn.</param>
    /// <param name="toolContext">
    /// Optional tool registry made available to the agent. Defaults to <see cref="ToolContext.Empty"/>.
    /// </param>
    public ChatSession(Agent agent, AgentOptions options, ToolContext? toolContext = null)
    {
        this._agent = agent ?? throw new ArgumentNullException(nameof(agent));
        this._options = options ?? throw new ArgumentNullException(nameof(options));
        this._toolContext = toolContext ?? ToolContext.Empty;
    }

    /// <summary>Gets the accumulated token usage for this session, or zero if no turns have been sent yet.</summary>
    public LlmTokenUsage TotalUsage => this._ctx?.TotalUsage ?? new LlmTokenUsage(0, 0);

    /// <summary>Gets the accumulated USD cost for this session, or zero if no turns have been sent yet.</summary>
    public decimal TotalCostUsd => this._ctx?.TotalCostUsd ?? 0m;

    /// <summary>Gets the number of successfully completed turns (i.e. turns that received an <see cref="AgentResultEvent"/>).</summary>
    public int TurnCount => this._turnCount;

    /// <summary>Gets a value indicating whether the first turn has been sent and the underlying <see cref="Context"/> created.</summary>
    public bool IsStarted => this._ctx is not null;

    /// <summary>
    /// Switches the agent used for subsequent turns. Conversation history is preserved;
    /// the new agent will receive the full prior context on its first call.
    /// </summary>
    /// <param name="agent">The replacement agent.</param>
    public void SetAgent(Agent agent)
    {
        this._agent = agent ?? throw new ArgumentNullException(nameof(agent));
    }

    /// <summary>
    /// Sends <paramref name="userMessage"/> to the agent and streams back the resulting
    /// <see cref="AgentEvent"/>s. The underlying <see cref="Context"/> is created lazily
    /// on the first call and reused on subsequent calls so conversation history is preserved.
    /// </summary>
    /// <remarks>
    /// Callers are responsible for handling <see cref="OperationCanceledException"/>; this
    /// method does not catch it. Rendering, command dispatch, and error recovery are
    /// intentionally left to the host.
    /// </remarks>
    /// <param name="userMessage">The user's message for this turn.</param>
    /// <param name="ct">Cancellation token for the turn.</param>
    public async IAsyncEnumerable<AgentEvent> SendAsync(
        string userMessage,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        this._ctx ??= Agent.CreateContext(
            userMessage,
            this._toolContext,
            new EnvironmentalContext { ContextWindowSize = this._options.ContextWindowSize });

        await foreach (AgentEvent evt in this._agent.ChatAsync(userMessage, this._ctx, this._options, ct))
        {
            if (evt is AgentResultEvent)
            {
                this._turnCount++;
            }

            yield return evt;
        }
    }

    /// <summary>
    /// Resets the session by clearing the conversation history and turn count.
    /// The next <see cref="SendAsync"/> call will start a fresh conversation.
    /// </summary>
    public void Reset()
    {
        this._ctx = null;
        this._turnCount = 0;
    }
}
