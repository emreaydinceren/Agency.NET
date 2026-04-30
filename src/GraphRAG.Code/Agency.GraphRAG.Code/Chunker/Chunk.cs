using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Walker;

namespace Agency.GraphRAG.Code.Chunker;

/// <summary>
/// Represents a semantic source chunk.
/// </summary>
/// <param name="Id">The stable chunk identifier.</param>
/// <param name="Path">The repository-relative file path.</param>
/// <param name="Language">The source language.</param>
/// <param name="Granularity">The chunk granularity.</param>
/// <param name="Name">The simple symbol or statement name.</param>
/// <param name="FullyQualifiedName">The fully-qualified symbol name.</param>
/// <param name="Signature">The captured signature.</param>
/// <param name="Content">The raw source text.</param>
/// <param name="Range">The source range.</param>
/// <param name="SymbolKind">The symbol kind when the chunk represents a symbol.</param>
/// <param name="ImportsInScope">The imports visible to the chunk.</param>
/// <param name="ParentId">The parent chunk identifier when present.</param>
/// <param name="Inherits">Base types for ordering.</param>
/// <param name="Implements">Implemented interfaces for ordering.</param>
public sealed record Chunk(
    string Id,
    string Path,
    Language Language,
    ChunkGranularity Granularity,
    string Name,
    string FullyQualifiedName,
    string? Signature,
    string Content,
    ChunkSourceRange Range,
    SymbolKind SymbolKind,
    IReadOnlyList<ImportReference> ImportsInScope,
    string? ParentId = null,
    IReadOnlyList<string>? Inherits = null,
    IReadOnlyList<string>? Implements = null);
