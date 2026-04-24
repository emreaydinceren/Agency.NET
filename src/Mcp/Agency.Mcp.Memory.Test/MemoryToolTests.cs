using Agency.KeyValueStore.Common;
using Moq;

namespace Agency.Mcp.Memory.Test;

/// <summary>
/// Unit tests for <see cref="MemoryTool"/> using a mocked <see cref="IKVStore"/>.
/// </summary>
public sealed class MemoryToolTests
{
    private readonly Mock<IKVStore> _kvStoreMock;
    private readonly MemoryTool _tool;

    /// <summary>
    /// Initializes a new instance of <see cref="MemoryToolTests"/> with a fresh mock store.
    /// </summary>
    public MemoryToolTests()
    {
        this._kvStoreMock = new Mock<IKVStore>();
        this._tool = new MemoryTool(this._kvStoreMock.Object);
    }

    // ── Memorize ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A valid record with all fields populated should invoke UpsertAsync with the composite key and all mirrored metadata fields.
    /// </summary>
    [Fact]
    public async Task Memorize_ValidRecord_UpsertsWithCompositeKeyAndMirroredMetadata()
    {
        var record = new MemoryRecord
        {
            Scope = new MemoryScope { UserId = "u1", SessionId = "s1" },
            Domain = "notes",
            Key = "k1",
            Value = "hello",
            Tags = ["a", "b"]
        };

        this._kvStoreMock
            .Setup(s => s.UpsertAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IDictionary<string, object>?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _ = await this._tool.Memorize(record);

        this._kvStoreMock.Verify(s => s.UpsertAsync(
            "u1",
            "s1",
            "notes|k1",
            "hello",
            It.Is<IDictionary<string, object>?>(d =>
                d != null &&
                d.ContainsKey("domain") &&
                d.ContainsKey("key") &&
                d.ContainsKey("tags")),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// A null Scope should return an error string and never call UpsertAsync.
    /// </summary>
    [Fact]
    public async Task Memorize_NullScope_ReturnsErrorWithoutCallingStore()
    {
        var record = new MemoryRecord
        {
            Scope = null,
            Domain = "notes",
            Key = "k1",
            Value = "hello"
        };

        string result = await this._tool.Memorize(record);

        Assert.Contains("Error", result, StringComparison.OrdinalIgnoreCase);
        this._kvStoreMock.Verify(s => s.UpsertAsync(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IDictionary<string, object>?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// An empty Domain should return an error string and never call UpsertAsync.
    /// </summary>
    [Fact]
    public async Task Memorize_EmptyDomain_ReturnsErrorWithoutCallingStore()
    {
        var record = new MemoryRecord
        {
            Scope = new MemoryScope { UserId = "u1", SessionId = "s1" },
            Domain = "",
            Key = "k1",
            Value = "hello"
        };

        string result = await this._tool.Memorize(record);

        Assert.Contains("Error", result, StringComparison.OrdinalIgnoreCase);
        this._kvStoreMock.Verify(s => s.UpsertAsync(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IDictionary<string, object>?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// An empty Key should return an error string and never call UpsertAsync.
    /// </summary>
    [Fact]
    public async Task Memorize_EmptyKey_ReturnsErrorWithoutCallingStore()
    {
        var record = new MemoryRecord
        {
            Scope = new MemoryScope { UserId = "u1", SessionId = "s1" },
            Domain = "notes",
            Key = "",
            Value = "hello"
        };

        string result = await this._tool.Memorize(record);

        Assert.Contains("Error", result, StringComparison.OrdinalIgnoreCase);
        this._kvStoreMock.Verify(s => s.UpsertAsync(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IDictionary<string, object>?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// An empty Value should return an error string and never call UpsertAsync.
    /// </summary>
    [Fact]
    public async Task Memorize_EmptyValue_ReturnsErrorWithoutCallingStore()
    {
        var record = new MemoryRecord
        {
            Scope = new MemoryScope { UserId = "u1", SessionId = "s1" },
            Domain = "notes",
            Key = "k1",
            Value = ""
        };

        string result = await this._tool.Memorize(record);

        Assert.Contains("Error", result, StringComparison.OrdinalIgnoreCase);
        this._kvStoreMock.Verify(s => s.UpsertAsync(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IDictionary<string, object>?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Forget ────────────────────────────────────────────────────────────────

    /// <summary>
    /// When DeleteAsync returns true the result should indicate the entry was removed.
    /// </summary>
    [Fact]
    public async Task Forget_ExistingEntry_ReportsRemoved()
    {
        var scope = new MemoryScope { UserId = "u1", SessionId = "s1" };

        this._kvStoreMock
            .Setup(s => s.DeleteAsync("u1", "s1", "notes|k1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        string result = await this._tool.Forget(scope, "notes", "k1");

        Assert.Contains("Removed", result, StringComparison.OrdinalIgnoreCase);
        this._kvStoreMock.Verify(s => s.DeleteAsync("u1", "s1", "notes|k1", It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// When DeleteAsync returns false the result should indicate the entry was not found.
    /// </summary>
    [Fact]
    public async Task Forget_NonExistingEntry_ReportsNotFound()
    {
        var scope = new MemoryScope { UserId = "u1", SessionId = "s1" };

        this._kvStoreMock
            .Setup(s => s.DeleteAsync("u1", "s1", "notes|missing", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        string result = await this._tool.Forget(scope, "notes", "missing");

        Assert.Contains("Not found", result, StringComparison.OrdinalIgnoreCase);
    }

    // ── ListGlobalKeys ────────────────────────────────────────────────────────

    /// <summary>
    /// SearchAsync must be called with SessionId = "*" so only user-global entries are returned.
    /// </summary>
    [Fact]
    public async Task ListGlobalKeys_QueriesGlobalSessionId()
    {
        this._kvStoreMock
            .Setup(s => s.SearchAsync<string>(It.IsAny<Query>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SearchHit<string>>());

        _ = await this._tool.ListGlobalKeys("u1");

        this._kvStoreMock.Verify(s => s.SearchAsync<string>(
            It.Is<Query>(q => q.UserId == "u1" && q.SessionId == "*"),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// Keys must be distinct and tags must be de-duplicated across all returned hits.
    /// </summary>
    [Fact]
    public async Task ListGlobalKeys_ReturnsDistinctKeysAndTags()
    {
        var hit1 = new SearchHit<string>("u1", null, "notes|k1", "v1",
            new Dictionary<string, object> { ["tags"] = new List<object> { "tagA", "tagB" } }, 0.0, DateTimeOffset.UtcNow);
        var hit2 = new SearchHit<string>("u1", null, "notes|k2", "v2",
            new Dictionary<string, object> { ["tags"] = new List<object> { "tagB", "tagC" } }, 0.0, DateTimeOffset.UtcNow);

        this._kvStoreMock
            .Setup(s => s.SearchAsync<string>(It.IsAny<Query>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SearchHit<string>> { hit1, hit2 });

        string result = await this._tool.ListGlobalKeys("u1");

        using var doc = System.Text.Json.JsonDocument.Parse(result);
        string[] keys = doc.RootElement.GetProperty("Keys").EnumerateArray().Select(e => e.GetString()!).ToArray();
        string[] tags = doc.RootElement.GetProperty("Tags").EnumerateArray().Select(e => e.GetString()!).ToArray();

        Assert.Equal(["notes|k1", "notes|k2"], keys.Order().ToArray());
        Assert.Equal(["tagA", "tagB", "tagC"], tags.Order().ToArray());
    }

    // ── Recall ────────────────────────────────────────────────────────────────

    /// <summary>
    /// When domain and key are both provided SearchAsync should be called with the full composite key.
    /// </summary>
    [Fact]
    public async Task Recall_AllFiltersProvided_UsesExactCompositeKey()
    {
        var scope = new MemoryScope { UserId = "u1", SessionId = "s1" };

        this._kvStoreMock
            .Setup(s => s.SearchAsync<string>(It.IsAny<Query>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SearchHit<string>>());

        _ = await this._tool.Recall(scope, "notes", "k1", null);

        this._kvStoreMock.Verify(s => s.SearchAsync<string>(
            It.Is<Query>(q => q.UserId == "u1" && q.SessionId == "s1" && q.Key == "notes|k1"),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// When only scope is provided (no domain/key) SearchAsync should be called with a null Key and metadata filter for userId and sessionId.
    /// </summary>
    [Fact]
    public async Task Recall_OnlyScope_UsesMetadataFilterForScope()
    {
        var scope = new MemoryScope { UserId = "u1", SessionId = "s1" };

        this._kvStoreMock
            .Setup(s => s.SearchAsync<string>(It.IsAny<Query>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SearchHit<string>>());

        _ = await this._tool.Recall(scope, null, null, null);

        this._kvStoreMock.Verify(s => s.SearchAsync<string>(
            It.Is<Query>(q =>
                q.UserId == "u1" &&
                q.SessionId == "s1" &&
                q.Key == null),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// When tags are provided the metadata filter should contain the tags array.
    /// </summary>
    [Fact]
    public async Task Recall_WithTags_AddsTagsToMetadataFilter()
    {
        var scope = new MemoryScope { UserId = "u1", SessionId = "s1" };
        string[] tags = ["important", "work"];

        this._kvStoreMock
            .Setup(s => s.SearchAsync<string>(It.IsAny<Query>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SearchHit<string>>());

        _ = await this._tool.Recall(scope, null, null, tags);

        this._kvStoreMock.Verify(s => s.SearchAsync<string>(
            It.Is<Query>(q =>
                q.UserId == "u1" &&
                q.SessionId == "s1" &&
                q.MetadataFilter != null &&
                q.MetadataFilter.ContainsKey("tags") &&
                (q.MetadataFilter["tags"] as string[]) != null &&
                ((string[])q.MetadataFilter["tags"]).SequenceEqual(tags)),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// The return value should be valid JSON containing the key and value from the search hit.
    /// </summary>
    [Fact]
    public async Task Recall_SerializesSearchHitsAsJson()
    {
        var scope = new MemoryScope { UserId = "u1", SessionId = "s1" };
        var hit = new SearchHit<string>("u1", "s1", "notes|k1", "hello", null, 0.0, DateTimeOffset.UtcNow);

        this._kvStoreMock
            .Setup(s => s.SearchAsync<string>(It.IsAny<Query>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SearchHit<string>> { hit });

        string result = await this._tool.Recall(scope, "notes", "k1", null);

        Assert.False(string.IsNullOrEmpty(result));

        using var doc = System.Text.Json.JsonDocument.Parse(result);
        var root = doc.RootElement;
        Assert.Equal(System.Text.Json.JsonValueKind.Array, root.ValueKind);
        Assert.Equal(1, root.GetArrayLength());
        var first = root[0];
        Assert.Equal("notes|k1", first.GetProperty("Key").GetString());
        Assert.Equal("hello", first.GetProperty("Value").GetString());
    }
}
