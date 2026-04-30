namespace Agency.GraphRAG.Code.Chunker;

/// <summary>
/// Chunks parsed source files into embedding-sized semantic blocks.
/// </summary>
public interface IChunker
{
    /// <summary>
    /// Produces semantic chunks for the provided parsed file.
    /// </summary>
    /// <param name="input">The chunker input.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The emitted chunks.</returns>
    Task<IReadOnlyList<Chunk>> ChunkAsync(ChunkerInput input, CancellationToken cancellationToken = default);
}
