using Agency.Memory.Common.Records;
using Agency.Memory.Common.Storage;
using MemoryRecord = Agency.Memory.Common.Records.Record;

namespace Agency.Memory.Common.Test;

/// <summary>
/// Abstract contract tests that any <see cref="IMemoryStore"/> implementation must satisfy.
/// Concrete test classes parameterise over a store factory.
/// </summary>
/// <remarks>
/// The contract tests are abstract here. Workstream B wires the Postgres implementation.
/// The in-memory store (if/when available) would wire a separate derived class.
/// </remarks>
public abstract class IMemoryStoreContractTests
{
    /// <summary>Creates a fresh <see cref="IMemoryStore"/> for each test.</summary>
    protected abstract Task<IMemoryStore> CreateStoreAsync();

    private static MemoryRecord MakeRecord(
        string userId,
        string? sessionId,
        string domain,
        string key,
        double importance = 0.5,
        ContentType contentType = ContentType.Fact,
        ReadOnlyMemory<float> embedding = default)
    {
        var now = DateTimeOffset.UtcNow;
        return MemoryRecord.Create(
            id: Guid.NewGuid().ToString(),
            userId: userId,
            sessionId: sessionId,
            contentType: contentType,
            domain: domain,
            key: key,
            title: $"{domain}/{key}",
            value: $"Value of {key}",
            tags: [],
            importance: importance,
            createdAt: now,
            updatedAt: now,
            embedding: embedding);
    }

    /// <summary>Upserting a new record assigns an Id and sets CreatedAt.</summary>
    [Fact]
    public async Task Upsert_NewRecord_ReturnsAssignedIdAndCreatedAtSet()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = await this.CreateStoreAsync();
        var record = MakeRecord("u1", null, "D", "K");

        var result = await store.UpsertAsync(record, ct);

