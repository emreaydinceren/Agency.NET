using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Storage;
using ClusterRecord = Agency.GraphRAG.Code.Domain.Cluster;

namespace Agency.GraphRAG.Code.Query;

/// <summary>
/// Combines vector search, graph traversal, and cluster lookups into a single query-time retrieval pass.
/// </summary>
public sealed class HybridRetriever(
    IGraphStore graphStore,
    IClusterQuerySource clusterQuerySource,
    ISymbolTextProvider symbolTextProvider)
{
    /// <summary>
    /// Retrieves graph context for the supplied plan.
    /// </summary>
    public async Task<QueryRetrievalResult> RetrieveAsync(QueryPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        Dictionary<Guid, QuerySymbolResult> symbolsById = [];
        List<QueryClusterResult> clusters = [];
        List<QueryClusterResult> infrastructureClusters = [];
        bool hasLowConfidenceReferences = false;

        if (plan.UseSymbolVectorSearch)
        {
            IReadOnlyList<VectorSearchResult> vectorResults = await graphStore.VectorSearchSymbolsAsync(
                plan.QueryText,
                plan.SymbolTopK,
                cancellationToken).ConfigureAwait(false);

            foreach (VectorSearchResult result in vectorResults.OrderByDescending(static result => result.Score))
            {
                Symbol? symbol = await graphStore.GetSymbolByIdAsync(result.Id, cancellationToken).ConfigureAwait(false);
                if (symbol is null)
                {
                    continue;
                }

                symbolsById[symbol.Id] = new QuerySymbolResult
                {
                    Symbol = symbol,
                    Score = result.Score,
                    Depth = 0,
                    RawCode = await symbolTextProvider.LoadAsync(symbol, cancellationToken).ConfigureAwait(false),
                };
            }
        }

        if (plan.UseTraversal)
        {
            IReadOnlyList<Guid> seedIds = symbolsById.Keys.ToArray();
            if (seedIds.Count == 0)
            {
                string lookupTerm = string.IsNullOrWhiteSpace(plan.FocusTerm) ? plan.QueryText : plan.FocusTerm;
                IReadOnlyList<Symbol> seeds = await graphStore.FindSymbolsByNameAsync(lookupTerm, cancellationToken).ConfigureAwait(false);
                foreach (Symbol seed in seeds)
                {
                    if (!symbolsById.ContainsKey(seed.Id))
                    {
                        symbolsById[seed.Id] = new QuerySymbolResult
                        {
                            Symbol = seed,
                            Score = 1.0,
                            Depth = 0,
                            RawCode = await symbolTextProvider.LoadAsync(seed, cancellationToken).ConfigureAwait(false),
                        };
                    }
                }

                seedIds = symbolsById.Keys.ToArray();
            }

            if (seedIds.Count > 0)
            {
                IReadOnlyList<TraversalHop> hops = await graphStore.TraverseFromAsync(
                    new TraversalRequest
                    {
                        SeedSymbolIds = seedIds,
                        EdgeKinds = plan.TraversalEdgeKinds,
                        MaxHops = plan.TraversalMaxHops,
                        Direction = plan.TraversalDirection,
                    },
                    cancellationToken).ConfigureAwait(false);

                foreach (TraversalHop hop in hops.Where(static hop => hop.Depth > 0))
                {
                    Symbol? symbol = await graphStore.GetSymbolByIdAsync(hop.SymbolId, cancellationToken).ConfigureAwait(false);
                    if (symbol is null)
                    {
                        continue;
                    }

                    hasLowConfidenceReferences |= hop.ViaEdge is not null
                        && (hop.ViaEdge.Confidence < 0.6 || hop.ViaEdge.Signals.Contains(Signal.Unresolved));

                    QuerySymbolResult candidate = new()
                    {
                        Symbol = symbol,
                        Score = 1.0 / (hop.Depth + 1),
                        Depth = hop.Depth,
                        RawCode = await symbolTextProvider.LoadAsync(symbol, cancellationToken).ConfigureAwait(false),
                    };

                    if (!symbolsById.TryGetValue(symbol.Id, out QuerySymbolResult? existing)
                        || candidate.Depth < existing.Depth
                        || candidate.Score > existing.Score)
                    {
                        symbolsById[symbol.Id] = candidate;
                    }
                }
            }
        }

        if (plan.UseClusterVectorSearch)
        {
            IReadOnlyList<VectorSearchResult> clusterResults = await graphStore.VectorSearchClustersAsync(
                plan.QueryText,
                plan.ClusterTopK,
                cancellationToken).ConfigureAwait(false);
            IReadOnlyList<Guid> clusterIds = clusterResults.Select(static result => result.Id).ToArray();
            IReadOnlyDictionary<Guid, ClusterRecord> clusterMap = (await clusterQuerySource.GetClustersAsync(clusterIds, cancellationToken).ConfigureAwait(false))
                .ToDictionary(cluster => cluster.Id);

            foreach (VectorSearchResult result in clusterResults.OrderByDescending(static result => result.Score))
            {
                if (!clusterMap.TryGetValue(result.Id, out ClusterRecord? cluster))
                {
                    continue;
                }

                QueryClusterResult candidate = new()
                {
                    Cluster = cluster,
                    Score = result.Score,
                };

                if (plan.AggregateInfrastructureClusters && cluster.Type == ClusterType.Infrastructure)
                {
                    infrastructureClusters.Add(candidate);
                    continue;
                }

                if (plan.PreferredClusterTypes.Count > 0 && !plan.PreferredClusterTypes.Contains(cluster.Type))
                {
                    if (cluster.Type == ClusterType.Mixed && plan.IncludeMixedClusters)
                    {
                        clusters.Add(candidate);
                    }

                    continue;
                }

                clusters.Add(candidate);
            }
        }

        return new QueryRetrievalResult
        {
            Clusters = clusters,
            InfrastructureClusters = infrastructureClusters,
            Symbols = symbolsById.Values
                .OrderByDescending(static result => result.Score)
                .ThenBy(static result => result.Depth)
                .ThenBy(static result => result.Symbol.FileId)
                .ThenBy(static result => result.Symbol.SourceRangeStart)
                .ToArray(),
            HasLowConfidenceReferences = hasLowConfidenceReferences,
        };
    }
}
