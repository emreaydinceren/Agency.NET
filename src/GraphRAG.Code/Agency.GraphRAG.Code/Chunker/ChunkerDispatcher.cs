using Agency.GraphRAG.Code.Walker;

namespace Agency.GraphRAG.Code.Chunker;

/// <summary>
/// Dispatches chunking requests to the language-specific chunker registered for the input language.
/// </summary>
public sealed class ChunkerDispatcher : IChunker
{
    private readonly IReadOnlyDictionary<Language, IChunker> _chunkers;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChunkerDispatcher"/> class.
    /// </summary>
    /// <param name="chunkers">The chunkers keyed by supported language.</param>
    public ChunkerDispatcher(IReadOnlyDictionary<Language, IChunker> chunkers)
    {
        ArgumentNullException.ThrowIfNull(chunkers);
        _chunkers = chunkers;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Chunk>> ChunkAsync(ChunkerInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (!_chunkers.TryGetValue(input.Language, out IChunker? chunker))
        {
            throw new NotSupportedException($"No chunker is registered for language '{input.Language}'.");
        }

        return chunker.ChunkAsync(input, cancellationToken);
    }
}
