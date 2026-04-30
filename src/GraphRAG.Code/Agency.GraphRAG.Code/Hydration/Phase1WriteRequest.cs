using Agency.GraphRAG.Code.Chunker;
using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Summarizer;

namespace Agency.GraphRAG.Code.Hydration;

/// <summary>
/// Describes the data needed to write a parsed file's definitions into the graph.
/// </summary>
public sealed record Phase1WriteRequest(
    SourceFile File,
    Module? Module,
    IReadOnlyList<Chunk> Chunks,
    IReadOnlyDictionary<string, SymbolSummary> Summaries,
    IReadOnlyList<UnresolvedCallSite> UnresolvedCallSites);
