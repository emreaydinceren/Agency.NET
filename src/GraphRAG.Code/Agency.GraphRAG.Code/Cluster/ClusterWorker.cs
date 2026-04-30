using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Storage;

namespace Agency.GraphRAG.Code.Cluster;

/// <summary>
/// Loads cluster input data for <see cref="ClusterWorker"/>.
/// </summary>
public interface IClusterGraphProvider
{
    /// <summary>
    /// Loads the current symbol graph and symbol payloads.
    /// </summary>
    Task<ClusterWorkspace> LoadAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Drives the re-clustering workflow end to end.
/// </summary>
public sealed class ClusterWorker(
    IGraphStore graphStore,
    IClusterGraphProvider graphProvider,
    ITwoPassClusterer clusterer,
    IClusterSummarizer summarizer)
{
    /// <summary>
    /// Runs a full clustering pass and commits the results atomically.
    /// </summary>
    public async Task RunAsync(ClusterOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        ClusterWorkspace workspace = await graphProvider.LoadAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ClusterAssignment> assignments = clusterer.Cluster(workspace.Graph, options);

        await graphStore.ApplyClusterAssignmentsAsync(
            assignments.ToDictionary(
                static assignment => assignment.SymbolId,
                static assignment => (assignment.ClusterId, assignment.Kind == ClusterMembershipKind.Utility ? "utility" : "primary")),
            cancellationToken).ConfigureAwait(false);

        IReadOnlyList<ClusterSummaryRequest> summaryRequests = assignments
            .GroupBy(static assignment => assignment.ClusterId)
            .Select(group =>
            {
                IReadOnlyList<Symbol> symbols = group
                    .Select(assignment => workspace.Symbols[assignment.SymbolId])
                    .OrderBy(static symbol => symbol.FullyQualifiedName ?? symbol.Name, StringComparer.Ordinal)
                    .ToArray();
                ClusterMembershipKind origin = group.Any(static assignment => assignment.Kind == ClusterMembershipKind.Utility)
                    ? ClusterMembershipKind.Utility
                    : ClusterMembershipKind.Primary;
                string label = origin == ClusterMembershipKind.Utility
                    ? $"Infrastructure ({workspace.Graph.Nodes[group.First().SymbolId].ProjectKey})"
                    : InferPrimaryLabel(symbols);

                return new ClusterSummaryRequest(group.Key, label, origin, symbols);
            })
            .OrderBy(static request => request.Label, StringComparer.Ordinal)
            .ToArray();

        IReadOnlyList<Agency.GraphRAG.Code.Domain.Cluster> summaries = await summarizer.SummarizeAsync(summaryRequests, cancellationToken).ConfigureAwait(false);
        await graphStore.ReplaceClusterSummariesAtomicallyAsync(summaries, cancellationToken).ConfigureAwait(false);
    }

    private static string InferPrimaryLabel(IReadOnlyList<Symbol> symbols)
    {
        string? namespaceLabel = symbols
            .Select(static symbol => GetNamespacePrefix(symbol.FullyQualifiedName))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .GroupBy(static value => value!, StringComparer.Ordinal)
            .OrderByDescending(static group => group.Count())
            .ThenBy(static group => group.Key, StringComparer.Ordinal)
            .Select(static group => group.Key)
            .FirstOrDefault();

        return namespaceLabel ?? symbols.First().Name;
    }

    private static string? GetNamespacePrefix(string? fullyQualifiedName)
    {
        if (string.IsNullOrWhiteSpace(fullyQualifiedName))
        {
            return null;
        }

        int lastDot = fullyQualifiedName.LastIndexOf('.');
        return lastDot <= 0 ? fullyQualifiedName : fullyQualifiedName[..lastDot];
    }
}

/// <summary>
/// Contains the raw graph and symbol payloads required for clustering.
/// </summary>
/// <param name="Graph">The cluster graph.</param>
/// <param name="Symbols">The symbols keyed by identifier.</param>
public sealed record ClusterWorkspace(ClusterGraph Graph, IReadOnlyDictionary<Guid, Symbol> Symbols);
