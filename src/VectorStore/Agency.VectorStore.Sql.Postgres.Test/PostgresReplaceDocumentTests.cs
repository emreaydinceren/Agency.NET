using Agency.VectorStore.Common;

namespace Agency.VectorStore.Sql.Postgres.Test;

/// <summary>
/// Tests for <see cref="PostgresKVStore.ReplaceDocumentAsync{TValue}"/> against an PostgreSQL database.
/// </summary>
[Trait("Category", "Functional")]
[Collection(PostgresKVStoreFunctionalTests.SchemaCollection)]
public sealed class PostgresReplaceDocumentTests : IClassFixture<PostgresKVStoreFunctionalTests.VectorStoreFixture>
{
    private const string UserId = "replace-user";

    private readonly PostgresKVStoreFunctionalTests.VectorStoreFixture _fixture;

    /// <summary>
    /// Creates the test class with its shared PostgreSQL fixture.
    /// </summary>
    public PostgresReplaceDocumentTests(PostgresKVStoreFunctionalTests.VectorStoreFixture fixture)
    {
        this._fixture = fixture;
    }

    /// <summary>
    /// Verifies that replacing a new document stores every chunk stamped with its <c>source_file</c>.
    /// </summary>
    [Fact]
    public async Task ReplaceDocumentAsync_NewDocument_StoresChunksWithSourceFile()
    {
        string project = this._fixture.UniqueName("rd-new");

        int deleted = await this.ReplaceAsync(project, "a.md", "one", "two", "three");

        Assert.Equal(0, deleted);
        IReadOnlyList<SearchHit<string>> hits = await this.ChunksOfAsync(project, "a.md");
        Assert.Equal(["a.md:chunk:0", "a.md:chunk:1", "a.md:chunk:2"], hits.Select(h => h.Key).Order(StringComparer.Ordinal));
        Assert.All(hits, h => Assert.Equal("a.md", h.Metadata!["source_file"]));
        Assert.Contains(hits, h => h.Value == "two");
    }

    /// <summary>
    /// Verifies that replacing a document with fewer chunks removes the old tail chunks.
    /// </summary>
    [Fact]
    public async Task ReplaceDocumentAsync_DocumentShrinks_DeletesStaleChunks()
    {
        string project = this._fixture.UniqueName("rd-shrink");
        await this.ReplaceAsync(project, "a.md", "one", "two", "three");

        int deleted = await this.ReplaceAsync(project, "a.md", "uno");

        Assert.Equal(2, deleted);
        SearchHit<string> only = Assert.Single(await this.ChunksOfAsync(project, "a.md"));
        Assert.Equal("uno", only.Value);
    }

    /// <summary>
    /// Verifies that an empty chunk list deletes the whole document.
    /// </summary>
    [Fact]
    public async Task ReplaceDocumentAsync_EmptyChunks_DeletesDocument()
    {
        string project = this._fixture.UniqueName("rd-empty");
        await this.ReplaceAsync(project, "a.md", "one", "two");

        int deleted = await this.ReplaceAsync(project, "a.md");

        Assert.Equal(2, deleted);
        Assert.Empty(await this.ChunksOfAsync(project, "a.md"));
    }

    /// <summary>
    /// Verifies that a replace leaves other documents and other projects untouched.
    /// </summary>
    [Fact]
    public async Task ReplaceDocumentAsync_OtherDocumentsAndProjects_AreUntouched()
    {
        string project = this._fixture.UniqueName("rd-iso");
        string otherProject = this._fixture.UniqueName("rd-iso-other");
        await this.ReplaceAsync(project, "a.md", "one", "two");
        await this.ReplaceAsync(project, "b.md", "bee");
        await this.ReplaceAsync(otherProject, "a.md", "other-one", "other-two");

        await this.ReplaceAsync(project, "a.md");

        Assert.Single(await this.ChunksOfAsync(project, "b.md"));
        Assert.Equal(2, (await this.ChunksOfAsync(otherProject, "a.md")).Count);
    }

    /// <summary>
    /// Verifies that duplicate chunk keys are rejected before anything is written.
    /// </summary>
    [Fact]
    public async Task ReplaceDocumentAsync_DuplicateKeys_Throws()
    {
        string project = this._fixture.UniqueName("rd-dup");
        DocumentChunk<string>[] chunks = [new("k", "one"), new("k", "two")];

        await Assert.ThrowsAsync<ArgumentException>(() => this._fixture.KVStore.ReplaceDocumentAsync(
            UserId, null, "a.md", chunks, project, TestContext.Current.CancellationToken));

        Assert.Empty(await this.ChunksOfAsync(project, "a.md"));
    }

    /// <summary>Replaces <paramref name="sourceFile"/> in <paramref name="project"/> with one chunk per value.</summary>
    private Task<int> ReplaceAsync(string project, string sourceFile, params string[] values)
    {
        DocumentChunk<string>[] chunks = values
            .Select((v, i) => new DocumentChunk<string>($"{sourceFile}:chunk:{i}", v))
            .ToArray();
        return this._fixture.KVStore.ReplaceDocumentAsync(
            UserId, null, sourceFile, chunks, project, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Returns every stored chunk of <paramref name="sourceFile"/> in <paramref name="project"/>. A non-null session
    /// id is passed because Postgres treats a null session as "every session and project of the user".
    /// </summary>
    private async Task<IReadOnlyList<SearchHit<string>>> ChunksOfAsync(string project, string sourceFile)
    {
        IReadOnlyList<SearchHit<string>> hits = await this._fixture.KVStore.SearchAsync<string>(
            new Query(UserId, "no-session", null, null, new Dictionary<string, object> { ["source_file"] = sourceFile }, 1000, true, [project]),
            TestContext.Current.CancellationToken);
        return hits.Where(h => h.Key.StartsWith(sourceFile, StringComparison.Ordinal)).ToList();
    }
}
