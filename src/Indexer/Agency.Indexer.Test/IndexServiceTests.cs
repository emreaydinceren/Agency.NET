namespace Agency.Indexer.Test;

/// <summary>
/// End-to-end tests for <see cref="IndexService"/> over a temporary SQLite database, wired through the
/// production composition root with a deterministic fake embedding generator.
/// </summary>
public sealed class IndexServiceTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly string _docs;
    private readonly string _database;
    private readonly FakeEmbeddingGenerator _embeddings = new();

    /// <summary>Creates a docs folder and a database path inside a fresh temporary directory.</summary>
    public IndexServiceTests()
    {
        this._docs = Path.Combine(this._dir.Path, "docs");
        this._database = Path.Combine(this._dir.Path, "db", "index.db");
        Directory.CreateDirectory(this._docs);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Deletes the temporary directory.</summary>
    public void Dispose() => this._dir.Dispose();

    /// <summary>
    /// Verifies the core delta: after indexing ten files, changing two re-embeds exactly those two and leaves
    /// the other eight untouched.
    /// </summary>
    [Fact]
    public async Task IndexAsync_TwoOfTenFilesChanged_ReembedsOnlyThoseTwo()
    {
        for (int i = 0; i < 10; i++)
        {
            this.WriteDoc($"doc{i}.md", $"Document number {i} talks about topic{i}.");
        }

        IndexService service = await Services.SqliteAsync(this._database, this._embeddings);
        IndexResult first = await service.IndexAsync(Request(root: this._docs), Ct);
        Assert.Equal(IndexStatus.Ok, first.Status);
        Assert.Equal(10, first.Added.Count);

        this._embeddings.EmbeddedInputs.Clear();
        this.WriteDoc("doc3.md", "Document three was rewritten to discuss deployment.");
        this.WriteDoc("doc7.md", "Document seven now covers releases.", touchSeconds: 5);

        IndexResult second = await service.IndexAsync(Request(), Ct);

        Assert.Equal(IndexStatus.Ok, second.Status);
        Assert.Empty(second.Added);
        Assert.Equal([Path.Combine(this._docs, "doc3.md"), Path.Combine(this._docs, "doc7.md")], second.Changed.Order(StringComparer.Ordinal));
        Assert.Empty(second.Removed);
        Assert.Equal(8, second.Unchanged);
        Assert.Equal(2, this._embeddings.EmbeddedInputs.Count);
    }

    /// <summary>Verifies that a run with nothing changed embeds nothing.</summary>
    [Fact]
    public async Task IndexAsync_NothingChanged_EmbedsNothing()
    {
        this.WriteDoc("a.md", "alpha");
        IndexService service = await Services.SqliteAsync(this._database, this._embeddings);
        await service.IndexAsync(Request(root: this._docs), Ct);
        this._embeddings.EmbeddedInputs.Clear();

        IndexResult result = await service.IndexAsync(Request(), Ct);

        Assert.Equal(1, result.Unchanged);
        Assert.Empty(this._embeddings.EmbeddedInputs);
    }

    /// <summary>Verifies that deleting a file removes it from the manifest and from search results.</summary>
    [Fact]
    public async Task IndexAsync_FileDeleted_RemovesItsChunks()
    {
        string keep = this.WriteDoc("keep.md", "kubernetes deployment guide");
        string gone = this.WriteDoc("gone.md", "kubernetes deployment notes");
        IndexService service = await Services.SqliteAsync(this._database, this._embeddings);
        await service.IndexAsync(Request(root: this._docs), Ct);

        File.Delete(gone);
        IndexResult result = await service.IndexAsync(Request(), Ct);

        Assert.Equal([gone], result.Removed);
        IReadOnlyList<SearchResultHit> hits = await service.SearchAsync("docs", "kubernetes deployment", 10, Ct);
        Assert.All(hits, h => Assert.Equal(keep, h.Path));
        var (_, files) = await service.ListAsync("docs", Ct);
        Assert.Equal([keep], files.Select(f => f.Path));
    }

    /// <summary>Verifies that a file shrinking to fewer chunks leaves no stale chunks behind.</summary>
    [Fact]
    public async Task IndexAsync_FileShrinks_LeavesNoStaleChunks()
    {
        string path = this.WriteDoc("long.md", string.Join("\n\n", Enumerable.Range(0, 30).Select(i => $"Paragraph {i} about release engineering and versioning.")));
        IndexService service = await Services.SqliteAsync(this._database, this._embeddings);
        await service.IndexAsync(Request(root: this._docs), Ct);
        var (_, before) = await service.ListAsync("docs", Ct);
        Assert.True(before[0].Chunks > 1);

        this.WriteDoc("long.md", "Short now.");
        await service.IndexAsync(Request(), Ct);

        IReadOnlyList<SearchResultHit> hits = await service.SearchAsync("docs", "release engineering versioning", 50, Ct);
        SearchResultHit only = Assert.Single(hits);
        Assert.Equal(path, only.Path);
        Assert.Equal("Short now.", only.Text);
    }

    /// <summary>Verifies that search ranks the matching document first and reports its path and chunk.</summary>
    [Fact]
    public async Task SearchAsync_ReturnsBestMatchFirst()
    {
        this.WriteDoc("release.md", "How to publish a release: tag the commit and push the tag.");
        this.WriteDoc("logging.md", "Turn on verbose logging with the debug switch.");
        this.WriteDoc("site/page.html", "<html><body><script>var x;</script><p>Database backups run nightly.</p></body></html>");
        IndexService service = await Services.SqliteAsync(this._database, this._embeddings);
        await service.IndexAsync(Request(root: this._docs), Ct);

        IReadOnlyList<SearchResultHit> releaseHits = await service.SearchAsync("docs", "publish a release tag", 3, Ct);
        IReadOnlyList<SearchResultHit> htmlHits = await service.SearchAsync("docs", "database backups nightly", 1, Ct);

        Assert.Equal(Path.Combine(this._docs, "release.md"), releaseHits[0].Path);
        Assert.Equal(0, releaseHits[0].Chunk);
        Assert.Equal("Database backups run nightly.", htmlHits[0].Text);
    }

    /// <summary>Verifies that narrowing the extension selection removes files that no longer match.</summary>
    [Fact]
    public async Task IndexAsync_ExtensionSelectionNarrowed_RemovesUnselectedFiles()
    {
        string md = this.WriteDoc("a.md", "markdown");
        string txt = this.WriteDoc("b.txt", "text");
        IndexService service = await Services.SqliteAsync(this._database, this._embeddings);
        await service.IndexAsync(Request(root: this._docs), Ct);

        IndexResult result = await service.IndexAsync(Request() with { Extensions = [".md"] }, Ct);

        Assert.Equal([txt], result.Removed);
        var (config, files) = await service.ListAsync("docs", Ct);
        Assert.Equal([".md"], config.Extensions);
        Assert.Equal([md], files.Select(f => f.Path));
    }

    /// <summary>Verifies the usage errors: no root for a new index, a different root, a different model.</summary>
    [Fact]
    public async Task IndexAsync_InvalidRequests_ThrowUsageException()
    {
        this.WriteDoc("a.md", "alpha");
        IndexService service = await Services.SqliteAsync(this._database, this._embeddings);

        await Assert.ThrowsAsync<UsageException>(() => service.IndexAsync(Request(), Ct));
        await service.IndexAsync(Request(root: this._docs), Ct);
        await Assert.ThrowsAsync<UsageException>(() => service.IndexAsync(Request(root: this._dir.Path), Ct));

        IndexService otherModel = await Services.SqliteAsync(this._database, this._embeddings, model: "other-model");
        await Assert.ThrowsAsync<UsageException>(() => otherModel.IndexAsync(Request(), Ct));
    }

    /// <summary>Verifies that a second writer is turned away while the lock is held, and that searches still work.</summary>
    [Fact]
    public async Task IndexAsync_WhileAnotherWriterHoldsTheLock_ReturnsLocked()
    {
        this.WriteDoc("a.md", "alpha beta");
        IndexService service = await Services.SqliteAsync(this._database, this._embeddings);
        await service.IndexAsync(Request(root: this._docs), Ct);

        await using (IAsyncDisposable? held = await new FileWriterLock(Path.GetFullPath(this._database)).TryAcquireAsync("docs", Ct))
        {
            Assert.NotNull(held);
            IndexResult locked = await service.IndexAsync(Request(), Ct);
            Assert.Equal(IndexStatus.Locked, locked.Status);
            Assert.NotEmpty(await service.SearchAsync("docs", "alpha", 1, Ct));
        }

        Assert.Equal(IndexStatus.Ok, (await service.IndexAsync(Request(), Ct)).Status);
    }

    /// <summary>Verifies that dropping an index removes its chunks, manifest and configuration.</summary>
    [Fact]
    public async Task DropAsync_RemovesEverything()
    {
        this.WriteDoc("a.md", "alpha");
        IndexService service = await Services.SqliteAsync(this._database, this._embeddings);
        await service.IndexAsync(Request(root: this._docs), Ct);

        DropResult dropped = await service.DropAsync("docs", wait: false, Ct);

        Assert.Equal(IndexStatus.Ok, dropped.Status);
        Assert.Equal(1, dropped.ChunksDeleted);
        Assert.Empty(await service.ListIndexesAsync(Ct));
        await Assert.ThrowsAsync<UsageException>(() => service.SearchAsync("docs", "alpha", 1, Ct));
    }

    /// <summary>Verifies progress is reported up front and after every file, ending with a completion line.</summary>
    [Fact]
    public async Task IndexAsync_WithProgress_ReportsStartAndEachFile()
    {
        this.WriteDoc("a.md", "alpha");
        this.WriteDoc("b.md", "beta");
        IndexService service = await Services.SqliteAsync(this._database, this._embeddings);
        var lines = new List<string>();

        await service.IndexAsync(Request(root: this._docs), Ct, lines.Add);

        Assert.Equal(3, lines.Count);
        Assert.StartsWith("indexing 2 files", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("indexing 1/2 files", lines[1], StringComparison.Ordinal);
        Assert.Contains("left", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("indexing 2/2 files", lines[2], StringComparison.Ordinal);
        Assert.Contains("done in", lines[2], StringComparison.Ordinal);
    }

    /// <summary>Verifies a dry run reports the delta and the real chunk count, writes nothing, and times a sample.</summary>
    [Fact]
    public async Task DryRunAsync_ReportsPlanAndWritesNothing()
    {
        for (int i = 0; i < 5; i++)
        {
            this.WriteDoc($"doc{i}.md", $"Document number {i} talks about topic{i}.");
        }

        IndexService service = await Services.SqliteAsync(this._database, this._embeddings);

        DryRunResult plan = await service.DryRunAsync(Request(root: this._docs), this._embeddings, Ct);

        Assert.Equal(5, plan.Added.Count);
        Assert.Empty(await service.ListIndexesAsync(Ct));
        Assert.True(plan.EstimatedSeconds >= 0);
        Assert.Equal(3, this._embeddings.EmbeddedInputs.Count);

        IndexResult real = await service.IndexAsync(Request(root: this._docs), Ct);
        Assert.Equal(real.ChunksWritten, plan.EstimatedChunks);
    }

    /// <summary>
    /// Verifies a failing embedding is reported per file as it happens and in the result with the reason, while the
    /// other files are still indexed and the failed one is retried by the next run.
    /// </summary>
    [Fact]
    public async Task IndexAsync_EmbeddingFails_ReportsFileAndReasonInProgressAndResult()
    {
        this.WriteDoc("good.md", "alpha document");
        string bad = this.WriteDoc("bad.md", "POISON document");
        this._embeddings.FailWhenInputContains = "POISON";
        IndexService service = await Services.SqliteAsync(this._database, this._embeddings);
        var lines = new List<string>();

        IndexResult result = await service.IndexAsync(Request(root: this._docs), Ct, lines.Add);

        Assert.Equal(IndexStatus.PartialFailure, result.Status);
        FailedFile failure = Assert.Single(result.Failed);
        Assert.Equal(bad, failure.Path);
        Assert.Contains("model is overloaded", failure.Reason, StringComparison.Ordinal);
        Assert.Contains("the operation timed out", failure.Reason, StringComparison.Ordinal);
        Assert.Contains(lines, l => l.StartsWith("FAILED ", StringComparison.Ordinal) && l.Contains(bad, StringComparison.Ordinal) && l.Contains("model is overloaded", StringComparison.Ordinal));
        Assert.Single(result.Added);

        this._embeddings.FailWhenInputContains = null;
        IndexResult retry = await service.IndexAsync(Request(), Ct);
        Assert.Equal(IndexStatus.Ok, retry.Status);
        Assert.Equal([bad], retry.Added);
    }

    /// <summary>Verifies the repeated messages of an aggregate (the SDK's "Retry failed after N tries") appear once.</summary>
    [Fact]
    public void FailureReason_AggregateOfIdenticalErrors_ListsEachMessageOnce()
    {
        var inner = new HttpRequestException("connection refused (127.0.0.1:9)");
        var aggregate = new AggregateException("Retry failed after 4 tries. (connection refused (127.0.0.1:9)) (connection refused (127.0.0.1:9))", new Exception[] { inner, new HttpRequestException("connection refused (127.0.0.1:9)") });

        Assert.Equal("Retry failed after 4 tries. -> connection refused (127.0.0.1:9)", FailureReason.Of(aggregate));
    }

    /// <summary>Verifies the run log echoes every line and appends timestamped lines to the file across runs.</summary>
    [Fact]
    public void RunLog_WithFile_EchoesAndAppendsTimestampedLines()
    {
        string path = Path.Combine(this._dir.Path, "logs", "run.log");
        var echoed = new List<string>();

        using (var first = new RunLog(path, echoed.Add))
        {
            first.Write("indexing 1/2 files");
        }

        using (var second = new RunLog(path, echoed.Add))
        {
            second.Write("FAILED a.md: boom");
        }

        string[] lines = File.ReadAllLines(path);
        Assert.Equal(["indexing 1/2 files", "FAILED a.md: boom"], echoed);
        Assert.Equal(2, lines.Length);
        Assert.EndsWith(" indexing 1/2 files", lines[0], StringComparison.Ordinal);
        Assert.True(DateTimeOffset.TryParse(lines[1][..lines[1].IndexOf(' ')], out _));
        Assert.EndsWith(" FAILED a.md: boom", lines[1], StringComparison.Ordinal);
    }

    /// <summary>Verifies the run log without a file only echoes.</summary>
    [Fact]
    public void RunLog_WithoutFile_OnlyEchoes()
    {
        var echoed = new List<string>();

        using var log = new RunLog(null, echoed.Add);
        log.Write("x");

        Assert.Equal(["x"], echoed);
    }

    private static IndexRequest Request(string? root = null) =>
        new("docs", root, null, null, FileScanner.DefaultMaxFileBytes, Wait: false);

    /// <summary>
    /// Writes a document and gives it a distinct last-write time, so a rewrite is detected even when it keeps
    /// the same size and lands within the file system's timestamp granularity.
    /// </summary>
    private string WriteDoc(string relativePath, string content, int touchSeconds = 1)
    {
        string path = this._dir.Write(Path.Combine("docs", relativePath), content);
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(touchSeconds));
        return path;
    }
}
