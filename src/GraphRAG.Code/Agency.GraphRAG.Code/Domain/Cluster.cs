namespace Agency.GraphRAG.Code.Domain;

/// <summary>
/// Represents a semantically coherent group of symbols identified during graph analysis.
/// </summary>
public record class Cluster
{
    /// <summary>Gets the unique identifier for this cluster.</summary>
    public required Guid Id { get; init; }

    /// <summary>Gets the human-readable label describing the cluster's topic.</summary>
    public required string Label { get; init; }

    /// <summary>Gets the classification type of this cluster.</summary>
    public required ClusterType Type { get; init; }

    /// <summary>Gets the coherence score for this cluster in the range [0.0, 1.0].</summary>
    public required double CoherenceScore { get; init; }

    /// <summary>Gets a longer prose summary of the cluster, or <c>null</c> if not yet generated.</summary>
    public string? Summary { get; init; }

    /// <summary>Gets the embedding vector for this cluster, or <c>null</c> if not yet computed.</summary>
    public float[]? Embedding { get; init; }
}
