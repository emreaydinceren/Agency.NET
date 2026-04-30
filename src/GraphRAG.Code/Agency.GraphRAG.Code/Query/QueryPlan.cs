using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Storage;

namespace Agency.GraphRAG.Code.Query;

/// <summary>
/// Describes how a query should be retrieved from the graph.
/// </summary>
public sealed record QueryPlan
{
    /// <summary>Gets the original user query.</summary>
    public required string QueryText { get; init; }

    /// <summary>Gets the classified query category.</summary>
    public required QueryCategory Category { get; init; }

    /// <summary>Gets an optional narrowed focus term used to resolve seed symbols.</summary>
    public string? FocusTerm { get; init; }

    /// <summary>Gets a value indicating whether symbol vector search should run.</summary>
    public bool UseSymbolVectorSearch { get; init; }

    /// <summary>Gets a value indicating whether cluster vector search should run.</summary>
    public bool UseClusterVectorSearch { get; init; }

    /// <summary>Gets a value indicating whether graph traversal should run.</summary>
    public bool UseTraversal { get; init; }

    /// <summary>Gets the symbol vector top-k.</summary>
    public int SymbolTopK { get; init; } = 5;

    /// <summary>Gets the cluster vector top-k.</summary>
    public int ClusterTopK { get; init; } = 3;

    /// <summary>Gets the traversal direction.</summary>
    public TraversalDirection TraversalDirection { get; init; } = TraversalDirection.Outgoing;

    /// <summary>Gets the traversal edge kinds.</summary>
    public IReadOnlyList<EdgeKind> TraversalEdgeKinds { get; init; } = [];

    /// <summary>Gets the traversal hop limit.</summary>
    public int TraversalMaxHops { get; init; } = 2;

    /// <summary>Gets the primary cluster types to surface.</summary>
    public IReadOnlyList<ClusterType> PreferredClusterTypes { get; init; } = [];

    /// <summary>Gets a value indicating whether mixed clusters should surface with a confidence note.</summary>
    public bool IncludeMixedClusters { get; init; }

    /// <summary>Gets a value indicating whether infrastructure clusters should be aggregated into a footer.</summary>
    public bool AggregateInfrastructureClusters { get; init; }
}
