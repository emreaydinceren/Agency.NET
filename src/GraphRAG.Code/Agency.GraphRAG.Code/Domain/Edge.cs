namespace Agency.GraphRAG.Code.Domain;

/// <summary>
/// Represents a directed relationship between two nodes in the code graph.
/// </summary>
public record class Edge
{
    /// <summary>Gets the unique identifier for this edge.</summary>
    public required Guid Id { get; init; }

    /// <summary>Gets the identifier of the source node.</summary>
    public required Guid SourceId { get; init; }

    /// <summary>Gets the entity kind of the source node (e.g. "symbol", "file", "module").</summary>
    public required string SourceKind { get; init; }

    /// <summary>Gets the identifier of the target node.</summary>
    public required Guid TargetId { get; init; }

    /// <summary>Gets the entity kind of the target node (e.g. "symbol", "file", "module").</summary>
    public required string TargetKind { get; init; }

    /// <summary>Gets the semantic kind of this edge.</summary>
    public required EdgeKind EdgeKind { get; init; }

    /// <summary>Gets the confidence score for this edge in the range [0.0, 1.0].</summary>
    public required double Confidence { get; init; }

    /// <summary>Gets the list of evidence signals that contributed to this edge.</summary>
    public required IReadOnlyList<Signal> Signals { get; init; }

    /// <summary>Gets an arbitrary property bag for additional edge metadata.</summary>
    public required IReadOnlyDictionary<string, object?> Properties { get; init; }

    /// <summary>
    /// Gets the membership kind for a <see cref="EdgeKind.MemberOf"/> edge (e.g. "primary" or "utility"),
    /// read from the <c>"kind"</c> entry in <see cref="Properties"/>. Returns <c>null</c> if absent.
    /// </summary>
    public string? MemberKind =>
        this.Properties.TryGetValue("kind", out object? value) ? value as string : null;
}
