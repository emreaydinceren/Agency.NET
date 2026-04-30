namespace Agency.GraphRAG.Code.Domain;

/// <summary>
/// Represents a logical grouping of symbols within a file, such as a namespace or class body.
/// </summary>
public record class Module
{
    /// <summary>Gets the unique identifier for this module.</summary>
    public required Guid Id { get; init; }

    /// <summary>Gets the identifier of the file that contains this module.</summary>
    public required Guid FileId { get; init; }

    /// <summary>Gets the name of the module (e.g. a namespace or class name).</summary>
    public required string Name { get; init; }

    /// <summary>Gets the kind of module (e.g. "namespace", "class").</summary>
    public required string Kind { get; init; }
}
