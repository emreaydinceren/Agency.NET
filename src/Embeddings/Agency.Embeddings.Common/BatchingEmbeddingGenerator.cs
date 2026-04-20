using System.Threading.Channels;

namespace Agency.Embeddings.Common;

/// <summary>
/// Decorator that coalesces individual <see cref="IEmbeddingGenerator.GenerateEmbeddingAsync"/> calls
/// into batch requests, reducing round-trips to the embedding API.
/// </summary>
/// <remarks>
/// Up to <see cref="DefaultMaxBatchSize"/> pending requests are grouped into a single
/// <see cref="IEmbeddingGenerator.GenerateEmbeddingsAsync"/> call. A batch is flushed when it
/// reaches <paramref name="maxBatchSize"/> items or after <paramref name="maxDelay"/> has elapsed,
/// whichever comes first. <see cref="GenerateEmbeddingsAsync"/> bypasses the buffer and is forwarded
/// directly to the inner generator.
/// </remarks>
public sealed class BatchingEmbeddingGenerator : IEmbeddingGenerator, IAsyncDisposable
{
    /// <summary>Default maximum number of items per batch.</summary>
    public const int DefaultMaxBatchSize = 32;

    /// <summary>Default delay window before flushing a partial batch.</summary>
    public static readonly TimeSpan DefaultMaxDelay = TimeSpan.FromMilliseconds(50);

    private readonly record struct PendingRequest(string Input, TaskCompletionSource<ReadOnlyMemory<float>> Tcs);

    private readonly IEmbeddingGenerator _inner;
    private readonly int _maxBatchSize;
    private readonly TimeSpan _maxDelay;
    private readonly Channel<PendingRequest> _channel;
    private readonly Task _flushLoop;

    /// <summary>
    /// Creates a batching wrapper around <paramref name="inner"/>.
    /// </summary>
    /// <param name="inner">The underlying generator that receives batched requests.</param>
    /// <param name="maxBatchSize">Maximum items per batch. Defaults to <see cref="DefaultMaxBatchSize"/>.</param>
    /// <param name="maxDelay">Maximum time to accumulate items before flushing. Defaults to <see cref="DefaultMaxDelay"/>.</param>
    public BatchingEmbeddingGenerator(
        IEmbeddingGenerator inner,
        int maxBatchSize = DefaultMaxBatchSize,
        TimeSpan? maxDelay = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBatchSize, 1);

        this._inner = inner;
        this._maxBatchSize = maxBatchSize;
        this._maxDelay = maxDelay ?? DefaultMaxDelay;
        this._channel = Channel.CreateUnbounded<PendingRequest>(new UnboundedChannelOptions { SingleReader = true });
        this._flushLoop = Task.Run(this.FlushLoopAsync);
    }

    /// <summary>
    /// Enqueues <paramref name="input"/> into the next batch and returns the resulting embedding vector.
    /// </summary>
    public Task<ReadOnlyMemory<float>> GenerateEmbeddingAsync(
        string input,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var tcs = new TaskCompletionSource<ReadOnlyMemory<float>>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (cancellationToken.CanBeCanceled)
        {
            cancellationToken.Register(
                static (state, token) => ((TaskCompletionSource<ReadOnlyMemory<float>>)state!).TrySetCanceled(token),
                tcs);
        }

        this._channel.Writer.TryWrite(new PendingRequest(input, tcs));
        return tcs.Task;
    }

    /// <summary>
    /// Passes the batch directly to the inner generator without additional buffering.
    /// </summary>
    public Task<IReadOnlyList<ReadOnlyMemory<float>>> GenerateEmbeddingsAsync(
        IEnumerable<string> inputs,
        CancellationToken cancellationToken = default)
        => this._inner.GenerateEmbeddingsAsync(inputs, cancellationToken);

    /// <summary>
    /// Completes the input channel, drains any queued requests, and awaits the flush loop.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        this._channel.Writer.Complete();
        await this._flushLoop.ConfigureAwait(false);
    }

    private async Task FlushLoopAsync()
    {
        while (await this._channel.Reader.WaitToReadAsync(CancellationToken.None).ConfigureAwait(false))
        {
            var batch = new List<PendingRequest>(this._maxBatchSize);

            while (batch.Count < this._maxBatchSize && this._channel.Reader.TryRead(out var item))
            {
                batch.Add(item);
            }

            if (batch.Count < this._maxBatchSize)
            {
                using var windowCts = new CancellationTokenSource(this._maxDelay);
                try
                {
                    while (batch.Count < this._maxBatchSize &&
                           await this._channel.Reader.WaitToReadAsync(windowCts.Token).ConfigureAwait(false))
                    {
                        while (batch.Count < this._maxBatchSize && this._channel.Reader.TryRead(out var item))
                        {
                            batch.Add(item);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Window expired — flush what we have.
                }
            }

            await this.FlushBatchAsync(batch).ConfigureAwait(false);
        }
    }

    private async Task FlushBatchAsync(List<PendingRequest> batch)
    {
        var alive = batch.Where(static r => !r.Tcs.Task.IsCompleted).ToList();
        if (alive.Count == 0)
        {
            return;
        }

        try
        {
            var embeddings = await this._inner.GenerateEmbeddingsAsync(
                alive.Select(static r => r.Input),
                CancellationToken.None).ConfigureAwait(false);

            for (int i = 0; i < alive.Count; i++)
            {
                alive[i].Tcs.TrySetResult(embeddings[i]);
            }
        }
        catch (Exception ex)
        {
            foreach (var r in alive)
            {
                r.Tcs.TrySetException(ex);
            }
        }
    }
}
