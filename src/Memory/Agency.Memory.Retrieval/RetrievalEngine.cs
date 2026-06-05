using Agency.Harness.Contexts;
using Agency.Memory.Common.Options;
using Agency.Memory.Common.Ranking;
using Agency.Memory.Common.Records;
using Agency.Memory.Common.Storage;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Agency.Memory.Retrieval;

/// <summary>
/// Retrieves the most relevant memory records for the current agent iteration and injects
/// them into <see cref="Context.Knowledge"/> and <see cref="Context.Memory"/>.
/// Implements the retrieval flow described in Spec §6.4.
/// </summary>
/// <remarks>
/// The engine over-fetches by <see cref="MemoryOptions.OverFetchFactor"/> before applying
/// the composite ranking formula, then partitions the top-K results by
/// <see cref="ContentType"/>.
/// </remarks>
internal sealed class RetrievalEngine
{
    private readonly IMemoryStore _store;
    private readonly Agency.Embeddings.Common.IEmbeddingGenerator _embedder;
    private readonly IOptions<MemoryOptions> _options;

    /// <summary>
    /// Initialises a new <see cref="RetrievalEngine"/>.
    /// </summary>
    /// <param name="store">The memory store to search.</param>
    /// <param name="embedder">The embedding generator used to vectorise the query text.</param>
    /// <param name="options">Memory configuration options.</param>
    internal RetrievalEngine(IMemoryStore store, Agency.Embeddings.Common.IEmbeddingGenerator embedder, IOptions<MemoryOptions> options)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// Runs the retrieval pass for the given context, updating
    /// <see cref="Context.Knowledge"/>, <see cref="Context.Memory"/>, and
    /// <see cref="Context.MemoryLastRetrievedAt"/>.
    /// </summary>
    /// <param name="ctx">The current session context.</param>
    /// <param name="ct">Cancellation token.</param>
    internal async Task RetrieveAsync(Context ctx, CancellationToken ct = default)
    {
        MemoryOptions opts = _options.Value;
        string userId = ctx.User.Id ?? string.Empty;

        // Build query text: last user message + focus terms.
        string lastUserMessage = ctx.Conversation.Messages
            .LastOrDefault(static m => m.Role == ChatRole.User)
            ?.Text ?? ctx.Query.Prompt;

        string query = BuildQueryText(lastUserMessage, ctx.Focus);

        // Embed the query.
        ReadOnlyMemory<float> queryVec = await _embedder.GenerateEmbeddingAsync(query, ct).ConfigureAwait(false);

        // Over-fetch candidates.
        int fetchK = opts.RetrievalTopK * opts.OverFetchFactor;
        IReadOnlyList<SearchHit> hits = await _store.SearchAsync(
            new SearchQuery(userId, queryVec, fetchK), ct).ConfigureAwait(false);

        // Re-rank using the composite formula.
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string? sessionId = ctx.Session.Id;

        List<(SearchHit Hit, double Score)> scored = hits
            .Select(h => (Hit: h, Score: RankingFormula.Score(
                h.Similarity,
                h.Record,
                sessionId,
                now,
                opts.Ranking,
                opts.RecencyHalfLifeDays)))
            .OrderByDescending(x => x.Score)
            .Take(opts.RetrievalTopK)
            .ToList();

        // Partition by ContentType — explicit arms, no default, so a future enum value
        // forces a compile-time decision (CS8509) rather than silently routing to Memory.
        List<MemoryRecord> facts = [];
        List<MemoryRecord> memories = [];

        foreach ((SearchHit hit, double _) in scored)
        {
            Common.Records.Record r = hit.Record;
            var projected = new MemoryRecord(r.Title, r.Value, r.UpdatedAt);
            (r.ContentType switch
            {
                ContentType.Fact => facts,
                ContentType.Memory => memories,
                _ => throw new InvalidOperationException($"Unhandled ContentType {r.ContentType}."),
            }).Add(projected);
        }

        ctx.Knowledge = ctx.Knowledge with { Records = facts };
        ctx.Memory = ctx.Memory with { Records = memories };
        ctx.MemoryLastRetrievedAt = now;
    }

    /// <summary>
    /// Builds the retrieval query text by appending focus terms to the last user message.
    /// </summary>
    /// <param name="lastUserMessage">The raw text of the most recent user turn.</param>
    /// <param name="focus">The current focus context; may be empty.</param>
    /// <returns>The combined query string.</returns>
    private static string BuildQueryText(string lastUserMessage, FocusContext focus)
    {
        if (focus == FocusContext.Empty
            || (focus.Title is null && focus.Domain is null && focus.Tags.Count == 0))
        {
            return lastUserMessage;
        }

        var parts = new List<string> { lastUserMessage };

        if (focus.Title is { Length: > 0 } title)
        {
            parts.Add(title);
        }

        if (focus.Domain is { Length: > 0 } domain)
        {
            parts.Add(domain);
        }

        if (focus.Tags.Count > 0)
        {
            parts.Add(string.Join(" ", focus.Tags));
        }

        return string.Join(" ", parts);
    }
}
