namespace Agency.Ingestion.Test;

using Agency.VectorStore.Common;
using Moq;
using System.Runtime.CompilerServices;

/// <summary>
/// DefaultIngestionPipeline orchestrates the full Load → Split → Store flow.
/// All three dependencies (IDocumentLoader, ITextSplitter, IKVStore) are mocked
/// with Moq so tests are deterministic and free of I/O. A shared StringConverter
/// delegate maps each Document chunk to its Content string, mirroring the most
/// common real-world usage (storing raw text in IKVStore&lt;string&gt;).
/// </summary>
public sealed class DefaultIngestionPipelineTests
{
    private static readonly Func<Document, string> StringConverter = doc => doc.Content;

    /// <summary>
    /// The pipeline must reject null dependencies at call time rather than
    /// discovering them later inside Parallel.ForEachAsync, where the exception
    /// context would be harder to diagnose.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_NullLoader_ThrowsArgumentNullException()
    {
        var pipeline = new DefaultIngestionPipeline<string>(StringConverter);
        var store = new Mock<IKVStore>().Object;
        var splitter = new Mock<ITextSplitter>().Object;

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => pipeline.ExecuteAsync(null!, splitter, store));
    }

    /// <summary>
    /// Same null-guard requirement as loader — a null splitter would cause a
    /// NullReferenceException mid-pipeline with no clear indication that the
    /// splitter was missing.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_NullSplitter_ThrowsArgumentNullException()
    {
        var pipeline = new DefaultIngestionPipeline<string>(StringConverter);
        var store = new Mock<IKVStore>().Object;
        var loader = new Mock<IDocumentLoader>().Object;

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => pipeline.ExecuteAsync(loader, null!, store));
    }

    /// <summary>
    /// A null store means no data would ever be persisted. Failing fast here
    /// prevents a silent run that produces a successful-looking IngestionResult
    /// while having stored nothing.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_NullStore_ThrowsArgumentNullException()
    {
        var pipeline = new DefaultIngestionPipeline<string>(StringConverter);
        var loader = new Mock<IDocumentLoader>().Object;
        var splitter = new Mock<ITextSplitter>().Object;

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => pipeline.ExecuteAsync(loader, splitter, null!));
    }

    /// <summary>
    /// An empty document source is valid (e.g. the directory was just cleared).
    /// The pipeline must return a result with zero counts rather than hanging or
    /// throwing, and IsSuccess must be true because nothing failed.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_NoDocuments_ReturnsZeroSucceededAndFailed()
    {
        var pipeline = new DefaultIngestionPipeline<string>(StringConverter, maxDegreeOfParallelism: 1);
        var (loader, splitter, store) = CreateMocks([], _ => []);

        var result = await pipeline.ExecuteAsync(loader, splitter, store);

        Assert.Equal(0, result.Succeeded);
        Assert.Equal(0, result.Failed);
        Assert.True(result.IsSuccess);
    }

    /// <summary>
    /// The simplest success path: one document, one chunk, one upsert.
    /// Verifies that the counter increments by one and that no failure is recorded.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_SingleDocument_SingleChunk_ReturnsOneSucceeded()
    {
        var doc = new Document("content", "source1");
        var pipeline = new DefaultIngestionPipeline<string>(StringConverter, maxDegreeOfParallelism: 1);
        var (loader, splitter, store) = CreateMocks([doc], d => [d]);

        var result = await pipeline.ExecuteAsync(loader, splitter, store);

        Assert.Equal(1, result.Succeeded);
        Assert.Equal(0, result.Failed);
    }

    /// <summary>
    /// Succeeded counts individual chunks, not documents. A splitter that
    /// produces two chunks from one document must result in Succeeded == 2
    /// because each chunk is a separate UpsertAsync call.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_SingleDocument_TwoChunks_ReturnsTwoSucceeded()
    {
        var doc = new Document("content", "source1");
        var chunk1 = doc with { Content = "chunk 1" };
        var chunk2 = doc with { Content = "chunk 2" };
        var pipeline = new DefaultIngestionPipeline<string>(StringConverter, maxDegreeOfParallelism: 1);
        var (loader, splitter, store) = CreateMocks([doc], _ => [chunk1, chunk2]);

        var result = await pipeline.ExecuteAsync(loader, splitter, store);

        Assert.Equal(2, result.Succeeded);
        Assert.Equal(0, result.Failed);
    }

    /// <summary>
    /// If the store throws (e.g. database unavailable), the pipeline must catch
    /// the exception, increment Failed, and continue rather than aborting all
    /// remaining chunks. This implements the partial-success contract described
    /// in the spec.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_UpsertThrows_IncrementsFailedCount()
    {
        var doc = new Document("content", "source1");
        var pipeline = new DefaultIngestionPipeline<string>(StringConverter, maxDegreeOfParallelism: 1);
        var storeMock = new Mock<IKVStore>();
        storeMock
            .Setup(s => s.UpsertAsync<string>(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IDictionary<string, object>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("store error"));

        var loaderMock = new Mock<IDocumentLoader>();
        loaderMock.Setup(l => l.LoadAsync(It.IsAny<CancellationToken>()))
            .Returns(ToAsyncEnumerable([doc]));

        var splitterMock = new Mock<ITextSplitter>();
        splitterMock.Setup(s => s.Split(doc)).Returns([doc]);

        var result = await pipeline.ExecuteAsync(loaderMock.Object, splitterMock.Object, storeMock.Object);

        Assert.Equal(0, result.Succeeded);
        Assert.Equal(1, result.Failed);
        Assert.False(result.IsSuccess);
    }

    /// <summary>
    /// When a chunk fails, its key must appear in FailedKeys so callers can log
    /// the exact keys that need to be retried without re-ingesting the entire
    /// document set.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_UpsertThrows_AddsKeyToFailedKeys()
    {
        var doc = new Document("content", "my-source");
        var pipeline = new DefaultIngestionPipeline<string>(StringConverter, maxDegreeOfParallelism: 1);
        var storeMock = new Mock<IKVStore>();
        storeMock
            .Setup(s => s.UpsertAsync<string>(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IDictionary<string, object>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("error"));

        var loaderMock = new Mock<IDocumentLoader>();
        loaderMock.Setup(l => l.LoadAsync(It.IsAny<CancellationToken>()))
            .Returns(ToAsyncEnumerable([doc]));

        var splitterMock = new Mock<ITextSplitter>();
        splitterMock.Setup(s => s.Split(doc)).Returns([doc]);

        var result = await pipeline.ExecuteAsync(loaderMock.Object, splitterMock.Object, storeMock.Object);

        Assert.NotNull(result.FailedKeys);
        Assert.Contains("my-source:chunk:0", result.FailedKeys);
    }

    /// <summary>
    /// In the success path, FailedKeys must be null (not an empty list) to avoid
    /// unnecessary allocation and to allow callers to use a clean null check
    /// rather than checking <c>.Count &gt; 0</c>.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_FailedKeys_NullWhenNoFailures()
    {
        var doc = new Document("content", "source1");
        var pipeline = new DefaultIngestionPipeline<string>(StringConverter, maxDegreeOfParallelism: 1);
        var (loader, splitter, store) = CreateMocks([doc], d => [d]);

        var result = await pipeline.ExecuteAsync(loader, splitter, store);

        Assert.Null(result.FailedKeys);
    }

    /// <summary>
    /// The key passed to UpsertAsync must follow the "{sourceId}:chunk:{index}"
    /// pattern. This key is used for conflict resolution (ON CONFLICT in Postgres)
    /// and appears in FailedKeys — callers depend on the exact format for
    /// targeted retries and idempotent re-ingestion.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_KeyPattern_MatchesSourceIdChunkIndex()
    {
        var doc = new Document("content", "my-source");
        var pipeline = new DefaultIngestionPipeline<string>(StringConverter, maxDegreeOfParallelism: 1);
        var storeMock = new Mock<IKVStore>();
        var loaderMock = new Mock<IDocumentLoader>();
        loaderMock.Setup(l => l.LoadAsync(It.IsAny<CancellationToken>()))
            .Returns(ToAsyncEnumerable([doc]));
        var splitterMock = new Mock<ITextSplitter>();
        splitterMock.Setup(s => s.Split(doc)).Returns([doc]);

        await pipeline.ExecuteAsync(loaderMock.Object, splitterMock.Object, storeMock.Object);

        storeMock.Verify(s => s.UpsertAsync<string>(
            "my-source:chunk:0",
            It.IsAny<string>(),
            It.IsAny<IDictionary<string, object>?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// source_file metadata links a chunk in the vector store back to its origin.
    /// Without it, a SearchHit result has no way to tell the caller which file
    /// the matched text came from, breaking the lineage chain.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_InjectsSourceFileMetadata()
    {
        var doc = new Document("content", "file.md");
        var pipeline = new DefaultIngestionPipeline<string>(StringConverter, maxDegreeOfParallelism: 1);
        IDictionary<string, object>? capturedMetadata = null;
        var storeMock = new Mock<IKVStore>();
        storeMock
            .Setup(s => s.UpsertAsync<string>(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IDictionary<string, object>?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IDictionary<string, object>?, CancellationToken>(
                (_, _, meta, _) => capturedMetadata = meta)
            .Returns(Task.CompletedTask);

        var loaderMock = new Mock<IDocumentLoader>();
        loaderMock.Setup(l => l.LoadAsync(It.IsAny<CancellationToken>()))
            .Returns(ToAsyncEnumerable([doc]));
        var splitterMock = new Mock<ITextSplitter>();
        splitterMock.Setup(s => s.Split(doc)).Returns([doc]);

        await pipeline.ExecuteAsync(loaderMock.Object, splitterMock.Object, storeMock.Object);

        Assert.NotNull(capturedMetadata);
        Assert.Equal("file.md", capturedMetadata["source_file"]);
    }

    /// <summary>
    /// chunk_index metadata must be 0-based and increment for each chunk within
    /// a document. Callers use it to reconstruct document order from the vector
    /// store and to correlate chunk keys with splitter output. An incorrect
    /// index would make ordered reconstruction impossible.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_InjectsChunkIndexMetadata()
    {
        var doc = new Document("content", "src");
        var chunk1 = doc with { Content = "c1" };
        var chunk2 = doc with { Content = "c2" };
        var pipeline = new DefaultIngestionPipeline<string>(StringConverter, maxDegreeOfParallelism: 1);
        var capturedMetadata = new List<IDictionary<string, object>?>();
        var storeMock = new Mock<IKVStore>();
        storeMock
            .Setup(s => s.UpsertAsync<string>(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IDictionary<string, object>?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IDictionary<string, object>?, CancellationToken>(
                (_, _, meta, _) => capturedMetadata.Add(meta))
            .Returns(Task.CompletedTask);

        var loaderMock = new Mock<IDocumentLoader>();
        loaderMock.Setup(l => l.LoadAsync(It.IsAny<CancellationToken>()))
            .Returns(ToAsyncEnumerable([doc]));
        var splitterMock = new Mock<ITextSplitter>();
        splitterMock.Setup(s => s.Split(doc)).Returns([chunk1, chunk2]);

        await pipeline.ExecuteAsync(loaderMock.Object, splitterMock.Object, storeMock.Object);

        Assert.Equal(2, capturedMetadata.Count);
        Assert.Equal(0, (int)capturedMetadata[0]!["chunk_index"]);
        Assert.Equal(1, (int)capturedMetadata[1]!["chunk_index"]);
    }

    /// <summary>
    /// ingested_at records when each chunk was written to the store. It is used
    /// by SearchHit.RecencyMinutes/RecencyHours to bias search results toward
    /// recently ingested content. The value must fall between the test's before
    /// and after timestamps to confirm it reflects the actual ingestion moment.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_InjectsIngestedAtMetadata()
    {
        var doc = new Document("content", "src");
        var before = DateTimeOffset.UtcNow;
        var pipeline = new DefaultIngestionPipeline<string>(StringConverter, maxDegreeOfParallelism: 1);
        IDictionary<string, object>? capturedMetadata = null;
        var storeMock = new Mock<IKVStore>();
        storeMock
            .Setup(s => s.UpsertAsync<string>(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IDictionary<string, object>?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IDictionary<string, object>?, CancellationToken>(
                (_, _, meta, _) => capturedMetadata = meta)
            .Returns(Task.CompletedTask);

        var loaderMock = new Mock<IDocumentLoader>();
        loaderMock.Setup(l => l.LoadAsync(It.IsAny<CancellationToken>()))
            .Returns(ToAsyncEnumerable([doc]));
        var splitterMock = new Mock<ITextSplitter>();
        splitterMock.Setup(s => s.Split(doc)).Returns([doc]);

        await pipeline.ExecuteAsync(loaderMock.Object, splitterMock.Object, storeMock.Object);
        var after = DateTimeOffset.UtcNow;

        Assert.NotNull(capturedMetadata);
        var ingestedAt = (DateTimeOffset)capturedMetadata["ingested_at"];
        Assert.True(ingestedAt >= before && ingestedAt <= after);
    }

    /// <summary>
    /// Helper: builds three aligned mocks (loader, splitter, store) from a
    /// document list and a splitter function. Used by the simpler tests that do
    /// not need to capture callbacks or verify specific call arguments.
    /// </summary>
    private static (IDocumentLoader, ITextSplitter, IKVStore) CreateMocks(
        IEnumerable<Document> documents,
        Func<Document, IEnumerable<Document>> splitterFunc)
    {
        var loaderMock = new Mock<IDocumentLoader>();
        loaderMock.Setup(l => l.LoadAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) => ToAsyncEnumerable(documents, ct));

        var splitterMock = new Mock<ITextSplitter>();
        splitterMock.Setup(s => s.Split(It.IsAny<Document>()))
            .Returns((Document d) => splitterFunc(d));

        var storeMock = new Mock<IKVStore>();
        storeMock
            .Setup(s => s.UpsertAsync<string>(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IDictionary<string, object>?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return (loaderMock.Object, splitterMock.Object, storeMock.Object);
    }

    /// <summary>
    /// Helper: wraps a synchronous IEnumerable as an IAsyncEnumerable so that
    /// Moq-backed IDocumentLoader.LoadAsync returns can simulate the async
    /// streaming behaviour of a real loader without requiring actual I/O.
    /// <c>await Task.Yield()</c> gives Parallel.ForEachAsync a chance to schedule
    /// concurrently even within a single test.
    /// </summary>
    private static async IAsyncEnumerable<Document> ToAsyncEnumerable(
        IEnumerable<Document> docs,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var doc in docs)
        {
            ct.ThrowIfCancellationRequested();
            yield return doc;
            await Task.Yield();
        }
    }
}
