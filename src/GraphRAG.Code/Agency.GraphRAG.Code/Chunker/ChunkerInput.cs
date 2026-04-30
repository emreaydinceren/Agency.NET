using Agency.GraphRAG.Code.Walker;

namespace Agency.GraphRAG.Code.Chunker;

/// <summary>
/// Provides the source file input for chunk generation.
/// </summary>
/// <param name="Path">The repository-relative file path.</param>
/// <param name="Language">The source language.</param>
/// <param name="Source">The file contents.</param>
public sealed record ChunkerInput(string Path, Language Language, string Source);
