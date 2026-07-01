using Microsoft.Extensions.Configuration;

namespace Agency.Embeddings.OpenAI.Test;

/// <summary>
/// Functional tests for <see cref="Agency.Embeddings.OpenAI.EmbeddingGenerator"/> that call the real LM
/// Studio server configured in <c>appsettings.json</c>.
/// Run with: dotnet test --filter "Category=Functional"
/// Skip with: dotnet test --filter "Category!=Functional"
/// Requires LM Studio running with text-embedding-qwen3-embedding-0.6b loaded. Configure the endpoint in appsettings.json.
/// </summary>
[Trait("Category", "Functional")]
public sealed class EmbeddingGeneratorFunctionalTests
{
    private static readonly EmbeddingGenerator Generator = CreateGenerator();

    private static EmbeddingGenerator CreateGenerator()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddSharedConfiguration("shared-test-appsettings.json")
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddUserSecrets<EmbeddingGeneratorFunctionalTests>(optional: true)
            .AddEnvironmentVariables()
            .AddPlaceholderResolver()
            .Build();

        var options = new EmbeddingOptions
        {
            BaseUrl = configuration[$"{EmbeddingOptions.SectionName}:BaseUrl"],
            ModelId = configuration[$"{EmbeddingOptions.SectionName}:ModelId"],
            ApiKey = configuration[$"{EmbeddingOptions.SectionName}:ApiKey"],
        };

        return new EmbeddingGenerator(options);
    }

    // -------------------------------------------------------------------------
    // Different texts produce different vectors
    // -------------------------------------------------------------------------

    /// <summary>
    /// Verifies that unrelated texts produce different embedding vectors.
    /// </summary>
    [Fact]
    public async Task DifferentTexts_ReturnDifferentEmbeddings()
    {
        var a = await Generator.GenerateEmbeddingAsync("the quick brown fox", TestContext.Current.CancellationToken);
        var b = await Generator.GenerateEmbeddingAsync("SELECT * FROM users WHERE id = 1", TestContext.Current.CancellationToken);

        Assert.False(VectorsAreEqual(a, b), "Unrelated texts should not produce identical embedding vectors.");
    }

    /// <summary>
    /// Verifies that batch and single calls produce different embeddings for unrelated texts.
    /// </summary>
    [Fact]
    public async Task DifferentTexts_BatchAndSingle_ReturnDifferentEmbeddings()
    {
        var results = await Generator.GenerateEmbeddingsAsync(["sunrise over the mountains", "database migration script"], TestContext.Current.CancellationToken);

        Assert.False(VectorsAreEqual(results[0], results[1]),
            "Two unrelated texts in a batch should produce different embedding vectors.");
    }

    // -------------------------------------------------------------------------
    // Same text reproduces the same vector
    // -------------------------------------------------------------------------

    /// <summary>
    /// Verifies that identical text produces equivalent embeddings across calls.
    /// </summary>
    [Fact]
    public async Task SameText_CalledTwice_ReturnsEquivalentEmbeddings()
    {
        const string input = "embeddings should be deterministic";

        var first = await Generator.GenerateEmbeddingAsync(input, TestContext.Current.CancellationToken);
        var second = await Generator.GenerateEmbeddingAsync(input, TestContext.Current.CancellationToken);

        Assert.Equal(first.Length, second.Length);
        var similarity = CosineSimilarity(first, second);
        Assert.True(similarity > 0.9999f, $"Same text twice should yield near-identical vectors (got cosine={similarity:F6}).");
    }

    // -------------------------------------------------------------------------
    // Semantically similar texts are closer together than dissimilar ones
    // -------------------------------------------------------------------------

    /// <summary>
    /// Verifies that semantically similar texts are closer than dissimilar texts.
    /// </summary>
    [Fact]
    public async Task SemanticallySimilarTexts_HigherSimilarityThanDissimilarTexts()
    {
        var dog = await Generator.GenerateEmbeddingAsync("dog", TestContext.Current.CancellationToken);
        var puppy = await Generator.GenerateEmbeddingAsync("puppy", TestContext.Current.CancellationToken);
        var spaceship = await Generator.GenerateEmbeddingAsync("spaceship", TestContext.Current.CancellationToken);

        var similarPairScore = CosineSimilarity(dog, puppy);
        var dissimilarPairScore = CosineSimilarity(dog, spaceship);

        Assert.True(
            similarPairScore > dissimilarPairScore,
            $"'dog'↔'puppy' similarity ({similarPairScore:F4}) should exceed 'dog'↔'spaceship' ({dissimilarPairScore:F4}).");
    }

    /// <summary>
    /// Verifies that semantically similar sentences are closer than unrelated sentences.
    /// </summary>
    [Fact]
    public async Task SemanticallySimilarSentences_HigherSimilarityThanUnrelatedSentences()
    {
        var programming = await Generator.GenerateEmbeddingAsync("writing clean code in C#", TestContext.Current.CancellationToken);
        var refactoring = await Generator.GenerateEmbeddingAsync("refactoring software for maintainability", TestContext.Current.CancellationToken);
        var cooking = await Generator.GenerateEmbeddingAsync("how to bake a sourdough loaf", TestContext.Current.CancellationToken);

        var relatedScore = CosineSimilarity(programming, refactoring);
        var unrelatedScore = CosineSimilarity(programming, cooking);

        Assert.True(
            relatedScore > unrelatedScore,
            $"Related sentences ({relatedScore:F4}) should be more similar than unrelated ones ({unrelatedScore:F4}).");
    }

    // -------------------------------------------------------------------------
    // Batch results are consistent with individual calls
    // -------------------------------------------------------------------------

    /// <summary>
    /// Verifies that batch embeddings match individual embeddings.
    /// </summary>
    [Fact]
    public async Task BatchEmbeddings_MatchIndividualEmbeddings()
    {
        string[] inputs = ["neural network", "machine learning", "deep learning"];

        var batch = await Generator.GenerateEmbeddingsAsync(inputs, TestContext.Current.CancellationToken);

        var individual = new List<ReadOnlyMemory<float>>();
        foreach (var input in inputs)
        {
            individual.Add(await Generator.GenerateEmbeddingAsync(input, TestContext.Current.CancellationToken));
        }

        Assert.Equal(batch.Count, individual.Count);
        for (var i = 0; i < batch.Count; i++)
        {
            var similarity = CosineSimilarity(batch[i], individual[i]);
            Assert.True(similarity > 0.9999f,
                $"Batch[{i}] and individual[{i}] should be near-identical (got cosine={similarity:F6}).");
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static float CosineSimilarity(ReadOnlyMemory<float> a, ReadOnlyMemory<float> b)
    {
        var spanA = a.Span;
        var spanB = b.Span;

        Assert.Equal(spanA.Length, spanB.Length);

        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < spanA.Length; i++)
        {
            dot += spanA[i] * spanB[i];
            normA += spanA[i] * spanA[i];
            normB += spanB[i] * spanB[i];
        }

        return (float)(dot / (Math.Sqrt(normA) * Math.Sqrt(normB)));
    }

    private static bool VectorsAreEqual(ReadOnlyMemory<float> a, ReadOnlyMemory<float> b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        return a.Span.SequenceEqual(b.Span);
    }
}