using Agency.Embeddings.Common;
using System.Text;

namespace Agency.GraphRAG.Code.Sqlite.Test;

/// <summary>
/// Deterministic embedding generator used by SQLite graph-store tests.
/// </summary>
public sealed class FakeEmbeddingGenerator : IEmbeddingGenerator
{
    /// <summary>
    /// Gets the embedding width produced by this generator.
    /// </summary>
    public const int Dimensions = 8;

    /// <inheritdoc />
    public Task<ReadOnlyMemory<float>> GenerateEmbeddingAsync(string input, CancellationToken cancellationToken = default)
        => Task.FromResult(new ReadOnlyMemory<float>(CreateVector(input)));

    /// <inheritdoc />
    public Task<IReadOnlyList<ReadOnlyMemory<float>>> GenerateEmbeddingsAsync(
        IEnumerable<string> inputs,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ReadOnlyMemory<float>> vectors = inputs
            .Select(input => new ReadOnlyMemory<float>(CreateVector(input)))
            .ToArray();

        return Task.FromResult(vectors);
    }

    /// <summary>
    /// Creates the deterministic vector that corresponds to the supplied text.
    /// </summary>
    /// <param name="input">The text to encode.</param>
    /// <returns>The generated vector.</returns>
    public static float[] CreateVector(string input)
    {
        ArgumentNullException.ThrowIfNull(input);

        byte[] bytes = Encoding.UTF8.GetBytes(input);
        if (bytes.Length == 0)
        {
            return new float[Dimensions];
        }

        float[] vector = new float[Dimensions];
        for (int index = 0; index < Dimensions; index++)
        {
            int value = bytes[index % bytes.Length];
            vector[index] = value / 255f;
        }

        return vector;
    }
}
