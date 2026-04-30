namespace Agency.GraphRAG.Code.Query;

/// <summary>
/// The final output from the query pipeline.
/// </summary>
public sealed record QueryResponse
{
    /// <summary>Gets the synthesized answer text.</summary>
    public required string Answer { get; init; }

    /// <summary>Gets the query plan that drove retrieval.</summary>
    public required QueryPlan Plan { get; init; }

    /// <summary>Gets the assembled context used for synthesis.</summary>
    public required QueryContextAssembly Context { get; init; }
}
