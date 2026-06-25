
using Agency.Harness.Contexts;
using Agency.Harness.Permissions;
using System.Runtime.CompilerServices;

namespace Agency.Harness;
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
public sealed class ChatSession : IAsyncDisposable
{
    private Agent _agent;
    private readonly AgentOptions _options;
    private readonly ToolContext _toolContext;
    private readonly UserSpecificContext? _user;
    private readonly SkillContext? _skills;
    private Context? _ctx;
    private int _turnCount;
    private bool _disposed;

    /// <summary>
    /// Initialises a new session bound to the supplied <paramref name="agent"/>.
    /// </summary>
    /// <param name="agent">The agent that will process each turn.</param>
    /// <param name="options">Agent options forwarded to <see cref="Agent.ChatAsync"/> on every turn.</param>
    /// <param name="toolContext">
    /// Optional tool registry made available to the agent. Defaults to <see cref="ToolContext.Empty"/>.
    /// </param>
    /// <param name="user">Optional caller identity propagated into the context on first send.</param>
    /// <param name="skills">Optional skill catalog context; defaults to <see cref="SkillContext.Empty"/>.</param>
    public ChatSession(Agent agent, AgentOptions options, ToolContext? toolContext = null, UserSpecificContext? user = null, SkillContext? skills = null)
    {
        this._agent = agent ?? throw new ArgumentNullException(nameof(agent));
        this._options = options ?? throw new ArgumentNullException(nameof(options));
        this._toolContext = toolContext ?? ToolContext.Empty;
        this._user = user;
        this._skills = skills;
    }

    /// <summary>Gets the model identifier of the agent driving this session.</summary>
    internal string WorkerModel => this._agent.Model;

    /// <summary>Gets the client-type display name of the agent driving this session (e.g. "Claude").</summary>
    internal string WorkerClientType => this._agent.ClientType;

    /// <summary>Gets the accumulated token usage for this session, or zero if no turns have been sent yet.</summary>
    public LlmTokenUsage TotalUsage => this._ctx?.TotalUsage ?? new LlmTokenUsage(0, 0);

    /// <summary>Gets the accumulated USD cost for this session, or zero if no turns have been sent yet.</summary>
    public decimal TotalCostUsd => this._ctx?.TotalCostUsd ?? 0m;

    /// <summary>Gets the number of successfully completed turns (i.e. turns that received an <see cref="AgentResultEvent"/>).</summary>
    public int TurnCount => this._turnCount;

    /// <summary>Gets a value indicating whether the first turn has been sent and the underlying <see cref="Context"/> created.</summary>
    public bool IsStarted => this._ctx is not null;

    /// <summary>
    /// Returns the context that would be sent to the model: the live conversation context once
    /// the first turn has started, or — before then — a freshly built preview reflecting the
    /// system-prompt inputs, tools, environment, and user for the next turn. Built with the same
    /// factory <see cref="SendAsync"/> uses; the preview is not stored and does not start the session.
    /// </summary>
    internal Context PreviewContext() => this._ctx ?? Agent.CreateContext(
        string.Empty,
        this._toolContext,
        new EnvironmentalContext { ContextWindowSize = this._options.ContextWindowSize },
        user: this._user,
        timeProvider: this._agent.TimeProvider,
        skills: this._skills);

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
            new EnvironmentalContext { ContextWindowSize = this._options.ContextWindowSize },
            user: this._user,
            timeProvider: this._agent.TimeProvider,
            skills: this._skills);

        // Abandonment (spec §6.4): if a turn is parked and the user sends a new message,
        // implicitly deny all pending calls with the abandonment reason, complete the batch
        // (steps 5–6 of §6.3), then proceed normally with the new message.
        // We do NOT continue the loop here — ChatAsync below handles the new user message.
        if (this._ctx.PendingToolBatch is { } parkedBatch)
        {
            var abandonResponseById = parkedBatch.Pending.ToDictionary(
                p => p.RequestId,
                p => new PermissionResponse(
                    p.RequestId,
                    PermissionResponseKind.DenyOnce,
                    "The user did not respond to the permission request."));

            // Complete the batch (execute/deny pending calls, append results, clear PendingToolBatch).
            // Events are discarded — the abandoned results are already in conversation history.
            await this._agent.ExecutePendingBatchAsync(this._ctx, parkedBatch, abandonResponseById, ct);
        }

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
    /// Resumes a turn parked with <see cref="AgentResultStatus.AwaitingPermission"/>.
    /// Every pending <see cref="PermissionRequestedEvent.RequestId"/> must have exactly one response.
    /// Streams the remainder of the turn (completed-batch <see cref="ToolInvokedEvent"/>s, then the loop continues).
    /// </summary>
    /// <param name="responses">
    /// One <see cref="PermissionResponse"/> per pending <see cref="PermissionRequestedEvent"/>.
    /// </param>
    /// <param name="ct">Cancellation token for the resumed turn.</param>
    /// <exception cref="InvalidOperationException">No turn is parked.</exception>
    /// <exception cref="ArgumentException">Responses are missing, duplicated, or unknown.</exception>
    public IAsyncEnumerable<AgentEvent> ResumeWithPermissionsAsync(
        IReadOnlyList<PermissionResponse> responses,
        CancellationToken ct = default)
    {
        if (this._ctx is null)
        {
            throw new InvalidOperationException(
                "No turn is in progress. Call SendAsync before ResumeWithPermissionsAsync.");
        }

        // Delegate to Agent.ResumeAsync, which performs the remaining eager validation
        // (PendingToolBatch null check and response validation).
        return this._agent.ResumeAsync(this._ctx, responses, this._options, ct);
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

    /// <summary>
    /// Fires the agent's <c>OnSessionEnd</c> hook (once) to signal end-of-session, then
    /// marks the session disposed. Safe to call multiple times.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (this._disposed)
        {
            return;
        }

        this._disposed = true;
        if (this._ctx is not null)
        {
            await this._agent.RaiseSessionEndAsync(this._ctx);
        }
    }
}
