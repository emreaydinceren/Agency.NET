namespace Agency.GraphRAG.Code.Chunker;

/// <summary>
/// Represents a source range for a chunk.
/// </summary>
/// <param name="StartLine">The zero-based start line.</param>
/// <param name="StartColumn">The zero-based start column.</param>
/// <param name="EndLine">The zero-based end line.</param>
/// <param name="EndColumn">The zero-based end column.</param>
public sealed record ChunkSourceRange(int StartLine, int StartColumn, int EndLine, int EndColumn);
