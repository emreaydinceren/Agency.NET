namespace Agency.Agentic.Contexts;

/// <summary>Domain facts re-injected into the system prompt on every iteration (D3).</summary>
public sealed record KnowledgeContext
{
    /// <summary>Gets the shared empty knowledge context.</summary>
    public static KnowledgeContext Empty { get; } = new();

    /// <summary>Gets the factual statements to include in the system prompt.</summary>
    public IReadOnlyList<string> Facts { get; init; } = [];
}