        Assert.NotNull(result.Id);
        Assert.NotEqual(DateTimeOffset.MinValue, result.CreatedAt);
    }

    /// <summary>Upserting an existing upsert key preserves the Id and updates Value and UpdatedAt.</summary>
    [Fact]
    public async Task Upsert_ExistingUpsertKey_PreservesId_UpdatesValueAndUpdatedAt()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = await this.CreateStoreAsync();
        var original = MakeRecord("u1", null, "D", "K");
        var first = await store.UpsertAsync(original, ct);

        var updated = first with { Value = "updated value" };
        var second = await store.UpsertAsync(updated, ct);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal("updated value", second.Value);
        Assert.True(second.UpdatedAt >= first.UpdatedAt);
    }

    /// <summary>Searching filters by content type.</summary>
    [Fact]
    public async Task Search_FiltersByContentType()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = await this.CreateStoreAsync();
        var embedding = new float[] { 1.0f, 0.0f };
        var fact = MakeRecord("u1", null, "D", "F1", contentType: ContentType.Fact, embedding: embedding);
        var memory = MakeRecord("u1", null, "D", "M1", contentType: ContentType.Memory, embedding: embedding);

        await store.UpsertAsync(fact, ct);
        await store.UpsertAsync(memory, ct);

        var query = new SearchQuery("u1", embedding, TopK: 10, ContentType: ContentType.Fact);
        var hits = await store.SearchAsync(query, ct);

        Assert.All(hits, h => Assert.Equal(ContentType.Fact, h.Record.ContentType));
    }

    /// <summary>Searching filters by domain.</summary>
    [Fact]
    public async Task Search_FiltersByDomain()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = await this.CreateStoreAsync();
        var embedding = new float[] { 1.0f, 0.0f };
        var inDomain = MakeRecord("u1", null, "Target", "K1", embedding: embedding);
        var outDomain = MakeRecord("u1", null, "Other", "K2", embedding: embedding);

        await store.UpsertAsync(inDomain, ct);
        await store.UpsertAsync(outDomain, ct);

        var query = new SearchQuery("u1", embedding, TopK: 10, Domain: "Target");
        var hits = await store.SearchAsync(query, ct);

        Assert.All(hits, h => Assert.Equal("Target", h.Record.Domain));
    }

    /// <summary>Searching respects TopK.</summary>
    [Fact]
    public async Task Search_RespectsTopK()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = await this.CreateStoreAsync();
        var embedding = new float[] { 1.0f, 0.0f };

        for (int i = 0; i < 5; i++)
        {
            await store.UpsertAsync(MakeRecord("u1", null, "D", $"K{i}", embedding: embedding), ct);
        }

        var query = new SearchQuery("u1", embedding, TopK: 3);
        var hits = await store.SearchAsync(query, ct);

        Assert.True(hits.Count <= 3);
    }

    /// <summary>Searching returns results ordered by cosine distance (most similar first).</summary>
    [Fact]
    public async Task Search_OrdersByCosineDistance()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = await this.CreateStoreAsync();
        var queryVec = new float[] { 1.0f, 0.0f };
        var closeVec = new float[] { 0.99f, 0.14f };   // high cosine similarity
        var farVec = new float[] { 0.0f, 1.0f };       // orthogonal, low similarity

        await store.UpsertAsync(MakeRecord("u1", null, "D", "Close", embedding: closeVec), ct);
        await store.UpsertAsync(MakeRecord("u1", null, "D", "Far", embedding: farVec), ct);

        var query = new SearchQuery("u1", queryVec, TopK: 10);
        var hits = await store.SearchAsync(query, ct);

        Assert.True(hits.Count >= 2);
        Assert.True(hits[0].Similarity >= hits[1].Similarity,
            $"Expected hits ordered by similarity descending but got {hits[0].Similarity} before {hits[1].Similarity}");
    }

    /// <summary>Forgetting an existing key returns true and removes the record.</summary>
    [Fact]
    public async Task Forget_ExistingKey_ReturnsTrueAndRemovesRow()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = await this.CreateStoreAsync();
        await store.UpsertAsync(MakeRecord("u1", null, "D", "K"), ct);

        bool result = await store.ForgetAsync("u1", "D", "K", ct);
        var records = await store.GetAllForUserAsync("u1", ct);

        Assert.True(result);
        Assert.Empty(records);
    }

    /// <summary>Forgetting a missing key returns false.</summary>
    [Fact]
    public async Task Forget_MissingKey_ReturnsFalse()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = await this.CreateStoreAsync();

        bool result = await store.ForgetAsync("u1", "D", "NonExistent", ct);

        Assert.False(result);
    }

    /// <summary>ForgetMe removes all records for a user without affecting other users.</summary>
    [Fact]
    public async Task ForgetMe_RemovesAllRowsForUser_DoesNotAffectOthers()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = await this.CreateStoreAsync();
        await store.UpsertAsync(MakeRecord("u1", null, "D", "K1"), ct);
        await store.UpsertAsync(MakeRecord("u1", null, "D", "K2"), ct);
        await store.UpsertAsync(MakeRecord("u2", null, "D", "K3"), ct);

        int deleted = await store.ForgetMeAsync("u1", ct);
        var u1Records = await store.GetAllForUserAsync("u1", ct);
        var u2Records = await store.GetAllForUserAsync("u2", ct);

        Assert.Equal(2, deleted);
        Assert.Empty(u1Records);
        Assert.Single(u2Records);
    }

    /// <summary>LastWrittenAt returns null for an unknown user.</summary>
    [Fact]
    public async Task LastWrittenAt_NullForUnknownUser()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = await this.CreateStoreAsync();

        var result = await store.LastWrittenAtAsync("unknown-user", ct);

        Assert.Null(result);
    }

    /// <summary>LastWrittenAt advances on upsert.</summary>
    [Fact]
    public async Task LastWrittenAt_AdvancesOnUpsert()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = await this.CreateStoreAsync();
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        await store.UpsertAsync(MakeRecord("u1", null, "D", "K"), ct);

        var lastWritten = await store.LastWrittenAtAsync("u1", ct);
        Assert.NotNull(lastWritten);
        Assert.True(lastWritten >= before);
    }

    /// <summary>LastWrittenAt advances on ForgetAsync.</summary>
    [Fact]
    public async Task LastWrittenAt_AdvancesOnForget()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = await this.CreateStoreAsync();
        await store.UpsertAsync(MakeRecord("u1", null, "D", "K"), ct);
        var afterUpsert = await store.LastWrittenAtAsync("u1", ct);

        await Task.Delay(10, ct);
        await store.ForgetAsync("u1", "D", "K", ct);

        var afterForget = await store.LastWrittenAtAsync("u1", ct);
        Assert.NotNull(afterForget);
        Assert.True(afterForget >= afterUpsert);
    }

    /// <summary>LastWrittenAt advances on ForgetMeAsync.</summary>
    [Fact]
    public async Task LastWrittenAt_AdvancesOnForgetMe()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = await this.CreateStoreAsync();
        await store.UpsertAsync(MakeRecord("u1", null, "D", "K"), ct);
        var afterUpsert = await store.LastWrittenAtAsync("u1", ct);

        await Task.Delay(10, ct);
        await store.ForgetMeAsync("u1", ct);

        var afterForgetMe = await store.LastWrittenAtAsync("u1", ct);
        Assert.NotNull(afterForgetMe);
        Assert.True(afterForgetMe >= afterUpsert);
    }
}
