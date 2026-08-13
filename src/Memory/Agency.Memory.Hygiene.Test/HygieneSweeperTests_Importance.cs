using Agency.Memory.Common.Options;
using Agency.Memory.Common.Records;
using Agency.Memory.Common.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using MemoryRecord = Agency.Memory.Common.Records.Record;

namespace Agency.Memory.Hygiene.Test;

/// <summary>Tests for the importance-based pruning pass of <see cref="HygieneSweeperBackgroundService"/>.</summary>
public sealed class HygieneSweeperTests_Importance
{
    /// <summary>Creates a default <see cref="HygieneSweeperBackgroundService"/> for testing.</summary>
    private static HygieneSweeperBackgroundService CreateSweeper(
        IMemoryStore store,
        MemoryOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        var opts = Options.Create(options ?? new MemoryOptions());
        return new HygieneSweeperBackgroundService(
            store,
            opts,
            timeProvider ?? TimeProvider.System,
            NullLogger<HygieneSweeperBackgroundService>.Instance);
    }

    /// <summary>
    /// A minimal <see cref="IMemoryStore"/> fake whose <see cref="DeleteWhereLowImportanceStaleAsync"/>
    /// applies the exact predicate used by the production Postgres/Sqlite stores
    /// (<c>importance &lt; threshold AND (lastAccessedAt IS NULL OR lastAccessedAt &lt; now - staleAge)</c>),
    /// so tests can assert genuine per-record survive/prune outcomes — not just that the sweeper
    /// delegates with the right arguments — without requiring a live database.
    /// </summary>
    private sealed class PredicateFakeStore : IMemoryStore
    {
        private readonly List<MemoryRecord> _records = [];

        internal void Seed(MemoryRecord record) => this._records.Add(record);

        internal IReadOnlyList<MemoryRecord> Remaining => this._records;

        public Task<int> DeleteWhereLowImportanceStaleAsync(double importanceThreshold, TimeSpan staleAge, DateTimeOffset now, CancellationToken ct = default)
        {
            List<MemoryRecord> toDelete = this._records
                .Where(r => r.Importance < importanceThreshold
                    && (r.LastAccessedAt is null || r.LastAccessedAt < now - staleAge))
                .ToList();

            foreach (MemoryRecord r in toDelete)
            {
                this._records.Remove(r);
            }

            return Task.FromResult(toDelete.Count);
        }

        public Task<int> DeleteWhereTtlExceededAsync(ContentType contentType, TimeSpan ttl, DateTimeOffset now, CancellationToken ct = default)
        {
            List<MemoryRecord> toDelete = this._records
                .Where(r => r.ContentType == contentType
                    && r.UpdatedAt < now - ttl
                    && (r.LastAccessedAt is null || r.LastAccessedAt < now - ttl))
                .ToList();

            foreach (MemoryRecord r in toDelete)
            {
                this._records.Remove(r);
            }

            return Task.FromResult(toDelete.Count);
        }

