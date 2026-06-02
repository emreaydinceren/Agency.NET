namespace Agency.Harness.Contexts;

/// <summary>Long-term memory items for the current session.</summary>
public sealed record MemoryContext
{
    /// <summary>Gets the shared empty memory context.</summary>
    public static MemoryContext Empty { get; } = new();

    /// <summary>
    /// Gets long-term memory entries. These are summarized into the system prompt.
    /// </summary>
    public IReadOnlyList<string> LongTermMemory { get; init; } = [];

    /// <summary>
    /// Gets the episodic <see cref="MemoryRecord"/> items retrieved from the memory store
    /// and injected by the retrieval engine. Each record has <c>ContentType == Memory</c>.
    /// Set by <c>RetrievalEngine</c> in <c>OnPreIteration</c>; rendered as <c>## Memories</c>
    /// in the system prompt.
    /// </summary>
    public IReadOnlyList<MemoryRecord> Records { get; init; } = [];
}
