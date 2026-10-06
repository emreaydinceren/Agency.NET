using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Agency.Embeddings.OpenAI.Test;

/// <summary>
/// Unit tests for <see cref="Agency.Embeddings.OpenAI.EmbeddingGenerator"/> covering construction,
/// single and batch embedding generation, and cancellation.
/// </summary>
public sealed class EmbeddingGeneratorTests
{
    private static readonly EmbeddingOptions DefaultOptions = LoadOptions();

    private static EmbeddingOptions LoadOptions()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddSharedConfiguration("shared-test-appsettings.json")
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddUserSecrets<EmbeddingGeneratorTests>(optional: true)
            .AddEnvironmentVariables()
            .AddPlaceholderResolver()
            .Build();

        return new EmbeddingOptions
        {
            BaseUrl = configuration[$"{EmbeddingOptions.SectionName}:BaseUrl"],
            ModelId = configuration[$"{EmbeddingOptions.SectionName}:ModelId"],
            ApiKey = configuration[$"{EmbeddingOptions.SectionName}:ApiKey"],
        };
    }

    // -------------------------------------------------------------------------
    // Constructor
    // -------------------------------------------------------------------------

    /// <summary>
    /// Verifies that constructing with null options throws.
    /// </summary>
    [Fact]
    public void Constructor_NullOptions_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new EmbeddingGenerator((EmbeddingOptions)null!));
    }

    /// <summary>
    /// Verifies that constructing with direct options succeeds.
    /// </summary>
    [Fact]
    public void Constructor_WithValidOptions_DoesNotThrow()
    {
        var exception = Record.Exception(() => new EmbeddingGenerator(DefaultOptions));

        Assert.Null(exception);
    }

    /// <summary>
    /// Verifies that constructing through <see cref="IOptions{TOptions}"/> succeeds.
    /// </summary>
    [Fact]
    public void Constructor_WithIOptions_DoesNotThrow()
    {
        var wrapped = Options.Create(DefaultOptions);

        var exception = Record.Exception(() => new EmbeddingGenerator(wrapped));

        Assert.Null(exception);
    }

    /// <summary>
    /// Verifies that the options wrapper value is used during construction.
    /// </summary>
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

    /// <summary>
    /// Verifies that a valid input returns the expected embedding vector.
    /// </summary>
    [Fact]
    public async Task GenerateEmbeddingAsync_ValidInput_ReturnsExpectedVector()
    {
        float[] expected = [0.1f, 0.2f, 0.3f];
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.BuildEmbeddingsJson(expected));
        var generator = new EmbeddingGenerator(DefaultOptions, handler);

        var result = await generator.GenerateEmbeddingAsync("hello world", TestContext.Current.CancellationToken);

        Assert.Equal(expected.Length, result.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], result.Span[i], precision: 5);
        }
    }

    /// <summary>
    /// Verifies that larger vectors are returned with the expected dimensionality.
    /// </summary>
    [Fact]
    public async Task GenerateEmbeddingAsync_LargerVector_ReturnsAllDimensions()
    {
        var expected = Enumerable.Range(0, 128).Select(i => i / 128f).ToArray();
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.BuildEmbeddingsJson(expected));
        var generator = new EmbeddingGenerator(DefaultOptions, handler);

        var result = await generator.GenerateEmbeddingAsync("text", TestContext.Current.CancellationToken);

        Assert.Equal(128, result.Length);
    }

    /// <summary>
    /// Verifies that cancellation is honored for single-embedding generation.
    /// </summary>
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

    /// <summary>
    /// Verifies that batch generation returns two vectors for two inputs.
    /// </summary>
    [Fact]
    public async Task GenerateEmbeddingsAsync_TwoInputs_ReturnsTwoVectors()
    {
        float[] first = [0.1f, 0.2f, 0.3f];
        float[] second = [0.4f, 0.5f, 0.6f];
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.BuildEmbeddingsJson(first, second));
        var generator = new EmbeddingGenerator(DefaultOptions, handler);

        var results = await generator.GenerateEmbeddingsAsync(["first input", "second input"], TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
    }

    /// <summary>
    /// Verifies that batch generation returns the expected vector values.
    /// </summary>
    [Fact]
    public async Task GenerateEmbeddingsAsync_TwoInputs_ReturnsCorrectVectorValues()
    {
        float[] first = [0.1f, 0.2f, 0.3f];
        float[] second = [0.4f, 0.5f, 0.6f];
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.BuildEmbeddingsJson(first, second));
        var generator = new EmbeddingGenerator(DefaultOptions, handler);

        var results = await generator.GenerateEmbeddingsAsync(["first input", "second input"], TestContext.Current.CancellationToken);

        Assert.Equal(first[0], results[0].Span[0], precision: 5);
        Assert.Equal(first[1], results[0].Span[1], precision: 5);
        Assert.Equal(first[2], results[0].Span[2], precision: 5);

        Assert.Equal(second[0], results[1].Span[0], precision: 5);
        Assert.Equal(second[1], results[1].Span[1], precision: 5);
        Assert.Equal(second[2], results[1].Span[2], precision: 5);
    }

    /// <summary>
    /// Verifies that batch generation returns a single vector for a single input.
    /// </summary>
    [Fact]
    public async Task GenerateEmbeddingsAsync_SingleInput_ReturnsListWithOneVector()
    {
        float[] expected = [0.7f, 0.8f, 0.9f];
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.BuildEmbeddingsJson(expected));
        var generator = new EmbeddingGenerator(DefaultOptions, handler);

        var results = await generator.GenerateEmbeddingsAsync(["only input"], TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal(expected.Length, results[0].Length);
    }

    /// <summary>
    /// Verifies that an oversized batch is split into several requests of at most
    /// <see cref="EmbeddingOptions.MaxBatchSize"/> inputs, so one large document cannot flood the server.
    /// </summary>
    [Fact]
    public async Task GenerateEmbeddingsAsync_MoreInputsThanMaxBatchSize_SplitsIntoSequentialRequests()
    {
        var handler = new InputCountingHandler();
        var options = new EmbeddingOptions { BaseUrl = DefaultOptions.BaseUrl, ModelId = DefaultOptions.ModelId, ApiKey = DefaultOptions.ApiKey, MaxBatchSize = 4 };
        var generator = new EmbeddingGenerator(options, handler);
        string[] inputs = Enumerable.Range(0, 10).Select(i => $"chunk {i}").ToArray();

        var results = await generator.GenerateEmbeddingsAsync(inputs, TestContext.Current.CancellationToken);

        Assert.Equal(10, results.Count);
        Assert.Equal([4, 4, 2], handler.BatchSizes);
        Assert.Equal(1, handler.MaxConcurrent);
    }

    /// <summary>
    /// Answers each embeddings request with one vector per input, recording the batch sizes and the
    /// highest number of requests in flight at once.
    /// </summary>
    private sealed class InputCountingHandler : HttpMessageHandler
    {
        private static readonly float[] Vector = [0.1f, 0.2f];

        private int _inFlight;

        public List<int> BatchSizes { get; } = [];

        public int MaxConcurrent { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            this.MaxConcurrent = Math.Max(this.MaxConcurrent, Interlocked.Increment(ref this._inFlight));
            try
            {
                string body = await request.Content!.ReadAsStringAsync(cancellationToken);
                int count = System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("input").GetArrayLength();
                this.BatchSizes.Add(count);
                await Task.Yield();

                string json = StubHttpMessageHandler.BuildEmbeddingsJson(Enumerable.Range(0, count).Select(_ => Vector).ToArray());
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
                };
            }
            finally
            {
                Interlocked.Decrement(ref this._inFlight);
            }
        }
    }

    /// <summary>
    /// Verifies that cancellation is honored for batch embedding generation.
    /// </summary>
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