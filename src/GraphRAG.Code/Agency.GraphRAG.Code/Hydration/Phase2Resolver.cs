using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.References;
using Agency.GraphRAG.Code.Storage;

namespace Agency.GraphRAG.Code.Hydration;

/// <summary>
/// Resolves staged unresolved call sites into scored reference edges.
/// </summary>
public sealed class Phase2Resolver(
    IGraphStore graphStore,
    ScopeResolver scopeResolver,
    ReferenceScorer referenceScorer)
{
    /// <summary>
    /// Resolves the staged call sites for a single source file.
    /// </summary>
    /// <param name="sourceFileId">The source file identifier to drain.</param>
    /// <param name="externalPackages">The external packages in scope for the file's project.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public async Task ResolveAsync(
        Guid sourceFileId,
        IReadOnlyList<ExternalPackage> externalPackages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(externalPackages);

        IReadOnlyList<UnresolvedCallSite> callSites = await graphStore.DrainUnresolvedCallSitesAsync(sourceFileId, cancellationToken).ConfigureAwait(false);
        if (callSites.Count == 0)
        {
            return;
        }

        IReadOnlySet<Guid> reachable = await scopeResolver.ResolveAsync(sourceFileId, cancellationToken).ConfigureAwait(false);
        List<Edge> resolvedEdges = [];

        foreach (UnresolvedCallSite callSite in callSites)
        {
            IReadOnlyList<Symbol> candidates = await graphStore.FindSymbolsByNameAsync(callSite.Identifier, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<Symbol> scopedCandidates = candidates
                .Where(candidate => reachable.Contains(candidate.Id))
                .ToArray();

            IReadOnlyList<ResolutionResult> results = referenceScorer.Score(
                callSite.Identifier,
                scopedCandidates,
                externalPackages,
                callSite.LlmExtractedTarget);

            foreach (ResolutionResult result in results.Where(static result => result.TargetSymbolId.HasValue))
            {
                Guid targetId = result.TargetSymbolId!.Value;
                resolvedEdges.Add(new Edge
                {
                    Id = HydrationIds.StableGuid($"{callSite.SourceSymbolId}:{targetId}:reference"),
                    SourceId = callSite.SourceSymbolId,
                    SourceKind = "symbol",
                    TargetId = targetId,
                    TargetKind = "symbol",
                    EdgeKind = EdgeKind.References,
                    Confidence = result.Confidence,
                    Signals = result.Signals,
                    Properties = new Dictionary<string, object?>
                    {
                        ["identifier"] = callSite.Identifier,
                        ["scope"] = callSite.Scope,
                    },
                });
            }
        }

        if (resolvedEdges.Count > 0)
        {
            await graphStore.UpsertEdgeBatchAsync(resolvedEdges, cancellationToken).ConfigureAwait(false);
        }
    }
}
