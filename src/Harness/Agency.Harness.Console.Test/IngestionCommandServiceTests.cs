using Agency.Harness.Console.Services;
using Agency.Ingestion;
using Agency.VectorStore.Common;
using Moq;

namespace Agency.Harness.Console.Test;

/// <summary>
/// Unit tests for <see cref="IngestionCommandService"/>, the service behind the
/// <c>/add-file</c> and <c>/add-folder</c> commands. These tests exist because a
/// user ran <c>/add-file</c> on a real, non-empty markdown file and got
/// "Ingested 1 file, 0 chunk(s)." with no indication anything had gone wrong —
/// the service discarded the pipeline's failure count entirely. The regression
/// covered here is that failures must be visible on the returned
/// <see cref="IngestionResult"/> rather than silently reported as success.
/// </summary>
public sealed class IngestionCommandServiceTests : IDisposable
{
    private readonly Mock<IVectorStore> _storeMock = new();
    private readonly Mock<ITextSplitter> _splitterMock = new();
    private readonly string _tempFile;
    private readonly string _tempDirectory;

    /// <summary>
    /// Creates a temp markdown file and a temp directory containing one, so each
    /// test has real files for the internally-constructed <c>FileLoader</c>/<c>DirectoryLoader</c> to read.
    /// </summary>
    public IngestionCommandServiceTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.md");
        File.WriteAllText(_tempFile, "# Heading\n\nSome real content to ingest.");

        _tempDirectory = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
        File.WriteAllText(Path.Combine(_tempDirectory, "doc.md"), "# Heading\n\nContent.");
    }

    /// <summary>Removes the temp file and directory created for this fixture.</summary>
    public void Dispose()
    {
        File.Delete(_tempFile);
        Directory.Delete(_tempDirectory, recursive: true);
    }

    private IngestionCommandService CreateService() => new(_storeMock.Object, _splitterMock.Object);

    /// <summary>
    /// The happy path: the splitter produces two chunks and every upsert succeeds.
    /// <see cref="IngestionResult.Succeeded"/> must count chunks, and
    /// <see cref="IngestionResult.Failed"/> must be zero.
    /// </summary>
    [Fact]
    public async Task IngestFileAsync_AllUpsertsSucceed_ReturnsSucceededWithNoFailures()
    {
        _splitterMock.Setup(s => s.Split(It.IsAny<Document>()))
            .Returns((Document d) => [d with { Content = "chunk 1" }, d with { Content = "chunk 2" }]);
        _storeMock
            .Setup(s => s.UpsertAsync<string>(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<IDictionary<string, object>?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        IngestionResult result = await CreateService().IngestFileAsync(
            _tempFile, "user1", "sess-1", null, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Succeeded);
        Assert.Equal(0, result.Failed);
        Assert.True(result.IsSuccess);
    }

    /// <summary>
    /// Reproduces the reported bug: when every chunk upsert fails (e.g. the
    /// embedding endpoint is unreachable), the result must report the failures
    /// rather than silently returning a zero count that reads as "an empty file
    /// was ingested successfully."
    /// </summary>
    [Fact]
    public async Task IngestFileAsync_AllUpsertsFail_ReturnsFailedCountNotSilentZero()
    {
        _splitterMock.Setup(s => s.Split(It.IsAny<Document>()))
            .Returns((Document d) => [d with { Content = "chunk 1" }]);
        _storeMock
            .Setup(s => s.UpsertAsync<string>(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<IDictionary<string, object>?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("embedding endpoint unreachable"));

        IngestionResult result = await CreateService().IngestFileAsync(
            _tempFile, "user1", "sess-1", null, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.Succeeded);
        Assert.Equal(1, result.Failed);
        Assert.False(result.IsSuccess);
        Assert.NotNull(result.FailureReasons);
        Assert.Contains("embedding endpoint unreachable", result.FailureReasons);
    }

    /// <summary>
    /// Mirrors the file-ingest happy path for the directory variant used by
    /// <c>/add-folder</c>, confirming the same result type flows through.
    /// </summary>
    [Fact]
    public async Task IngestDirectoryAsync_AllUpsertsSucceed_ReturnsSucceededWithNoFailures()
    {
        _splitterMock.Setup(s => s.Split(It.IsAny<Document>()))
            .Returns((Document d) => [d with { Content = "chunk 1" }]);
        _storeMock
            .Setup(s => s.UpsertAsync<string>(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<IDictionary<string, object>?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        IngestionResult result = await CreateService().IngestDirectoryAsync(
            _tempDirectory, "*.md", "user1", "sess-1", null, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Succeeded);
        Assert.Equal(0, result.Failed);
        Assert.True(result.IsSuccess);
    }
}
