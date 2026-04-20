namespace Agency.Embeddings.Common;

/// <summary>
/// Generates embedding vectors for one or more input strings.
/// </summary>
public interface IEmbeddingGenerator
{
    /// <summary>
    /// Generates an embedding vector for a single input string.
    /// </summary>
    Task<ReadOnlyMemory<float>> GenerateEmbeddingAsync(string input, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates embedding vectors for a batch of input strings.
    /// </summary>
    Task<IReadOnlyList<ReadOnlyMemory<float>>> GenerateEmbeddingsAsync(IEnumerable<string> inputs, CancellationToken cancellationToken = default);
}
