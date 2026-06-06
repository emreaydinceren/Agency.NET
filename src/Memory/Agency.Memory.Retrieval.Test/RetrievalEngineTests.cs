using Agency.Harness.Contexts;
using Agency.Embeddings.Common;
using Agency.Memory.Common.Options;
using Agency.Memory.Common.Ranking;
using Agency.Memory.Common.Records;
using Agency.Memory.Common.Storage;
using Microsoft.Extensions.Options;

namespace Agency.Memory.Retrieval.Test;

/// <summary>
/// Unit tests for <see cref="RetrievalEngine"/> — covers Spec §6.4.
/// </summary>
public sealed class RetrievalEngineTests
{
    // ── Fakes ─────────────────────────────────────────────────────────────────

    private sealed class FakeEmbeddingGenerator : IEmbeddingGenerator
    {
        public List<string> ReceivedInputs { get; } = [];

        public Task<ReadOnlyMemory<float>> GenerateEmbeddingAsync(string input, CancellationToken cancellationToken = default)
        {
            ReceivedInputs.Add(input);
            return Task.FromResult(new ReadOnlyMemory<float>([0.1f, 0.2f, 0.3f]));
        }

        public Task<IReadOnlyList<ReadOnlyMemory<float>>> GenerateEmbeddingsAsync(IEnumerable<string> inputs, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    private sealed class FakeMemoryStore : IMemoryStore
    {
        private readonly IReadOnlyList<SearchHit> _hits;
        private readonly DateTimeOffset? _lastWritten;
        public SearchQuery? LastQuery { get; private set; }

        public FakeMemoryStore(IReadOnlyList<SearchHit>? hits = null, DateTimeOffset? lastWritten = null)
        {
            _hits = hits ?? [];
            _lastWritten = lastWritten ?? DateTimeOffset.UtcNow.AddDays(-1);
        }

        public Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken ct = default)
        {
            LastQuery = query;
            return Task.FromResult(_hits);
        }

        public Task<DateTimeOffset?> LastWrittenAtAsync(string userId, CancellationToken ct = default) =>
            Task.FromResult(_lastWritten);

        public Task<Common.Records.Record> UpsertAsync(Common.Records.Record record, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<Common.Records.Record?> GetByKeyAsync(string userId, string? sessionId, string domain, string key, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<bool> ForgetAsync(string userId, string domain, string key, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<int> ForgetMeAsync(string userId, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<Common.Records.Record>> GetAllForUserAsync(string userId, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<int> DeleteWhereTtlExceededAsync(ContentType contentType, TimeSpan ttl, DateTimeOffset now, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<int> DeleteWhereLowImportanceStaleAsync(double importanceThreshold, TimeSpan staleAge, DateTimeOffset now, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<Common.Records.Record> MergeAsync(IReadOnlyList<string> idsToDelete, Common.Records.Record newRecord, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<Common.Records.Record?> UpdateRecordAsync(string recordId, string userId, string? newValue, double? newImportance, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<bool> DeleteByIdAsync(string recordId, string userId, CancellationToken ct = default) =>
            throw new NotImplementedException();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static IOptions<MemoryOptions> DefaultOptions(int topK = 3, int overFetch = 2) =>
        Options.Create(new MemoryOptions
        {
            RetrievalTopK = topK,
            OverFetchFactor = overFetch,
            Ranking = RankingWeights.Default,
            RecencyHalfLifeDays = 7.0,
        });

    private static Context MakeContext(string userId = "u1", string prompt = "hello")
    {
        return new Context
        {
            Query = new QueryContext { Prompt = prompt },
            User = new UserSpecificContext { Id = userId },
        };
    }

    private static Common.Records.Record MakeFact(
        string id = "f1",
        string userId = "u1",
        string? sessionId = null,
        double importance = 0.5,
        DateTimeOffset? updatedAt = null)
    {
        var now = updatedAt ?? DateTimeOffset.UtcNow;
        return Common.Records.Record.Create(
            id: id,
            userId: userId,
            sessionId: sessionId,
            contentType: ContentType.Fact,
            domain: "Preferences",
            key: $"Key_{id}",
            title: $"Title {id}",
            value: $"Value {id}",
            tags: [],
            importance: importance,
            createdAt: now,
            updatedAt: now,
            embedding: new ReadOnlyMemory<float>([0.1f, 0.2f, 0.3f]));
    }

    private static Common.Records.Record MakeMemory(
        string id = "m1",
        string userId = "u1",
        string? sessionId = null,
        double importance = 0.5,
        DateTimeOffset? updatedAt = null)
    {
        var now = updatedAt ?? DateTimeOffset.UtcNow;
        return Common.Records.Record.Create(
            id: id,
            userId: userId,
            sessionId: sessionId,
            contentType: ContentType.Memory,
            domain: "Debugging",
            key: $"Key_{id}",
            title: $"Memory {id}",
            value: $"## Observation\nObs {id}",
            tags: [],
            importance: importance,
            createdAt: now,
            updatedAt: now,
            embedding: new ReadOnlyMemory<float>([0.1f, 0.2f, 0.3f]));
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The engine fetches <c>topK * overFetchFactor</c> candidates from the store
    /// so that re-ranking has a broader pool.
    /// </summary>
    [Fact]
    public async Task Retrieve_OverFetches_TopK_TimesOverFetchFactor_FromStore()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new FakeMemoryStore();
        var engine = new RetrievalEngine(store, new FakeEmbeddingGenerator(), DefaultOptions(topK: 5, overFetch: 3));
        var ctx = MakeContext(prompt: "some query");

        await engine.RetrieveAsync(ctx, ct);

        Assert.NotNull(store.LastQuery);
        Assert.Equal(5 * 3, store.LastQuery!.TopK);
    }

    /// <summary>
    /// When hits have deliberately mis-ordered similarities, the composite ranking formula
    /// re-orders them so the highest composite score wins, not the highest cosine similarity.
    /// </summary>
    [Fact]
    public async Task Retrieve_AppliesRankingFormula_OrderingByCompositeScore()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;

        // Record A: high similarity 0.9, but very old (30 days), low importance 0.1
        // Record B: low  similarity 0.5, fresh (1 hour), high importance 0.8
        var recordA = MakeFact("a", updatedAt: now.AddDays(-30), importance: 0.1);
        var recordB = MakeFact("b", updatedAt: now.AddHours(-1), importance: 0.8);

        var hits = new List<SearchHit>
        {
            new(recordA, Similarity: 0.9),
            new(recordB, Similarity: 0.5),
        };

        var store = new FakeMemoryStore(hits);
        var engine = new RetrievalEngine(store, new FakeEmbeddingGenerator(), DefaultOptions(topK: 2, overFetch: 1));
        var ctx = MakeContext();

        await engine.RetrieveAsync(ctx, ct);

        // Record B should rank first due to high importance + recency even with lower similarity.
        Assert.Equal(2, ctx.Knowledge.Records.Count);
        Assert.Equal("b", ctx.Knowledge.Records[0].Title.Split(' ').Last());
    }

    /// <summary>
    /// Facts go to <c>ctx.Knowledge.Records</c> and Memories go to <c>ctx.Memory.Records</c>.
    /// </summary>
    [Fact]
    public async Task Retrieve_PartitionsByContentType_FactsToKnowledge_MemoriesToMemory()
    {
        var ct = TestContext.Current.CancellationToken;
        var fact = MakeFact("f1");
        var memory = MakeMemory("m1");
        var hits = new List<SearchHit>
        {
            new(fact, Similarity: 0.8),
            new(memory, Similarity: 0.7),
        };

        var store = new FakeMemoryStore(hits);
        var engine = new RetrievalEngine(store, new FakeEmbeddingGenerator(), DefaultOptions(topK: 5, overFetch: 1));
        var ctx = MakeContext();

        await engine.RetrieveAsync(ctx, ct);

        Assert.Single(ctx.Knowledge.Records);
        Assert.Single(ctx.Memory.Records);
        Assert.Equal("Title f1", ctx.Knowledge.Records[0].Title);
        Assert.Equal("Memory m1", ctx.Memory.Records[0].Title);
    }

    /// <summary>
    /// The combined output is capped to <c>RetrievalTopK</c> records.
    /// </summary>
    [Fact]
    public async Task Retrieve_RespectsTopK_CapsCombined()
    {
        var ct = TestContext.Current.CancellationToken;
        var hits = Enumerable.Range(1, 10)
            .Select(i => new SearchHit(MakeFact($"f{i}"), Similarity: 0.9 - (i * 0.05)))
            .ToList();

        var store = new FakeMemoryStore(hits);
        var engine = new RetrievalEngine(store, new FakeEmbeddingGenerator(), DefaultOptions(topK: 3, overFetch: 1));
        var ctx = MakeContext();

        await engine.RetrieveAsync(ctx, ct);

        Assert.Equal(3, ctx.Knowledge.Records.Count + ctx.Memory.Records.Count);
    }

    /// <summary>
    /// When the store returns no results, both collections are empty and no exception is thrown.
    /// </summary>
    [Fact]
    public async Task Retrieve_EmptyStore_AssignsEmptyLists_DoesNotThrow()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new FakeMemoryStore([]);
        var engine = new RetrievalEngine(store, new FakeEmbeddingGenerator(), DefaultOptions());
        var ctx = MakeContext();

        await engine.RetrieveAsync(ctx, ct);

        Assert.Empty(ctx.Knowledge.Records);
        Assert.Empty(ctx.Memory.Records);
    }

    /// <summary>
    /// After a successful retrieval, <c>ctx.MemoryLastRetrievedAt</c> must be set to approximately now.
    /// </summary>
    [Fact]
    public async Task Retrieve_SetsMemoryLastRetrievedAtToNow()
    {
        var ct = TestContext.Current.CancellationToken;
        var before = DateTimeOffset.UtcNow;
        var store = new FakeMemoryStore([]);
        var engine = new RetrievalEngine(store, new FakeEmbeddingGenerator(), DefaultOptions());
        var ctx = MakeContext();

        await engine.RetrieveAsync(ctx, ct);

        Assert.NotNull(ctx.MemoryLastRetrievedAt);
        Assert.True(ctx.MemoryLastRetrievedAt >= before);
        Assert.True(ctx.MemoryLastRetrievedAt <= DateTimeOffset.UtcNow.AddSeconds(1));
    }

    /// <summary>
    /// A record with an unrecognised <c>ContentType</c> must not silently land in the Memory
    /// bucket — it should throw rather than misroute. This guards against a future third value
    /// (e.g., <c>Reflection</c>) slipping through unnoticed (Spec §18.4).
    /// </summary>
    [Fact]
    public async Task Retrieve_UnknownContentType_ThrowsRatherThanSilentlyRoutingToMemory()
    {
        var ct = TestContext.Current.CancellationToken;
        // (ContentType)99 simulates a future third enum value not yet handled by the partition.
        var unknownRecord = Common.Records.Record.Create(
            id: "x1",
            userId: "u1",
            sessionId: null,
            contentType: (ContentType)99,
            domain: "Unknown",
            key: "Key_x1",
            title: "Unknown x1",
            value: "some value",
            tags: [],
            importance: 0.5,
            createdAt: DateTimeOffset.UtcNow,
            updatedAt: DateTimeOffset.UtcNow,
            embedding: new ReadOnlyMemory<float>([0.1f, 0.2f, 0.3f]));

        var store = new FakeMemoryStore([new SearchHit(unknownRecord, Similarity: 0.8)]);
        var engine = new RetrievalEngine(store, new FakeEmbeddingGenerator(), DefaultOptions(topK: 5, overFetch: 1));
        var ctx = MakeContext();

        await Assert.ThrowsAnyAsync<Exception>(() => engine.RetrieveAsync(ctx, ct));
    }

    /// <summary>
    /// When a <c>Focus</c> context is present, the query text sent to the embedder appends
    /// the focus title, domain, and tag csv.
    /// </summary>
    [Fact]
    public async Task Retrieve_AppendsFocusToQueryText()
    {
        var ct = TestContext.Current.CancellationToken;
        var embedder = new FakeEmbeddingGenerator();
        var store = new FakeMemoryStore([]);
        var engine = new RetrievalEngine(store, embedder, DefaultOptions());
        var ctx = MakeContext(prompt: "last user message");
        ctx.Focus = new FocusContext
        {
            Title = "Auth Debugging",
            Domain = "Debugging",
            Tags = ["ssl", "dns"],
        };

        await engine.RetrieveAsync(ctx, ct);

        Assert.Single(embedder.ReceivedInputs);
        string query = embedder.ReceivedInputs[0];
        Assert.Contains("last user message", query);
        Assert.Contains("Auth Debugging", query);
        Assert.Contains("Debugging", query);
        Assert.Contains("ssl", query);
        Assert.Contains("dns", query);
    }

    /// <summary>
    /// A record whose <c>SessionId</c> matches <c>ctx.Session.Id</c> must rank ahead of an
    /// otherwise-comparable record from a different session, because the session-match bonus
    /// (+0.1 × wₘ) tips the composite score in its favour even when its cosine similarity
    /// is lower (Spec §8.3 P3 session-match bonus).
    ///
    /// Scoring with default weights (sim=0.5, recency=0.3, importance=0.2, sessionMatch=0.1),
    /// both records fresh (recency=1) and importance=0.5:
    ///   Record A (session "s1", matching):     sim=0.70 → 0.350 + 0.300 + 0.100 + 0.100 = 0.850
    ///   Record B (session "s2", non-matching): sim=0.75 → 0.375 + 0.300 + 0.100 + 0.000 = 0.775
    /// Record A wins despite the lower similarity — the bonus is the decisive factor.
    /// </summary>
    [Fact]
    public async Task Retrieve_SameSessionRecord_OutranksOtherSession_WhenScoresOtherwiseEqual()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;

        // Record A: belongs to the current session; slightly lower similarity.
        var recordA = MakeFact("a", sessionId: "s1", importance: 0.5, updatedAt: now);
        // Record B: belongs to a different session; slightly higher similarity (would win without the bonus).
        var recordB = MakeFact("b", sessionId: "s2", importance: 0.5, updatedAt: now);

        var hits = new List<SearchHit>
        {
            new(recordA, Similarity: 0.70),
            new(recordB, Similarity: 0.75),
        };

        var store = new FakeMemoryStore(hits);
        var engine = new RetrievalEngine(store, new FakeEmbeddingGenerator(), DefaultOptions(topK: 2, overFetch: 1));
        var ctx = MakeContext(userId: "u1");
        ctx.Session = new SessionContext { Id = "s1" };

        await engine.RetrieveAsync(ctx, ct);

        // Both records are Facts → both land in ctx.Knowledge.Records.
        Assert.Equal(2, ctx.Knowledge.Records.Count);
        // The session-matched record (A) must be ranked first.
        Assert.Equal("Title a", ctx.Knowledge.Records[0].Title);
        Assert.Equal("Title b", ctx.Knowledge.Records[1].Title);
    }
}
