namespace Agency.GraphRAG.Code.Query;

/// <summary>
/// The assembled LLM context for a query.
/// </summary>
public sealed record QueryContextAssembly
{
    /// <summary>Gets the final context text.</summary>
    public required string ContextText { get; init; }

    /// <summary>Gets the estimated token count.</summary>
    public required int EstimatedTokens { get; init; }

    /// <summary>Gets a value indicating whether truncation occurred.</summary>
    public required bool IsTruncated { get; init; }
}