        public Task<MemoryRecord> UpsertAsync(MemoryRecord record, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<MemoryRecord?> GetByKeyAsync(string userId, string? sessionId, string domain, string key, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> ForgetAsync(string userId, string domain, string key, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> ForgetMeAsync(string userId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<DateTimeOffset?> LastWrittenAtAsync(string userId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<MemoryRecord>> GetAllForUserAsync(string userId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<MemoryRecord> MergeAsync(IReadOnlyList<string> idsToDelete, MemoryRecord newRecord, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<MemoryRecord?> UpdateRecordAsync(string recordId, string userId, string? newValue, double? newImportance, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> DeleteByIdAsync(string recordId, string userId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> MemorizeNowAsync(string userId, string sessionId, string title, string value, string domain, Importance importance, string[] tags, CancellationToken ct = default) => throw new NotImplementedException();
    }

    /// <summary>Builds a record for the importance-threshold scenarios, defaulting to a Fact aged past the stale window.</summary>
    private static MemoryRecord MakeRecord(
        string id,
        double importance,
        DateTimeOffset lastAccessedAt,
        DateTimeOffset now,
        MemorySource source = MemorySource.Distilled,
        DateTimeOffset? updatedAt = null) =>
        MemoryRecord.Create(
            id: id,
            userId: "u1",
            sessionId: null,
            contentType: ContentType.Fact,
            domain: "Test",
            key: id,
            title: id,
            value: "value",
            tags: [],
            importance: importance,
            createdAt: updatedAt ?? now.AddDays(-90),
            updatedAt: updatedAt ?? now.AddDays(-90),
            lastAccessedAt: lastAccessedAt,
            source: source);

    /// <summary>Verifies that low-importance stale records are deleted.</summary>
    [Fact]
    public async Task Importance_LowImportance_StaleAge_Deleted()
    {
        var store = new Mock<IMemoryStore>();
        store
            .Setup(s => s.DeleteWhereLowImportanceStaleAsync(0.2, TimeSpan.FromDays(30), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(5);

        var options = new MemoryOptions
        {
            ImportancePruneThreshold = 0.2,
            StalePruneAge = TimeSpan.FromDays(30),
            Ttl = [],
        };

        var sweeper = CreateSweeper(store.Object, options);

        await sweeper.RunOnceAsync(TestContext.Current.CancellationToken);

        store.Verify(
            s => s.DeleteWhereLowImportanceStaleAsync(0.2, TimeSpan.FromDays(30), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>Verifies that low-importance but recently-accessed records are not deleted (store handles this).</summary>
    [Fact]
    public async Task Importance_LowImportance_RecentlyAccessed_NotDeleted()
    {
        var store = new Mock<IMemoryStore>();
        // Store returns 0 because recently-accessed records survived the predicate inside the store.
        store
            .Setup(s => s.DeleteWhereLowImportanceStaleAsync(It.IsAny<double>(), It.IsAny<TimeSpan>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        var sweeper = CreateSweeper(store.Object);

        await sweeper.RunOnceAsync(TestContext.Current.CancellationToken);

        // The sweeper must still call the method; the store correctly returned 0.
        store.Verify(
            s => s.DeleteWhereLowImportanceStaleAsync(It.IsAny<double>(), It.IsAny<TimeSpan>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>Verifies that high-importance stale records are not deleted.</summary>
    [Fact]
    public async Task Importance_HighImportance_StaleAge_NotDeleted()
    {
        var store = new Mock<IMemoryStore>();
        // Store returns 0 because high-importance records survived the importance < threshold predicate.
        store
            .Setup(s => s.DeleteWhereLowImportanceStaleAsync(It.IsAny<double>(), It.IsAny<TimeSpan>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        var options = new MemoryOptions
        {
            ImportancePruneThreshold = 0.2,
            StalePruneAge = TimeSpan.FromDays(30),
            Ttl = [],
        };

        var sweeper = CreateSweeper(store.Object, options);

        await sweeper.RunOnceAsync(TestContext.Current.CancellationToken);

        store.Verify(
            s => s.DeleteWhereLowImportanceStaleAsync(0.2, TimeSpan.FromDays(30), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Genuine threshold-behaviour scenarios (PredicateFakeStore) ──────────────
    //
    // The tests above only assert that the sweeper *delegates* to the store with the right
    // threshold/staleAge; the actual prune-vs-survive decision is made inside the store's SQL
    // predicate (see PostgresMemoryStore.DeleteWhereLowImportanceStaleAsync), which this project
    // does not reference. PredicateFakeStore mirrors that predicate over in-memory records so the
    // following tests exercise the real per-record outcome through the sweeper's public API.

    /// <summary>Low-importance agent fact (0.3) is above the 0.2 threshold and survives, even when stale.</summary>
    [Fact]
    public async Task Importance_LowImportanceValue_0_3_SurvivesThreshold_0_2()
    {
        var now = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(now);
        var store = new PredicateFakeStore();
        store.Seed(MakeRecord("low", importance: 0.3, lastAccessedAt: now.AddDays(-45), now: now));

        var options = new MemoryOptions { ImportancePruneThreshold = 0.2, StalePruneAge = TimeSpan.FromDays(30) };
        var sweeper = CreateSweeper(store, options, timeProvider);

        await sweeper.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Contains(store.Remaining, r => r.Id == "low");
    }

    /// <summary>A record whose importance falls below the threshold (0.1 &lt; 0.2) and is stale is pruned.</summary>
    [Fact]
    public async Task Importance_SubThresholdValue_0_1_AndStale_IsDeleted()
    {
        var now = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(now);
        var store = new PredicateFakeStore();
        store.Seed(MakeRecord("subthreshold", importance: 0.1, lastAccessedAt: now.AddDays(-45), now: now));

        var options = new MemoryOptions { ImportancePruneThreshold = 0.2, StalePruneAge = TimeSpan.FromDays(30) };
        var sweeper = CreateSweeper(store, options, timeProvider);

        await sweeper.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(store.Remaining, r => r.Id == "subthreshold");
    }

    /// <summary>High (0.9), Normal (0.6), and Low (0.3) importance records all survive the prune, however stale.</summary>
    [Theory]
    [InlineData(0.9)]
    [InlineData(0.6)]
    [InlineData(0.3)]
    public async Task Importance_HighNormalLow_AllSurvivePruning_EvenWhenVeryStale(double importance)
    {
        var now = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(now);
        var store = new PredicateFakeStore();
        store.Seed(MakeRecord("r", importance, lastAccessedAt: now.AddDays(-60), now: now));

        var options = new MemoryOptions { ImportancePruneThreshold = 0.2, StalePruneAge = TimeSpan.FromDays(30) };
        var sweeper = CreateSweeper(store, options, timeProvider);

        await sweeper.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Contains(store.Remaining, r => r.Id == "r");
    }

    /// <summary>
    /// The importance-pruning predicate has no special case for <see cref="MemorySource.AgentSignaled"/> —
    /// a Low-importance agent fact (0.3) survives purely because 0.3 &gt; 0.2, the same as any other
    /// source would at that importance value. This documents that MemorizeNow's protection comes from
    /// its importance floor (High=0.9/Normal=0.6/Low=0.3, set at write time), not from a Source check.
    /// </summary>
    [Fact]
    public async Task Importance_AgentSignaledLowImportanceFact_Survives_ViaImportanceFloor_NotSourceCheck()
    {
        var now = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(now);
        var store = new PredicateFakeStore();
        store.Seed(MakeRecord("agent-low", importance: 0.3, lastAccessedAt: now.AddDays(-45), now: now, source: MemorySource.AgentSignaled));

        var options = new MemoryOptions { ImportancePruneThreshold = 0.2, StalePruneAge = TimeSpan.FromDays(30) };
        var sweeper = CreateSweeper(store, options, timeProvider);

        await sweeper.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Contains(store.Remaining, r => r.Id == "agent-low");
    }

    /// <summary>A Distilled fact with sub-threshold importance is pruned once it is old enough — provenance does not protect it.</summary>
    [Fact]
    public async Task Importance_DistilledLowImportanceFact_PrunedWhenOldEnough()
    {
        var now = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(now);
        var store = new PredicateFakeStore();
        store.Seed(MakeRecord("distilled-low", importance: 0.1, lastAccessedAt: now.AddDays(-31), now: now, source: MemorySource.Distilled));

        var options = new MemoryOptions { ImportancePruneThreshold = 0.2, StalePruneAge = TimeSpan.FromDays(30) };
        var sweeper = CreateSweeper(store, options, timeProvider);

        await sweeper.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(store.Remaining, r => r.Id == "distilled-low");
    }

    /// <summary>A sub-threshold-importance record younger than the stale age survives — recency protects it too.</summary>
    [Fact]
    public async Task Importance_SubThreshold_ButUnderStaleAge_Survives()
    {
        var now = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(now);
        var store = new PredicateFakeStore();
        // Accessed 10 days ago — under the 30-day stale window.
        store.Seed(MakeRecord("young-low", importance: 0.1, lastAccessedAt: now.AddDays(-10), now: now));

        var options = new MemoryOptions { ImportancePruneThreshold = 0.2, StalePruneAge = TimeSpan.FromDays(30) };
        var sweeper = CreateSweeper(store, options, timeProvider);

        await sweeper.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Contains(store.Remaining, r => r.Id == "young-low");
    }

    /// <summary>
    /// The TTL pass and the importance pass are independent: a High-importance (0.9) record that has
    /// exceeded its content-type TTL is deleted by the TTL pass even though its importance would have
    /// protected it from the importance pass.
    /// </summary>
    [Fact]
    public async Task Importance_TtlPass_DeletesHighImportanceRecord_IndependentlyOfImportanceProtection()
    {
        var now = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(now);
        var store = new PredicateFakeStore();
        // High importance (0.9) but last touched 100 days ago — exceeds a 90-day Fact TTL.
        store.Seed(MakeRecord("high-but-ttl-expired", importance: 0.9, lastAccessedAt: now.AddDays(-100), now: now, updatedAt: now.AddDays(-100)));

        var options = new MemoryOptions
        {
            ImportancePruneThreshold = 0.2,
            StalePruneAge = TimeSpan.FromDays(30),
            Ttl = new Dictionary<ContentType, TimeSpan> { [ContentType.Fact] = TimeSpan.FromDays(90) },
        };
        var sweeper = CreateSweeper(store, options, timeProvider);

        await sweeper.RunOnceAsync(TestContext.Current.CancellationToken);

        // Deleted by the TTL pass despite high importance — importance only protects against the
        // importance-pruning pass, not the TTL pass.
        Assert.DoesNotContain(store.Remaining, r => r.Id == "high-but-ttl-expired");
    }
}
