using Agency.GraphRAG.Code.Chunker;
using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Storage;

namespace Agency.GraphRAG.Code.Hydration;

/// <summary>
/// Writes definition-phase graph records for parsed files, including symbols, containment edges, import edges, and staged call sites.
/// </summary>
public sealed class Phase1Writer(IGraphStore graphStore)
{
    /// <summary>
    /// Persists the provided parsed-file data into the graph store.
    /// </summary>
    /// <param name="request">The file write request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public async Task WriteAsync(Phase1WriteRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await graphStore.UpsertFileAsync(request.File, cancellationToken).ConfigureAwait(false);

        if (request.Module is not null)
        {
            await graphStore.UpsertModuleAsync(request.Module, cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<Symbol> symbols = request.Chunks
            .Select(chunk => ToSymbol(chunk, request.File.Id, request.Module?.Id, request.Summaries))
            .ToArray();
        await graphStore.UpsertSymbolBatchAsync(symbols, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<Edge> edges = BuildEdges(request, symbols);
        if (edges.Count > 0)
        {
            await graphStore.UpsertEdgeBatchAsync(edges, cancellationToken).ConfigureAwait(false);
        }

        if (request.UnresolvedCallSites.Count > 0)
        {
            await graphStore.StageUnresolvedCallSiteBatchAsync(request.UnresolvedCallSites, cancellationToken).ConfigureAwait(false);
        }
    }

    private static Symbol ToSymbol(
        Chunk chunk,
        Guid fileId,
        Guid? moduleId,
        IReadOnlyDictionary<string, Summarizer.SymbolSummary> summaries)
    {
        summaries.TryGetValue(chunk.Id, out Summarizer.SymbolSummary? summary);

        return new Symbol
        {
            Id = HydrationIds.StableGuid(chunk.Id),
            FileId = fileId,
            ModuleId = moduleId,
            Name = chunk.Name,
            FullyQualifiedName = chunk.FullyQualifiedName,
            Kind = chunk.SymbolKind,
            Signature = chunk.Signature,
            Summary = summary?.Detailed,
            OneLineSummary = summary?.OneLine,
            ContentHash = ComputeContentHash(chunk.Content),
            Embedding = summary?.OneLineEmbedding.ToArray(),
            IsUtility = false,
            SourceRangeStart = chunk.Range.StartLine,
            SourceRangeEnd = chunk.Range.EndLine,
        };
    }

    private static IReadOnlyList<Edge> BuildEdges(Phase1WriteRequest request, IReadOnlyList<Symbol> symbols)
    {
        Dictionary<string, Symbol> symbolsByChunkId = request.Chunks
            .Zip(symbols, static (chunk, symbol) => new { chunk.Id, Symbol = symbol })
            .ToDictionary(static pair => pair.Id, static pair => pair.Symbol, StringComparer.Ordinal);
        List<Edge> edges = [];

        foreach ((Chunk chunk, Symbol symbol) in request.Chunks.Zip(symbols, static (chunk, symbol) => (chunk, symbol)))
        {
            if (request.Module is not null)
            {
                edges.Add(CreateEdge(request.Module.Id, "module", symbol.Id, "symbol", EdgeKind.Contains, $"{request.Module.Id}:{symbol.Id}:contains"));
            }

            edges.Add(CreateEdge(request.File.Id, "file", symbol.Id, "symbol", EdgeKind.Defines, $"{request.File.Id}:{symbol.Id}:defines"));

            foreach (ImportReference importReference in chunk.ImportsInScope
                         .GroupBy(static importReference => importReference.Source, StringComparer.OrdinalIgnoreCase)
                         .Select(static group => group.First()))
            {
                Guid importId = HydrationIds.StableGuid($"import:{importReference.Source}");
                edges.Add(CreateEdge(request.File.Id, "file", importId, "import", EdgeKind.Imports, $"{request.File.Id}:{importReference.Source}:imports"));
            }

            if (!string.IsNullOrWhiteSpace(chunk.ParentId)
                && symbolsByChunkId.TryGetValue(chunk.ParentId, out Symbol? parentSymbol))
            {
                edges.Add(CreateEdge(parentSymbol.Id, "symbol", symbol.Id, "symbol", EdgeKind.Contains, $"{parentSymbol.Id}:{symbol.Id}:contains"));
            }
        }

        return edges
            .GroupBy(static edge => (edge.SourceId, edge.TargetId, edge.EdgeKind))
            .Select(static group => group.First())
            .ToArray();
    }

    private static Edge CreateEdge(Guid sourceId, string sourceKind, Guid targetId, string targetKind, EdgeKind edgeKind, string seed) =>
        new()
        {
            Id = HydrationIds.StableGuid(seed),
            SourceId = sourceId,
            SourceKind = sourceKind,
            TargetId = targetId,
            TargetKind = targetKind,
            EdgeKind = edgeKind,
            Confidence = 1.0d,
            Signals = [],
            Properties = new Dictionary<string, object?>(),
        };

    private static string ComputeContentHash(string content)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(hash);
    }
}
