using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Storage;

namespace Agency.GraphRAG.Code.Query;

/// <summary>
/// Maps query categories onto retrieval strategies.
/// </summary>
public sealed class QueryPlanner(QueryClassifier classifier)
{
    /// <summary>
    /// Builds a retrieval plan for the supplied query.
    /// </summary>
    public async Task<QueryPlan> PlanAsync(string query, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        QueryCategory category = await classifier.ClassifyAsync(query, cancellationToken).ConfigureAwait(false);
        return category switch
        {
            QueryCategory.Local => new QueryPlan
            {
                QueryText = query,
                Category = category,
                UseSymbolVectorSearch = true,
                UseTraversal = true,
                SymbolTopK = 5,
                TraversalMaxHops = 1,
                TraversalDirection = TraversalDirection.Outgoing,
                TraversalEdgeKinds = [EdgeKind.References, EdgeKind.Defines, EdgeKind.MemberOf, EdgeKind.Contains],
            },
            QueryCategory.Subsystem => new QueryPlan
            {
                QueryText = query,
                Category = category,
                UseSymbolVectorSearch = true,
                UseClusterVectorSearch = true,
                UseTraversal = true,
                SymbolTopK = 6,
                ClusterTopK = 3,
                TraversalMaxHops = 2,
                TraversalDirection = TraversalDirection.Outgoing,
                TraversalEdgeKinds = [EdgeKind.References, EdgeKind.Defines, EdgeKind.MemberOf, EdgeKind.Contains, EdgeKind.DependsOn],
            },
            QueryCategory.Global => new QueryPlan
            {
                QueryText = query,
                Category = category,
                UseClusterVectorSearch = true,
                ClusterTopK = 6,
                PreferredClusterTypes = [ClusterType.Business],
                IncludeMixedClusters = true,
                AggregateInfrastructureClusters = true,
            },
            QueryCategory.Impact => new QueryPlan
            {
                QueryText = query,
                Category = category,
                FocusTerm = ExtractFocusTerm(query),
                UseTraversal = true,
                TraversalMaxHops = 2,
                TraversalDirection = TraversalDirection.Incoming,
                TraversalEdgeKinds = [EdgeKind.References, EdgeKind.MemberOf, EdgeKind.Contains],
            },
            QueryCategory.Dependency => new QueryPlan
            {
                QueryText = query,
                Category = category,
                FocusTerm = ExtractFocusTerm(query),
                UseTraversal = true,
                TraversalMaxHops = 2,
                TraversalDirection = TraversalDirection.Outgoing,
                TraversalEdgeKinds = [EdgeKind.DependsOn, EdgeKind.Imports, EdgeKind.References],
            },
            _ => throw new InvalidOperationException($"Unsupported query category '{category}'."),
        };
    }

    private static string ExtractFocusTerm(string query)
    {
        string trimmed = query.Trim().TrimEnd('?', '.', '!');
        string[] tokens = trimmed.Split([' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tokens.Length == 0 ? trimmed : tokens[^1];
    }
}
