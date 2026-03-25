using Agency.Common;

namespace Agency.SQL.Test;

/// <summary>
/// Tests for <see cref="Agency.SQL.SQLQueryEmbedder"/>.
/// </summary>
public sealed class SQLQueryEmbedderTests
{
    /// <summary>
    /// Verifies that constructing with a null embedding generator throws.
    /// </summary>
    [Fact]
    public void Constructor_NullEmbeddingGenerator_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new SQLQueryEmbedder(null!));
    }

    /// <summary>
    /// Verifies that blank SQL input is rejected.
    /// </summary>
    [Fact]
    public async Task EmbedVectorsInQueryAsync_NullOrWhitespace_ThrowsArgumentException()
    {
        var embedder = new SQLQueryEmbedder(new StubEmbeddingGenerator());

        await Assert.ThrowsAsync<ArgumentException>(() => embedder.EmbedVectorsInQueryAsync(null!));
        await Assert.ThrowsAsync<ArgumentException>(() => embedder.EmbedVectorsInQueryAsync("   "));
    }

    /// <summary>
    /// Verifies that SQL without vectorize calls is returned unchanged.
    /// </summary>
    [Fact]
    public async Task EmbedVectorsInQueryAsync_NoVectorizeCall_ReturnsOriginalQuery()
    {
        var generator = new StubEmbeddingGenerator();
        var embedder = new SQLQueryEmbedder(generator);
        const string sql = "SELECT * FROM docs WHERE id = 1";

        var result = await embedder.EmbedVectorsInQueryAsync(sql);

        Assert.Equal(sql, result);
        Assert.Empty(generator.CapturedInputs);
    }

    /// <summary>
    /// Verifies that a single vectorize call is replaced with a vector literal.
    /// </summary>
    [Fact]
    public async Task EmbedVectorsInQueryAsync_SingleVectorize_ReplacesWithVectorLiteral()
    {
        var generator = new StubEmbeddingGenerator();
        generator.Register("hello", [0.1f, -0.2f, 1.5f]);

        var embedder = new SQLQueryEmbedder(generator);
        const string sql = "SELECT * FROM docs WHERE 1 - (embedding <=> vectorize('hello')::vector) < 0.5";

        var result = await embedder.EmbedVectorsInQueryAsync(sql);

        Assert.Equal("SELECT * FROM docs WHERE 1 - (embedding <=> '[0.1,-0.2,1.5]'::vector) < 0.5", result);
        Assert.Single(generator.CapturedInputs);
        Assert.Equal("hello", generator.CapturedInputs[0]);
    }

    /// <summary>
    /// Verifies that escaped quotes are unescaped before embedding generation.
    /// </summary>
    [Fact]
    public async Task EmbedVectorsInQueryAsync_EscapedQuoteInput_UnescapesBeforeGeneration()
    {
        var generator = new StubEmbeddingGenerator();
        generator.Register("O'Reilly", [1f]);

        var embedder = new SQLQueryEmbedder(generator);
        const string sql = "SELECT vectorize('O''Reilly')::vector";

        var result = await embedder.EmbedVectorsInQueryAsync(sql);

        Assert.Equal("SELECT '[1]'::vector", result);
        Assert.Single(generator.CapturedInputs);
        Assert.Equal("O'Reilly", generator.CapturedInputs[0]);
    }

    /// <summary>
    /// Verifies that multiple vectorize calls are all replaced.
    /// </summary>
    [Fact]
    public async Task EmbedVectorsInQueryAsync_MultipleVectorizeCalls_ReplacesAllMatches()
    {
        var generator = new StubEmbeddingGenerator();
        generator.Register("first", [1f, 2f]);
        generator.Register("second", [3f, 4f]);

        var embedder = new SQLQueryEmbedder(generator);
        const string sql = "SELECT vectorize('first')::vector AS a, VECTORIZE('second')::vector AS b";

        var result = await embedder.EmbedVectorsInQueryAsync(sql);

        Assert.Equal("SELECT '[1,2]'::vector AS a, '[3,4]'::vector AS b", result);
        Assert.Equal(2, generator.CapturedInputs.Count);
        Assert.Equal("second", generator.CapturedInputs[0]);
        Assert.Equal("first", generator.CapturedInputs[1]);
    }

    /// <summary>
    /// Verifies that the cancellation token is forwarded to the embedding generator.
    /// </summary>
    [Fact]
    public async Task EmbedVectorsInQueryAsync_PassesCancellationTokenToGenerator()
    {
        var generator = new StubEmbeddingGenerator();
        generator.Register("token", [9f]);

        var embedder = new SQLQueryEmbedder(generator);
        using var cts = new CancellationTokenSource();

        _ = await embedder.EmbedVectorsInQueryAsync("SELECT vectorize('token')::vector", cts.Token);

        Assert.True(generator.CapturedTokens[0].CanBeCanceled);
    }

    private sealed class StubEmbeddingGenerator : IEmbeddingGenerator
    {
        private readonly Dictionary<string, ReadOnlyMemory<float>> _embeddings = [];

        public List<string> CapturedInputs { get; } = [];

        public List<CancellationToken> CapturedTokens { get; } = [];

        public void Register(string input, float[] vector)
        {
            _embeddings[input] = vector;
        }

        public Task<ReadOnlyMemory<float>> GenerateEmbeddingAsync(string input, CancellationToken cancellationToken = default)
        {
            CapturedInputs.Add(input);
            CapturedTokens.Add(cancellationToken);

            if (_embeddings.TryGetValue(input, out var embedding))
            {
                return Task.FromResult(embedding);
            }

            throw new InvalidOperationException($"No embedding registered for '{input}'.");
        }

        public Task<IReadOnlyList<ReadOnlyMemory<float>>> GenerateEmbeddingsAsync(IEnumerable<string> inputs, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
