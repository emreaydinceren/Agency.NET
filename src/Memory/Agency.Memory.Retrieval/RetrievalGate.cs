using Agency.Harness.Contexts;
using Agency.Memory.Common.Storage;

namespace Agency.Memory.Retrieval;

/// <summary>
/// Evaluates whether a vector search should run for the current agent iteration,
/// implementing the retrieval gate described in Spec §8.1.
/// </summary>
/// <remarks>
/// The gate skips retrieval when the memory store has not been written since
/// the last retrieval pass, making the check O(1) via the in-memory
/// <c>LastWrittenAt</c> cache on the store.
/// </remarks>
internal static class RetrievalGate
{
    /// <summary>
    /// Returns <see langword="true"/> when retrieval should run; <see langword="false"/>
    /// when the store is unchanged since the last pass and the search can be skipped.
    /// </summary>
    /// <param name="ctx">The current session context.</param>
    /// <param name="store">The memory store whose <c>LastWrittenAtAsync</c> provides the gate signal.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see langword="true"/> when <c>ctx.MemoryLastRetrievedAt</c> is <see langword="null"/>,
    /// or when the store's last-write timestamp is later than the last retrieval.
    /// </returns>
    internal static async ValueTask<bool> ShouldRetrieveAsync(
        Context ctx,
        IMemoryStore store,
        CancellationToken ct = default)
    {
        string userId = ctx.User.Id ?? string.Empty;
        DateTimeOffset? lastWritten = await store.LastWrittenAtAsync(userId, ct).ConfigureAwait(false);

        // No prior writes at all — store is empty; run a search (returns empty list quickly).
        if (lastWritten is null)
        {
            return true;
        }

        // First retrieval in this session.
        if (ctx.MemoryLastRetrievedAt is null)
        {
            return true;
        }

        // Store was mutated after the last retrieval — new records may be available.
        return lastWritten > ctx.MemoryLastRetrievedAt;
    }
}
