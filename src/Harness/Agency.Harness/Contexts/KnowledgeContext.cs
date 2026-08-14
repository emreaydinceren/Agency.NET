namespace Agency.Harness.Contexts;

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

    /// <summary>
    /// Gets the memory system's own operating policy — how to treat what was recalled, and when
    /// to persist something new. Rendered as its own <c>## Memory Policy</c> section, above the
    /// recalled records.
    /// </summary>
    /// <remarks>
    /// This is deliberately not a <see cref="Facts"/> entry. The policy is several hundred words
    /// of instruction about the memory system, while <see cref="Facts"/> and <see cref="Records"/>
    /// are content to answer from; rendering them under one heading told the model they were the
    /// same kind of thing, and the policy — being far the longer of the two — buried the records
    /// it was meant to introduce.
    /// </remarks>
    public string? MemoryPolicy { get; init; }
}
