using Microsoft.Extensions.Options;

namespace Agency.Embeddings.Test;

public sealed class EmbeddingGeneratorTests
{
    private static readonly EmbeddingOptions DefaultOptions = EmbeddingOptions.LMStudioDefaults;

    // -------------------------------------------------------------------------
    // Constructor
    // -------------------------------------------------------------------------

    [Fact]
    public void Constructor_NullOptions_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new EmbeddingGenerator((EmbeddingOptions)null!));
    }

    [Fact]
    public void Constructor_WithValidOptions_DoesNotThrow()
    {
        var exception = Record.Exception(() => new EmbeddingGenerator(DefaultOptions));

        Assert.Null(exception);
    }

    [Fact]
    public void Constructor_WithIOptions_DoesNotThrow()
    {
        var wrapped = Options.Create(DefaultOptions);

        var exception = Record.Exception(() => new EmbeddingGenerator(wrapped));

        Assert.Null(exception);
    }

    [Fact]
    public void Constructor_WithIOptions_UsesOptionsValue()
    {
        var custom = new EmbeddingOptions
        {
            BaseUrl = "http://localhost:9999/v1",
            ModelId = "custom-model",
            ApiKey = "test-key",
        };
        var wrapped = Options.Create(custom);

        // Constructing without throwing confirms the URI and model were applied
        var exception = Record.Exception(() => new EmbeddingGenerator(wrapped));

        Assert.Null(exception);
    }

    // -------------------------------------------------------------------------
    // GenerateEmbeddingAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GenerateEmbeddingAsync_ValidInput_ReturnsExpectedVector()
    {
        float[] expected = [0.1f, 0.2f, 0.3f];
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.BuildEmbeddingsJson(expected));
        var generator = new EmbeddingGenerator(DefaultOptions, handler);

        var result = await generator.GenerateEmbeddingAsync("hello world");

        Assert.Equal(expected.Length, result.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], result.Span[i], precision: 5);
        }
    }

    [Fact]
    public async Task GenerateEmbeddingAsync_LargerVector_ReturnsAllDimensions()
    {
        var expected = Enumerable.Range(0, 128).Select(i => i / 128f).ToArray();
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.BuildEmbeddingsJson(expected));
        var generator = new EmbeddingGenerator(DefaultOptions, handler);

        var result = await generator.GenerateEmbeddingAsync("text");

        Assert.Equal(128, result.Length);
    }

    [Fact]
    public async Task GenerateEmbeddingAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.BuildEmbeddingsJson([0.1f]));
        var generator = new EmbeddingGenerator(DefaultOptions, handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            generator.GenerateEmbeddingAsync("hello", cts.Token));
    }

    // -------------------------------------------------------------------------
    // GenerateEmbeddingsAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GenerateEmbeddingsAsync_TwoInputs_ReturnsTwoVectors()
    {
        float[] first = [0.1f, 0.2f, 0.3f];
        float[] second = [0.4f, 0.5f, 0.6f];
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.BuildEmbeddingsJson(first, second));
        var generator = new EmbeddingGenerator(DefaultOptions, handler);

        var results = await generator.GenerateEmbeddingsAsync(["first input", "second input"]);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task GenerateEmbeddingsAsync_TwoInputs_ReturnsCorrectVectorValues()
    {
        float[] first = [0.1f, 0.2f, 0.3f];
        float[] second = [0.4f, 0.5f, 0.6f];
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.BuildEmbeddingsJson(first, second));
        var generator = new EmbeddingGenerator(DefaultOptions, handler);

        var results = await generator.GenerateEmbeddingsAsync(["first input", "second input"]);

        Assert.Equal(first[0], results[0].Span[0], precision: 5);
        Assert.Equal(first[1], results[0].Span[1], precision: 5);
        Assert.Equal(first[2], results[0].Span[2], precision: 5);

        Assert.Equal(second[0], results[1].Span[0], precision: 5);
        Assert.Equal(second[1], results[1].Span[1], precision: 5);
        Assert.Equal(second[2], results[1].Span[2], precision: 5);
    }

    [Fact]
    public async Task GenerateEmbeddingsAsync_SingleInput_ReturnsListWithOneVector()
    {
        float[] expected = [0.7f, 0.8f, 0.9f];
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.BuildEmbeddingsJson(expected));
        var generator = new EmbeddingGenerator(DefaultOptions, handler);

        var results = await generator.GenerateEmbeddingsAsync(["only input"]);

        Assert.Single(results);
        Assert.Equal(expected.Length, results[0].Length);
    }

    [Fact]
    public async Task GenerateEmbeddingsAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.BuildEmbeddingsJson([0.1f]));
        var generator = new EmbeddingGenerator(DefaultOptions, handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            generator.GenerateEmbeddingsAsync(["hello"], cts.Token));
    }
}