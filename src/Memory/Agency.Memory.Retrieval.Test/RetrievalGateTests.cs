using Agency.Agentic.Contexts;
using Agency.Memory.Common.Records;
using Agency.Memory.Common.Storage;
using Agency.Memory.Retrieval;

namespace Agency.Memory.Retrieval.Test;

/// <summary>
/// Unit tests for <see cref="RetrievalGate"/> — covers the gate logic described in Spec §8.1.
/// </summary>
public sealed class RetrievalGateTests
{
    private sealed class StubMemoryStore : IMemoryStore
    {
        private readonly DateTimeOffset? _lastWritten;

        public StubMemoryStore(DateTimeOffset? lastWritten)
        {
            _lastWritten = lastWritten;
        }

        public Task<DateTimeOffset?> LastWrittenAtAsync(string userId, CancellationToken ct = default) =>
            Task.FromResult(_lastWritten);

        public Task<Common.Records.Record> UpsertAsync(Common.Records.Record record, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<Common.Records.Record?> GetByKeyAsync(string userId, string? sessionId, string domain, string key, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<bool> ForgetAsync(string userId, string domain, string key, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<int> ForgetMeAsync(string userId, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<Common.Records.Record>> GetAllForUserAsync(string userId, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<int> DeleteWhereTtlExceededAsync(ContentType contentType, TimeSpan ttl, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<int> DeleteWhereLowImportanceStaleAsync(double importanceThreshold, TimeSpan staleAge, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<Common.Records.Record> MergeAsync(IReadOnlyList<string> idsToDelete, Common.Records.Record newRecord, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<Common.Records.Record?> UpdateRecordAsync(string recordId, string userId, string? newValue, double? newImportance, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<bool> DeleteByIdAsync(string recordId, string userId, CancellationToken ct = default) =>
            throw new NotImplementedException();
    }

    private static Context MakeContext(string userId = "user1") =>
        new()
        {
            Query = new QueryContext { Prompt = "hello" },
            User = new UserSpecificContext { Id = userId },
        };

    /// <summary>
    /// When <c>MemoryLastRetrievedAt</c> is null (first call ever for this session),
    /// the gate must return <see langword="true"/> — retrieval should run.
    /// </summary>
    [Fact]
    public async Task Gate_FirstCall_RunsSearch()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = MakeContext();
        var store = new StubMemoryStore(lastWritten: DateTimeOffset.UtcNow.AddDays(-1));

        bool result = await RetrievalGate.ShouldRetrieveAsync(ctx, store, ct);

        Assert.True(result);
    }

    /// <summary>
    /// When the store has not been written since the last retrieval, the gate returns
    /// <see langword="false"/> — skip the redundant vector search.
    /// </summary>
    [Fact]
    public async Task Gate_StoreUnchangedSinceLastRetrieval_Skips()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = MakeContext();
        var lastRetrieved = DateTimeOffset.UtcNow.AddMinutes(-5);
        ctx.MemoryLastRetrievedAt = lastRetrieved;

        // Store was written 10 minutes ago — BEFORE the last retrieval.
        var store = new StubMemoryStore(lastWritten: DateTimeOffset.UtcNow.AddMinutes(-10));

        bool result = await RetrievalGate.ShouldRetrieveAsync(ctx, store, ct);

        Assert.False(result);
    }

    /// <summary>
    /// When the store has been written AFTER the last retrieval, the gate must return
    /// <see langword="true"/> — new records may exist that should be surfaced.
    /// </summary>
    [Fact]
    public async Task Gate_StoreWrittenSince_RunsSearch()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = MakeContext();
        var lastRetrieved = DateTimeOffset.UtcNow.AddMinutes(-5);
        ctx.MemoryLastRetrievedAt = lastRetrieved;

        // Store was written 2 minutes ago — AFTER the last retrieval.
        var store = new StubMemoryStore(lastWritten: DateTimeOffset.UtcNow.AddMinutes(-2));

        bool result = await RetrievalGate.ShouldRetrieveAsync(ctx, store, ct);

        Assert.True(result);
    }

    /// <summary>
    /// When the gate decides to skip, it must not mutate the context (no side effects on skip).
    /// </summary>
    [Fact]
    public async Task Gate_DoesNotMutateContext_OnSkip()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = MakeContext();
        var lastRetrieved = DateTimeOffset.UtcNow.AddMinutes(-5);
        ctx.MemoryLastRetrievedAt = lastRetrieved;
        var store = new StubMemoryStore(lastWritten: DateTimeOffset.UtcNow.AddMinutes(-10));

        _ = await RetrievalGate.ShouldRetrieveAsync(ctx, store, ct);

        // Context must not have been modified.
        Assert.Equal(lastRetrieved, ctx.MemoryLastRetrievedAt);
        Assert.Empty(ctx.Knowledge.Records);
        Assert.Empty(ctx.Memory.Records);
    }
}
