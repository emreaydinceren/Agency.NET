namespace Agency.Agentic.Contexts;

/// <summary>Short- and long-term memory items for the current session.</summary>
public sealed record MemoryContext
{
    /// <summary>Gets the shared empty memory context.</summary>
    public static MemoryContext Empty { get; } = new();

    /// <summary>
    /// Gets short-term memory entries. These are seeded into the conversation as prior turns at session start rather
    /// than into the system prompt.
    /// </summary>
    public IReadOnlyList<string> ShortTermMemory { get; init; } = [];

    /// <summary>
    /// Gets long-term memory entries. These are summarized into the system prompt.
    /// </summary>
    public IReadOnlyList<string> LongTermMemory { get; init; } = [];
}
