namespace Agency.GraphRAG.Code.Query;

/// <summary>
/// Holds the retrieved graph context for a query.
/// </summary>
public sealed record QueryRetrievalResult
{
    /// <summary>Gets the surfaced clusters.</summary>
    public IReadOnlyList<QueryClusterResult> Clusters { get; init; } = [];

    /// <summary>Gets infrastructure clusters that were aggregated instead of expanded.</summary>
    public IReadOnlyList<QueryClusterResult> InfrastructureClusters { get; init; } = [];

    /// <summary>Gets the surfaced symbols.</summary>
    public IReadOnlyList<QuerySymbolResult> Symbols { get; init; } = [];

    /// <summary>Gets a value indicating whether low-confidence references were observed.</summary>
    public bool HasLowConfidenceReferences { get; init; }
}
