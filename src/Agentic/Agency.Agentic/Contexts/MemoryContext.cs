namespace Agency.Agentic.Contexts;

/// <summary>Long-term memory items for the current session.</summary>
public sealed record MemoryContext
{
    /// <summary>Gets the shared empty memory context.</summary>
    public static MemoryContext Empty { get; } = new();

    /// <summary>
    /// Gets long-term memory entries. These are summarized into the system prompt.
    /// </summary>
    public IReadOnlyList<string> LongTermMemory { get; init; } = [];
}
