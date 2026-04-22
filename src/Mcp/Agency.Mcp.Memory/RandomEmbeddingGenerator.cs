using Agency.Embeddings.Common;

namespace Agency.Mcp.Memory;

/// <summary>
/// A deterministic embedding generator that produces pseudo-random vectors seeded from the input hash.
/// Used when no real embedding provider is configured, so the server remains functional for metadata-filtered recall.
/// </summary>
internal sealed class RandomEmbeddingGenerator : IEmbeddingGenerator
{
    private const int Dimensions = 1536;

    /// <inheritdoc/>
    public Task<ReadOnlyMemory<float>> GenerateEmbeddingAsync(string input, CancellationToken cancellationToken = default)
    {
        int seed = string.IsNullOrEmpty(input) ? 0 : input.GetHashCode();
        var rng = new Random(seed);
        float[] embedding = new float[Dimensions];
        for (int i = 0; i < embedding.Length; i++)
        {
            embedding[i] = (float)rng.NextDouble();
        }

        return Task.FromResult((ReadOnlyMemory<float>)embedding.AsMemory());
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ReadOnlyMemory<float>>> GenerateEmbeddingsAsync(IEnumerable<string> inputs, CancellationToken cancellationToken = default)
    {
        var results = new List<ReadOnlyMemory<float>>();
        foreach (string input in inputs)
        {
            results.Add(await this.GenerateEmbeddingAsync(input, cancellationToken));
        }

        return results;
    }
}
