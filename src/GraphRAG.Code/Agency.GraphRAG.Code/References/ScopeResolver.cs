using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Storage;

namespace Agency.GraphRAG.Code.References;

/// <summary>
/// Resolves the set of symbols reachable for name resolution from a source file's local symbols and imports.
/// </summary>
public sealed class ScopeResolver(
    IGraphStore graphStore,
    Func<Guid, CancellationToken, Task<IReadOnlyList<Symbol>>> loadSymbolsByFileAsync)
{
    /// <summary>
    /// Resolves the reachable symbol identifiers for a source file.
    /// </summary>
    /// <param name="sourceFileId">The source file identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The set of reachable symbol identifiers including the file-local symbols.</returns>
    public async Task<IReadOnlySet<Guid>> ResolveAsync(Guid sourceFileId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<Symbol> localSymbols = await loadSymbolsByFileAsync(sourceFileId, cancellationToken);
        if (localSymbols.Count == 0)
        {
            return new HashSet<Guid>();
        }

        HashSet<Guid> reachable = localSymbols.Select(static symbol => symbol.Id).ToHashSet();
        IReadOnlyList<TraversalHop> hops = await graphStore.TraverseFromAsync(
            new TraversalRequest
            {
                SeedSymbolIds = localSymbols.Select(static symbol => symbol.Id).ToArray(),
                EdgeKinds = [EdgeKind.Imports, EdgeKind.DependsOn, EdgeKind.Defines, EdgeKind.MemberOf, EdgeKind.Contains],
                MaxHops = 3,
                Direction = TraversalDirection.Outgoing,
            },
            cancellationToken);

        foreach (TraversalHop hop in hops)
        {
            reachable.Add(hop.SymbolId);
        }

        return reachable;
    }
}
