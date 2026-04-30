namespace Agency.GraphRAG.Code.Domain;

/// <summary>
/// Represents a named code symbol (class, method, field, etc.) extracted from a source file.
/// </summary>
public record class Symbol
{
    /// <summary>Gets the unique identifier for this symbol.</summary>
    public required Guid Id { get; init; }

    /// <summary>Gets the identifier of the file that contains this symbol.</summary>
    public required Guid FileId { get; init; }

    /// <summary>Gets the identifier of the module that contains this symbol, or <c>null</c> for top-level symbols.</summary>
    public Guid? ModuleId { get; init; }

    /// <summary>Gets the simple (unqualified) name of the symbol.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the fully qualified name of the symbol, or <c>null</c> if not resolved.</summary>
    public string? FullyQualifiedName { get; init; }

    /// <summary>Gets the kind of symbol (e.g. class, method, property).</summary>
    public required SymbolKind Kind { get; init; }

    /// <summary>Gets the type signature of the symbol, or <c>null</c> if not applicable.</summary>
    public string? Signature { get; init; }

    /// <summary>Gets a longer documentation summary, or <c>null</c> if unavailable.</summary>
    public string? Summary { get; init; }

    /// <summary>Gets a one-line summary suitable for embedding context, or <c>null</c> if unavailable.</summary>
    public string? OneLineSummary { get; init; }

    /// <summary>Gets the hash of the symbol's source content, or <c>null</c> if not computed.</summary>
    public string? ContentHash { get; init; }

    /// <summary>Gets the semantic embedding vector for this symbol, or <c>null</c> if not computed.</summary>
    public float[]? Embedding { get; init; }

    /// <summary>Gets a value indicating whether this symbol is classified as a utility (non-core-business) symbol.</summary>
    public required bool IsUtility { get; init; }

    /// <summary>Gets the start line (or offset) of the symbol's source range, inclusive.</summary>
    public required int SourceRangeStart { get; init; }

    /// <summary>Gets the end line (or offset) of the symbol's source range, inclusive.</summary>
    public required int SourceRangeEnd { get; init; }
}
