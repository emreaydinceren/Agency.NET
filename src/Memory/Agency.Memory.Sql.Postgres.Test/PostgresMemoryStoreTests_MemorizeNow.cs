using Agency.Memory.Common.Options;
using Agency.Memory.Common.Records;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Agency.Memory.Sql.Postgres.Test;

/// <summary>
/// Functional tests for <see cref="PostgresMemoryStore.MemorizeNowAsync"/>.
/// Require a running PostgreSQL instance.
/// </summary>
/// <remarks>
/// <see cref="PostgresMemoryStore"/> is <see langword="sealed"/> and <c>MemorizeNowAsync</c> delegates
/// to its own concrete <c>UpsertAsync(Record, CancellationToken)</c> overload rather than an injected,
/// mockable dependency — there is no seam to substitute a mock <c>IMemoryStore.UpsertAsync</c> at
/// (per <c>Agents/CSharpPrinciples.md</c>: "Sealed classes cannot be mocked — use functional/integration
/// tests"). These tests therefore exercise the real behaviour end-to-end against Postgres, matching the
/// sibling <c>PostgresMemoryStoreTests_Upsert.cs</c>/<c>_Forget.cs</c>/<c>_LastWritten.cs</c> convention,
/// rather than mocking an upsert call as the project plan's illustrative snippet imagined.
/// </remarks>
[Trait("Category", "Functional")]
public sealed class PostgresMemoryStoreTests_MemorizeNow : IAsyncLifetime
{
    private NpgsqlDataSource _dataSource = default!;
    private readonly string _runId = Guid.NewGuid().ToString("N")[..8];

    /// <summary>Initialises the store for each test.</summary>
    public async ValueTask InitializeAsync()
    {
        this._dataSource = TestHelpers.BuildDataSource<PostgresMemoryStoreTests_MemorizeNow>();
        await TestHelpers.ResetSchemaAsync(this._dataSource, 1536, TestContext.Current.CancellationToken);
    }

    /// <summary>Disposes the data source after each test.</summary>
    public async ValueTask DisposeAsync() => await this._dataSource.DisposeAsync();

    // ── validation ───────────────────────────────────────────────────────────

