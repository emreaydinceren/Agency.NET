namespace Agency.GraphRAG.Code.Domain;

/// <summary>
/// Represents a call site in source code whose target symbol could not be resolved
/// to a known indexed <see cref="Symbol"/> during static analysis.
/// </summary>
public record class UnresolvedCallSite
{
    /// <summary>Gets the unique identifier for this call site record.</summary>
    public required Guid Id { get; init; }

    /// <summary>Gets the identifier of the symbol that contains this call site.</summary>
    public required Guid SourceSymbolId { get; init; }

    /// <summary>Gets the identifier of the file that contains this call site.</summary>
    public required Guid SourceFileId { get; init; }

    /// <summary>Gets the identifier text as it appears at the call site.</summary>
    public required string Identifier { get; init; }

    /// <summary>Gets the enclosing scope or namespace context of the call site, or <c>null</c> if unknown.</summary>
    public string? Scope { get; init; }

    /// <summary>Gets the fully qualified target name as inferred by an LLM, or <c>null</c> if not available.</summary>
    public string? LlmExtractedTarget { get; init; }
}
