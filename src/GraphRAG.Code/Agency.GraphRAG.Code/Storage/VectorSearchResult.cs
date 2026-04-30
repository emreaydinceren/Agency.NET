namespace Agency.GraphRAG.Code.Storage;

/// <summary>
/// A single result from a vector similarity search against symbols or clusters.
/// </summary>
public sealed record VectorSearchResult
{
    /// <summary>Gets the symbol or cluster ID returned by the search.</summary>
    public required Guid Id { get; init; }

    /// <summary>Gets the similarity score in [0, 1] where 1 = identical.</summary>
    public required double Score { get; init; }
}