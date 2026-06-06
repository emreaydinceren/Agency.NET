namespace Agency.Embeddings.Common.Test;

public sealed class BatchingEmbeddingGeneratorTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Fake that tracks every call made to <see cref="IEmbeddingGenerator.GenerateEmbeddingsAsync"/>.
    /// Each embedding returned is a single float equal to the item's position in the batch,
    /// so callers can verify which result came from which input.
    /// </summary>
    private sealed class FakeEmbeddingGenerator : IEmbeddingGenerator
    {
        private readonly Func<IList<string>, IReadOnlyList<ReadOnlyMemory<float>>>? _factory;

        public List<IReadOnlyList<string>> BatchCalls { get; } = [];

        public FakeEmbeddingGenerator(Func<IList<string>, IReadOnlyList<ReadOnlyMemory<float>>>? factory = null)
        {
            this._factory = factory;
        }

        public Task<ReadOnlyMemory<float>> GenerateEmbeddingAsync(string input, CancellationToken cancellationToken = default)
            => Task.FromResult(new ReadOnlyMemory<float>([0f]));

        public Task<IReadOnlyList<ReadOnlyMemory<float>>> GenerateEmbeddingsAsync(
            IEnumerable<string> inputs,
            CancellationToken cancellationToken = default)
        {
            var inputList = inputs.ToList();
            this.BatchCalls.Add(inputList);

            IReadOnlyList<ReadOnlyMemory<float>> result = this._factory is not null
                ? this._factory(inputList)
                : inputList.Select((_, i) => new ReadOnlyMemory<float>([(float)i])).ToList();

            return Task.FromResult(result);
        }
    }

    // -------------------------------------------------------------------------
    // Constructor
    // -------------------------------------------------------------------------

    /// <summary>
    /// Verifies that constructing with a null inner generator throws.
    /// </summary>
    [Fact]
    public void Constructor_NullInner_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new BatchingEmbeddingGenerator(null!));
    }

    /// <summary>
    /// Verifies that a zero maxBatchSize throws.
    /// </summary>
    [Fact]
    public void Constructor_ZeroMaxBatchSize_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BatchingEmbeddingGenerator(new FakeEmbeddingGenerator(), maxBatchSize: 0));
    }

    /// <summary>
    /// Verifies that a negative maxBatchSize throws.
    /// </summary>
    [Fact]
    public void Constructor_NegativeMaxBatchSize_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BatchingEmbeddingGenerator(new FakeEmbeddingGenerator(), maxBatchSize: -1));
    }

    // -------------------------------------------------------------------------
    // GenerateEmbeddingAsync — routing
    // -------------------------------------------------------------------------

    /// <summary>
    /// Verifies that a single-item call is routed through the batch method on the inner
    /// generator, never the single-item method.
    /// </summary>
    [Fact]
    public async Task GenerateEmbeddingAsync_SingleCall_RoutedThroughBatchMethod()
    {
        var fake = new FakeEmbeddingGenerator();
        await using var batcher = new BatchingEmbeddingGenerator(fake, maxBatchSize: 10);

        await batcher.GenerateEmbeddingAsync("hello", TestContext.Current.CancellationToken);

        Assert.Single(fake.BatchCalls);
        Assert.Single(fake.BatchCalls[0]);
        Assert.Equal("hello", fake.BatchCalls[0][0]);
    }

    // -------------------------------------------------------------------------
    // GenerateEmbeddingAsync — results
    // -------------------------------------------------------------------------

    /// <summary>
    /// Verifies that the correct embedding vector is returned for a single call.
    /// </summary>
    [Fact]
    public async Task GenerateEmbeddingAsync_SingleCall_ReturnsCorrectVector()
    {
        float[] expected = [0.1f, 0.2f, 0.3f];
        var fake = new FakeEmbeddingGenerator(_ => [new ReadOnlyMemory<float>(expected)]);
        await using var batcher = new BatchingEmbeddingGenerator(fake, maxBatchSize: 10);

        var result = await batcher.GenerateEmbeddingAsync("hello", TestContext.Current.CancellationToken);

        Assert.Equal(expected, result.ToArray());
    }

    // -------------------------------------------------------------------------
    // GenerateEmbeddingAsync — coalescing
    // -------------------------------------------------------------------------

    /// <summary>
    /// Verifies that multiple calls made before the flush loop yields are coalesced into
    /// a single batch. Because <c>TryWrite</c> is synchronous, all three writes land in
    /// the channel before <c>await Task.WhenAll</c> ever yields to the background flusher,
    /// making this test deterministic without sleeps.
    /// </summary>
    [Fact]
    public async Task GenerateEmbeddingAsync_ThreeConcurrentCalls_CoalescedIntoOneBatch()
    {
        var fake = new FakeEmbeddingGenerator();
        await using var batcher = new BatchingEmbeddingGenerator(fake, maxBatchSize: 10);

        var t1 = batcher.GenerateEmbeddingAsync("a", TestContext.Current.CancellationToken);
        var t2 = batcher.GenerateEmbeddingAsync("b", TestContext.Current.CancellationToken);
        var t3 = batcher.GenerateEmbeddingAsync("c", TestContext.Current.CancellationToken);

        await Task.WhenAll(t1, t2, t3);

        Assert.Single(fake.BatchCalls);
        Assert.Equal(3, fake.BatchCalls[0].Count);
    }

    /// <summary>
    /// Verifies that inputs exceeding maxBatchSize are split across multiple batches.
    /// </summary>
    [Fact]
    public async Task GenerateEmbeddingAsync_FourCallsWithMaxBatchSizeTwo_SplitIntoTwoBatches()
    {
        var fake = new FakeEmbeddingGenerator();
        await using var batcher = new BatchingEmbeddingGenerator(fake, maxBatchSize: 2);

        var t1 = batcher.GenerateEmbeddingAsync("a", TestContext.Current.CancellationToken);
        var t2 = batcher.GenerateEmbeddingAsync("b", TestContext.Current.CancellationToken);
        var t3 = batcher.GenerateEmbeddingAsync("c", TestContext.Current.CancellationToken);
        var t4 = batcher.GenerateEmbeddingAsync("d", TestContext.Current.CancellationToken);

        await Task.WhenAll(t1, t2, t3, t4);

        Assert.Equal(2, fake.BatchCalls.Count);
        Assert.All(fake.BatchCalls, batch => Assert.Equal(2, batch.Count));
    }

    /// <summary>
    /// Verifies that the correct vector is returned for each call when multiple inputs are coalesced.
    /// </summary>
    [Fact]
    public async Task GenerateEmbeddingAsync_ThreeConcurrentCalls_EachReceivesCorrectVector()
    {
        var fake = new FakeEmbeddingGenerator();
        await using var batcher = new BatchingEmbeddingGenerator(fake, maxBatchSize: 10);

        var t1 = batcher.GenerateEmbeddingAsync("a", TestContext.Current.CancellationToken);
        var t2 = batcher.GenerateEmbeddingAsync("b", TestContext.Current.CancellationToken);
        var t3 = batcher.GenerateEmbeddingAsync("c", TestContext.Current.CancellationToken);

        var results = await Task.WhenAll(t1, t2, t3);

        // FakeEmbeddingGenerator returns index-as-float per item
        Assert.Equal(0f, results[0].Span[0]);
        Assert.Equal(1f, results[1].Span[0]);
        Assert.Equal(2f, results[2].Span[0]);
    }

    // -------------------------------------------------------------------------
    // GenerateEmbeddingAsync — cancellation
    // -------------------------------------------------------------------------

    /// <summary>
    /// Verifies that a pre-cancelled token causes an immediate OperationCanceledException.
    /// </summary>
    [Fact]
    public async Task GenerateEmbeddingAsync_PreCancelledToken_ThrowsImmediately()
    {
        var fake = new FakeEmbeddingGenerator();
        await using var batcher = new BatchingEmbeddingGenerator(fake, maxBatchSize: 10);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            batcher.GenerateEmbeddingAsync("hello", cts.Token));
    }

    /// <summary>
    /// Verifies that a request cancelled before the flush window is not forwarded to the inner generator.
    /// </summary>
    [Fact]
    public async Task GenerateEmbeddingAsync_CancelledBeforeFlush_NotSentToInner()
    {
        var fake = new FakeEmbeddingGenerator();
        // Long delay so the window doesn't fire — the maxBatchSize trigger drives the flush instead.
        await using var batcher = new BatchingEmbeddingGenerator(fake, maxBatchSize: 10, maxDelay: TimeSpan.FromSeconds(30));

        using var cts = new CancellationTokenSource();
        var pending = batcher.GenerateEmbeddingAsync("hello", cts.Token);

        // Cancel before the batch is flushed.
        await cts.CancelAsync();

        // Fill remaining slots to trigger the maxBatchSize flush.
        var fillers = Enumerable.Range(0, 9)
            .Select(i => batcher.GenerateEmbeddingAsync($"filler{i}", TestContext.Current.CancellationToken))
            .ToArray();
        await Task.WhenAll(fillers);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        // The cancelled input must not have been sent to the inner generator.
        Assert.DoesNotContain(fake.BatchCalls.SelectMany(b => b), s => s == "hello");
    }

    // -------------------------------------------------------------------------
    // GenerateEmbeddingsAsync — passthrough
    // -------------------------------------------------------------------------

    /// <summary>
    /// Verifies that GenerateEmbeddingsAsync bypasses the buffer and goes directly to the inner generator.
    /// </summary>
    [Fact]
    public async Task GenerateEmbeddingsAsync_PassesDirectlyToInner()
    {
        var fake = new FakeEmbeddingGenerator();
        await using var batcher = new BatchingEmbeddingGenerator(fake, maxBatchSize: 10);

        var result = await batcher.GenerateEmbeddingsAsync(["x", "y", "z"], TestContext.Current.CancellationToken);

        Assert.Equal(3, result.Count);
        Assert.Single(fake.BatchCalls);
        Assert.Equal(["x", "y", "z"], fake.BatchCalls[0]);
    }

    // -------------------------------------------------------------------------
    // Error propagation
    // -------------------------------------------------------------------------

    /// <summary>
    /// Verifies that an exception thrown by the inner generator is propagated to all callers in the batch.
    /// </summary>
    [Fact]
    public async Task GenerateEmbeddingAsync_InnerThrows_ExceptionPropagatedToAllCallers()
    {
        var boom = new InvalidOperationException("embedding service unavailable");
        var fake = new FakeEmbeddingGenerator(_ => throw boom);
        await using var batcher = new BatchingEmbeddingGenerator(fake, maxBatchSize: 10);

        var t1 = batcher.GenerateEmbeddingAsync("a", TestContext.Current.CancellationToken);
        var t2 = batcher.GenerateEmbeddingAsync("b", TestContext.Current.CancellationToken);

        var ex1 = await Assert.ThrowsAsync<InvalidOperationException>(() => t1);
        var ex2 = await Assert.ThrowsAsync<InvalidOperationException>(() => t2);

        Assert.Same(boom, ex1);
        Assert.Same(boom, ex2);
    }

    // -------------------------------------------------------------------------
    // DisposeAsync
    // -------------------------------------------------------------------------

    /// <summary>
    /// Verifies that DisposeAsync drains any items queued before disposal.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_WithPendingRequests_DrainsThem()
    {
        var fake = new FakeEmbeddingGenerator();
        var batcher = new BatchingEmbeddingGenerator(fake, maxBatchSize: 10);

        var t1 = batcher.GenerateEmbeddingAsync("a", TestContext.Current.CancellationToken);
        var t2 = batcher.GenerateEmbeddingAsync("b", TestContext.Current.CancellationToken);

        await batcher.DisposeAsync();

        Assert.True(t1.IsCompletedSuccessfully);
        Assert.True(t2.IsCompletedSuccessfully);
    }
}