    /// <summary>Null title throws <see cref="ArgumentNullException"/> (via <c>ArgumentException.ThrowIfNullOrWhiteSpace</c>).</summary>
    [Fact]
    public async Task MemorizeNowAsync_NullTitle_ThrowsArgumentNullException()
    {
        var store = this.MakeStore();
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            store.MemorizeNowAsync("u1", "s1", null!, "value", "Domain", Importance.Normal, [], TestContext.Current.CancellationToken));
    }

    /// <summary>Empty/whitespace title throws <see cref="ArgumentException"/>.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MemorizeNowAsync_BlankTitle_ThrowsArgumentException(string title)
    {
        var store = this.MakeStore();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.MemorizeNowAsync("u1", "s1", title, "value", "Domain", Importance.Normal, [], TestContext.Current.CancellationToken));
    }

    /// <summary>Null value throws <see cref="ArgumentNullException"/>.</summary>
    [Fact]
    public async Task MemorizeNowAsync_NullValue_ThrowsArgumentNullException()
    {
        var store = this.MakeStore();
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            store.MemorizeNowAsync("u1", "s1", "Title", null!, "Domain", Importance.Normal, [], TestContext.Current.CancellationToken));
    }

    /// <summary>Empty/whitespace value throws <see cref="ArgumentException"/>.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MemorizeNowAsync_BlankValue_ThrowsArgumentException(string value)
    {
        var store = this.MakeStore();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.MemorizeNowAsync("u1", "s1", "Title", value, "Domain", Importance.Normal, [], TestContext.Current.CancellationToken));
    }

    /// <summary>Null domain throws <see cref="ArgumentNullException"/>.</summary>
    [Fact]
    public async Task MemorizeNowAsync_NullDomain_ThrowsArgumentNullException()
    {
        var store = this.MakeStore();
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            store.MemorizeNowAsync("u1", "s1", "Title", "value", null!, Importance.Normal, [], TestContext.Current.CancellationToken));
    }

    /// <summary>Empty/whitespace domain throws <see cref="ArgumentException"/>.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MemorizeNowAsync_BlankDomain_ThrowsArgumentException(string domain)
    {
        var store = this.MakeStore();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.MemorizeNowAsync("u1", "s1", "Title", "value", domain, Importance.Normal, [], TestContext.Current.CancellationToken));
    }

    /// <summary>Null tags array throws <see cref="ArgumentNullException"/>.</summary>
    [Fact]
    public async Task MemorizeNowAsync_NullTags_ThrowsArgumentNullException()
    {
        var store = this.MakeStore();
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            store.MemorizeNowAsync("u1", "s1", "Title", "value", "Domain", Importance.Normal, null!, TestContext.Current.CancellationToken));
    }

    /// <summary>More than 4 tags throws <see cref="ArgumentException"/>.</summary>
    [Fact]
    public async Task MemorizeNowAsync_MoreThanFourTags_ThrowsArgumentException()
    {
        var store = this.MakeStore();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.MemorizeNowAsync("u1", "s1", "Title", "value", "Domain", Importance.Normal, ["a", "b", "c", "d", "e"], TestContext.Current.CancellationToken));
    }

    /// <summary>Exactly 4 tags is accepted (upper boundary).</summary>
    [Fact]
    public async Task MemorizeNowAsync_ExactlyFourTags_Succeeds()
    {
        var store = this.MakeStore();
        var ex = await Xunit.Record.ExceptionAsync(() =>
            store.MemorizeNowAsync("u1", "s1", "Title", "value", "Domain", Importance.Normal, ["a", "b", "c", "d"], TestContext.Current.CancellationToken));
        Assert.Null(ex);
    }

    /// <summary>An empty tags array is accepted (0 tags is valid).</summary>
    [Fact]
    public async Task MemorizeNowAsync_EmptyTagsArray_Succeeds()
    {
        var store = this.MakeStore();
        var ex = await Xunit.Record.ExceptionAsync(() =>
            store.MemorizeNowAsync("u1", "s1", "Title", "value", "Domain", Importance.Normal, [], TestContext.Current.CancellationToken));
        Assert.Null(ex);
    }

    /// <summary>An <see cref="Importance"/> value outside the defined enum members throws <see cref="ArgumentException"/>.</summary>
    [Fact]
    public async Task MemorizeNowAsync_InvalidImportanceValue_ThrowsArgumentException()
    {
        var store = this.MakeStore();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.MemorizeNowAsync("u1", "s1", "Title", "value", "Domain", (Importance)999, [], TestContext.Current.CancellationToken));
    }

    // ── importance mapping ──────────────────────────────────────────────────

    /// <summary>High/Normal/Low map to the documented 0.9/0.6/0.3 double values on the persisted record.</summary>
    [Theory]
    [InlineData(Importance.High, 0.9)]
    [InlineData(Importance.Normal, 0.6)]
    [InlineData(Importance.Low, 0.3)]
    public async Task MemorizeNowAsync_MapsImportanceToExpectedDouble(Importance importance, double expected)
    {
        var ct = TestContext.Current.CancellationToken;
        var store = this.MakeStore();

        string compositeKey = await store.MemorizeNowAsync("u1", "s1", "Some Title", "value", "domain", importance, [], ct);
        var (domain, key) = SplitCompositeKey(compositeKey);

        var record = await store.GetByKeyAsync("u1", null, domain, key, ct);

        Assert.NotNull(record);
        Assert.Equal(expected, record.Importance);
    }

    // ── key derivation ──────────────────────────────────────────────────────

    /// <summary>The composite key is <c>{lowercased-domain}|{slugified-title}</c>, matching the design spec's worked example.</summary>
    [Fact]
    public async Task MemorizeNowAsync_ReturnsCompositeKey_OfLowercasedDomainAndSlugifiedTitle()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = this.MakeStore();

        string result = await store.MemorizeNowAsync(
            userId: "u1",
            sessionId: "session-1",
            title: "Python 3.10 Async Perf",
            value: "Python 3.10+ is 40% faster.",
            domain: "Performance",
            importance: Importance.High,
            tags: ["async", "startup"],
            ct: ct);

        Assert.Equal("performance|python-310-async-perf", result);
    }

    /// <summary>The persisted record's <c>Domain</c> is lowercased and <c>Key</c> is the bare slug (the composite key is a caller-facing concatenation, not stored verbatim as one column).</summary>
    [Fact]
    public async Task MemorizeNowAsync_DomainIsCaseFolded_ToLowercase()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = this.MakeStore();

        string compositeKey = await store.MemorizeNowAsync("u1", "s1", "Some Fact", "value", "MixedCaseDomain", Importance.Normal, [], ct);
        var (domain, key) = SplitCompositeKey(compositeKey);

        Assert.Equal("mixedcasedomain", domain);

        var record = await store.GetByKeyAsync("u1", null, domain, key, ct);
        Assert.NotNull(record);
        Assert.Equal("mixedcasedomain", record.Domain);
    }

    // ── provenance & scope ───────────────────────────────────────────────────

    /// <summary>The persisted record's <c>Source</c> is <see cref="MemorySource.AgentSignaled"/>.</summary>
    [Fact]
    public async Task MemorizeNowAsync_SetsSourceToAgentSignaled()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = this.MakeStore();

        string compositeKey = await store.MemorizeNowAsync("u1", "s1", "Some Fact", "value", "Domain", Importance.Normal, [], ct);
        var (domain, key) = SplitCompositeKey(compositeKey);

        var record = await store.GetByKeyAsync("u1", null, domain, key, ct);

        Assert.NotNull(record);
        Assert.Equal(MemorySource.AgentSignaled, record.Source);
    }

    /// <summary>The persisted record's <c>SessionId</c> is null (Global scope) even though a session id was supplied to the call.</summary>
    [Fact]
    public async Task MemorizeNowAsync_SetsSessionIdToNull_GlobalScope()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = this.MakeStore();

        string compositeKey = await store.MemorizeNowAsync("u1", "session-xyz", "Some Fact", "value", "Domain", Importance.Normal, [], ct);
        var (domain, key) = SplitCompositeKey(compositeKey);

        var record = await store.GetByKeyAsync("u1", null, domain, key, ct);

        Assert.NotNull(record);
        Assert.Null(record.SessionId);
    }

    // ── persistence side effects ─────────────────────────────────────────────

    /// <summary>The record persisted via <c>MemorizeNowAsync</c> has a non-empty embedding (generated by the delegated <c>UpsertAsync</c>).</summary>
    [Fact]
    public async Task MemorizeNowAsync_PersistsRecordWithEmbedding()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = this.MakeStore();

        string compositeKey = await store.MemorizeNowAsync("u1", "s1", "Some Fact", "value", "Domain", Importance.Normal, [], ct);
        var (domain, key) = SplitCompositeKey(compositeKey);

        var record = await store.GetByKeyAsync("u1", null, domain, key, ct);

        Assert.NotNull(record);
        Assert.False(record.Embedding.IsEmpty);
    }

    /// <summary>Calling <c>MemorizeNowAsync</c> bumps <c>LastWrittenAtAsync</c> for the user, as every write path does.</summary>
    [Fact]
    public async Task MemorizeNowAsync_BumpsLastWrittenAt()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = this.MakeStore();
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        await store.MemorizeNowAsync("u-lw", "s1", "Some Fact", "value", "Domain", Importance.Low, [], ct);

        var lastWritten = await store.LastWrittenAtAsync("u-lw", ct);
        Assert.NotNull(lastWritten);
        Assert.True(lastWritten >= before);
    }

    // ── idempotency ──────────────────────────────────────────────────────────

    /// <summary>
    /// Calling <c>MemorizeNowAsync</c> twice with the same domain+title (and thus the same derived key)
    /// silently overwrites — exactly one row survives, holding the second call's content — rather than
    /// erroring on the duplicate (Design.md's stated idempotency contract).
    /// </summary>
    [Fact]
    public async Task MemorizeNowAsync_CalledTwiceWithSameDomainAndTitle_OverwritesSingleRecord()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = this.MakeStore();
        const string userId = "u-idem";

        string firstKey = await store.MemorizeNowAsync(userId, "session-1", "Async Perf", "First value", "Performance", Importance.High, [], ct);
        string secondKey = await store.MemorizeNowAsync(userId, "session-2", "Async Perf", "Second value", "Performance", Importance.Low, ["updated"], ct);

        Assert.Equal(firstKey, secondKey);

        var all = await store.GetAllForUserAsync(userId, ct);
        var matching = all.Where(r => r.Domain == "performance" && r.Key == "async-perf").ToList();

        Assert.Single(matching);
        Assert.Equal("Second value", matching[0].Value);
        Assert.Equal(0.3, matching[0].Importance);
        Assert.Equal(MemorySource.AgentSignaled, matching[0].Source);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private PostgresMemoryStore MakeStore() =>
        new(this._dataSource, TestHelpers.DeterministicEmbedder(1536), Options.Create(new MemoryOptions()), NullLogger<PostgresMemoryStore>.Instance);

    private static (string Domain, string Key) SplitCompositeKey(string compositeKey)
    {
        string[] parts = compositeKey.Split('|', 2);
        return (parts[0], parts[1]);
    }
}
