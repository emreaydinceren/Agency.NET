namespace Agency.Agentic.Contexts;

/// <summary>Domain facts re-injected into the system prompt on every iteration (D3).</summary>
public sealed record KnowledgeContext
{
    /// <summary>Gets the shared empty knowledge context.</summary>
    public static KnowledgeContext Empty { get; } = new();

    /// <summary>Gets the factual statements to include in the system prompt.</summary>
    public IReadOnlyList<string> Facts { get; init; } = [];

    /// <summary>
    /// Gets the fact <see cref="MemoryRecord"/> items retrieved from the memory store
    /// and injected by the retrieval engine. Each record has <c>ContentType == Fact</c>.
    /// Set by <c>RetrievalEngine</c> in <c>OnPreIteration</c>; rendered as <c>## Facts</c>
    /// in the system prompt.
    /// </summary>
    public IReadOnlyList<MemoryRecord> Records { get; init; } = [];
}
