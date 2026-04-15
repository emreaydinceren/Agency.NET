
using Agency.Agentic.Tools;

namespace Agency.Agentic;

/// <summary>The user's intent for this agent session.</summary>
public sealed record QueryContext
{
    /// <summary>Gets the user's prompt that seeds the conversation.</summary>
    public required string Prompt { get; init; }
}

/// <summary>Domain facts re-injected into the system prompt on every iteration (D3).</summary>
public sealed record KnowledgeContext
{
    /// <summary>Gets the shared empty knowledge context.</summary>
    public static KnowledgeContext Empty { get; } = new();

    /// <summary>Gets the factual statements to include in the system prompt.</summary>
    public IReadOnlyList<string> Facts { get; init; } = [];
}

/// <summary>Short- and long-term memory items for the current session.</summary>
public sealed record MemoryContext
{
    /// <summary>Gets the shared empty memory context.</summary>
    public static MemoryContext Empty { get; } = new();

    /// <summary>
    /// Gets short-term memory entries. These are seeded into the conversation
    /// as prior turns at session start rather than into the system prompt.
    /// </summary>
    public IReadOnlyList<string> ShortTermMemory { get; init; } = [];

    /// <summary>
    /// Gets long-term memory entries. These are summarized into the system prompt.
    /// </summary>
    public IReadOnlyList<string> LongTermMemory { get; init; } = [];
}

/// <summary>The tool registry available to the agent during this session.</summary>
public sealed record ToolContext
{
    /// <summary>Gets a shared empty tool context with no registered tools.</summary>
    public static ToolContext Empty { get; } = new();

    /// <summary>Gets the tool registry to dispatch tool calls against.</summary>
    public IToolRegistry Registry { get; init; } = new ToolRegistry(
        [new ExecutePowershellTool(),
            new ReadFileTool()]);
}

/// <summary>Caller identity and preferences injected into the system prompt.</summary>
public sealed record UserSpecificContext
{
    /// <summary>Gets the shared empty user context.</summary>
    public static UserSpecificContext Empty { get; } = new();

    /// <summary>Gets the user's display name.</summary>
    public string? Name { get; init; }
}

/// <summary>Current date/time injected into the system prompt as a grounding cue.</summary>
public sealed record TemporalContext
{
    /// <summary>Gets the shared empty temporal context.</summary>
    public static TemporalContext Empty { get; } = new();

    /// <summary>Gets the UTC timestamp at session start.</summary>
    public DateTimeOffset? CurrentDateUtc { get; init; }
}

/// <summary>Operating-environment facts injected into the system prompt.</summary>
public sealed record EnvironmentalContext
{
    /// <summary>Gets the shared empty environment context.</summary>
    public static EnvironmentalContext Empty { get; } = new();

    /// <summary>Gets the operating system name/version.</summary>
    public string? OperatingSystem { get; init; }
}

/// <summary>
/// Canonical session state. The wire-format <c>messages[]</c> array is derived
/// from this object on every loop iteration — it is never the source of truth.
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
