namespace Agency.GraphRAG.Code.Chunker;

/// <summary>
/// Describes an import that is in scope for a chunk.
/// </summary>
/// <param name="Source">The imported module, namespace, or package specifier.</param>
/// <param name="Symbols">The imported identifiers when known.</param>
/// <param name="IsRelative">A value indicating whether <paramref name="Source"/> is a relative path.</param>
/// <param name="Alias">The alias applied to the import when present.</param>
public sealed record ImportReference(string Source, IReadOnlyList<string> Symbols, bool IsRelative, string? Alias = null);
