namespace Agency.Agentic.Contexts;

/// <summary>
/// Canonical session state. The wire-format <c>messages[]</c> array is derived from this object on every loop
/// iteration — it is never the source of truth.
/// </summary>
public sealed record Context
{
    /// <summary>Gets the user's query that seeds the conversation.</summary>
    public required QueryContext Query { get; init; }

    /// <summary>Gets the knowledge context re-injected into the system prompt each iteration.</summary>
    public KnowledgeContext Knowledge { get; init; } = KnowledgeContext.Empty;

    /// <summary>Gets the memory context for this session.</summary>
    public MemoryContext Memory { get; init; } = MemoryContext.Empty;

    /// <summary>Gets the tool context providing the registry for this session.</summary>
    public ToolContext Tools { get; init; } = ToolContext.Empty;

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
}
