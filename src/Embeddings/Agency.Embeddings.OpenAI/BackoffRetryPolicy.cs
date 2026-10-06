using System.ClientModel.Primitives;

namespace Agency.Embeddings.OpenAI;

/// <summary>
/// The SDK's retry policy with a predictable exponential wait between attempts. Out of the box it retries transient
/// failures (429, 5xx, timeouts, connection errors) almost immediately, which does not help a local model server that
/// is busy. Which failures are retried is unchanged; only the wait is.
/// </summary>
internal sealed class BackoffRetryPolicy(int maxRetries, TimeSpan initialDelay) : ClientRetryPolicy(maxRetries)
{
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);

    /// <inheritdoc/>
    protected override TimeSpan GetNextDelay(PipelineMessage message, int tryCount)
    {
        // tryCount is 1 when the first retry is about to wait, so the waits run initialDelay, 2x, 4x, ...
        return TimeSpan.FromTicks((long)Math.Min(initialDelay.Ticks * Math.Pow(2, Math.Max(0, tryCount - 1)), MaxDelay.Ticks));
    }
}
