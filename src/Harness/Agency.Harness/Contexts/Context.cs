namespace Agency.Harness.Contexts;

/// <summary>
/// Canonical session state. The wire-format <c>messages[]</c> array is derived from this object on every loop
/// iteration — it is never the source of truth.
/// </summary>
public sealed record Context
{
    /// <summary>Gets the user's query that seeds the conversation.</summary>
    public required QueryContext Query { get; init; }

    /// <summary>
    /// Gets or sets the knowledge context re-injected into the system prompt each iteration.
    /// Settable (not <c>init</c>-only) so lifecycle hooks such as <c>OnSessionStarted</c> can refresh
    /// domain facts mid-session; the rebuilt system prompt picks up the change on the next iteration.
    /// KnowlegdeContext is immutable, so to update the knowledge facts you must assign a new instance to this property.
    /// Knowledge updates are additive; the system prompt accumulates all facts ever assigned to this property over the session lifetime.
    /// KnowledgeContext contains a collection string Facts.
    /// </summary>
    public KnowledgeContext Knowledge { get; set; } = KnowledgeContext.Empty;

    /// <summary>
    /// Gets or sets the memory context for this session.
    /// Settable so the retrieval engine can inject retrieved episodic records
    /// in <c>OnPreIteration</c>; the rebuilt system prompt picks up the change on
    /// every iteration.
    /// </summary>
    public MemoryContext Memory { get; set; } = MemoryContext.Empty;

    /// <summary>Gets the tool context providing the registry for this session.</summary>
    public ToolContext Tools { get; init; } = ToolContext.Empty;

    /// <summary>
    /// Gets or sets the current retrieval focus. Set by <c>SetFocusTool</c> to bias retrieval
    /// toward a particular task domain without forcing exact-match filtering (Spec §6.7.1).
    /// </summary>
    public FocusContext Focus { get; set; } = FocusContext.Empty;

    /// <summary>Gets user-specific context injected into the system prompt.</summary>
    public UserSpecificContext User { get; init; } = UserSpecificContext.Empty;

    /// <summary>Gets temporal context injected into the system prompt.</summary>
    public TemporalContext Temporal { get; init; } = TemporalContext.Empty;

    /// <summary>Gets environmental context injected into the system prompt.</summary>
    public EnvironmentalContext Environment { get; init; } = EnvironmentalContext.Empty;

    /// <summary>Gets the conversation manager owning the message history for this session.</summary>
    public IConversationManager Conversation { get; init; } = new InMemoryConversationManager();

    // ── Loop-owned mutable state ─────────────────────────────────────────────

    /// <summary>Gets the number of completed LLM iterations.</summary>
    public int IterationCount { get; internal set; }

    /// <summary>Gets the accumulated USD cost for this session.</summary>
    public decimal TotalCostUsd { get; internal set; }

    /// <summary>Gets the accumulated token usage for this session.</summary>
    public LlmTokenUsage TotalUsage { get; internal set; } = new(0, 0);

    // ── Memory retrieval state ────────────────────────────────────────────────

    /// <summary>
    /// Gets or sets the UTC timestamp of the most recent retrieval pass for this session,
    /// or <see langword="null"/> if retrieval has not yet been performed.
    /// Written by the retrieval engine in <c>OnPreIteration</c>; read by the retrieval gate
    /// (Spec §8.1) to decide whether to skip a redundant search.
    /// </summary>
    public DateTimeOffset? MemoryLastRetrievedAt { get; set; }
}
