namespace Agency.Ingestion.Test;

using Agency.Ingestion.FileSystem;

/// <summary>
/// DirectoryLoader recursively walks a directory and streams each matching file
/// as a Document. Tests use a real temporary directory (created in the constructor,
/// deleted in Dispose) so that glob matching and recursive traversal are exercised
/// against actual filesystem behaviour rather than mocked calls.
/// </summary>
public sealed class DirectoryLoaderTests : IDisposable
{
    private readonly string _tempDir;

    public DirectoryLoaderTests()
    {
        this._tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(this._tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(this._tempDir))
        {
            Directory.Delete(this._tempDir, recursive: true);
        }
    }

    /// <summary>
    /// An empty directory is a valid input (e.g. a freshly created docs folder).
    /// The loader must yield zero documents rather than throw, so the pipeline
    /// completes cleanly with Succeeded=0 rather than crashing.
    /// </summary>
    [Fact]
    public async Task LoadAsync_EmptyDirectory_YieldsNoDocuments()
    {
        var loader = new DirectoryLoader(this._tempDir);

        var docs = await CollectAsync(loader);

        Assert.Empty(docs);
    }

    /// <summary>
    /// The basic happy path: one file in the directory produces one document
    /// with the file's content intact. This guards against off-by-one errors
    /// (yielding zero or two) and content corruption.
    /// </summary>
    [Fact]
    public async Task LoadAsync_SingleMdFile_YieldsOneDocument()
    {
        await File.WriteAllTextAsync(Path.Combine(this._tempDir, "doc.md"), "# Hello");
        var loader = new DirectoryLoader(this._tempDir);

        var docs = await CollectAsync(loader);

        Assert.Single(docs);
        Assert.Equal("# Hello", docs[0].Content);
    }

    /// <summary>
    /// Each file must produce exactly one document. If the loader de-duplicated
    /// or skipped files, chunks from those files would be missing from the
    /// vector store without any error being raised.
    /// </summary>
    [Fact]
    public async Task LoadAsync_MultipleFiles_YieldsAllDocuments()
    {
        await File.WriteAllTextAsync(Path.Combine(this._tempDir, "a.md"), "a");
        await File.WriteAllTextAsync(Path.Combine(this._tempDir, "b.md"), "b");
        await File.WriteAllTextAsync(Path.Combine(this._tempDir, "c.md"), "c");
        var loader = new DirectoryLoader(this._tempDir);

        var docs = await CollectAsync(loader);

        Assert.Equal(3, docs.Count);
    }

    /// <summary>
    /// The searchPattern parameter is the primary filter — e.g. "*.md" should
    /// not pick up ".txt" files that happen to sit in the same directory.
    /// Ingesting unintended file types could pollute the vector store with
    /// non-document content (e.g. binary or config files).
    /// </summary>
    [Fact]
    public async Task LoadAsync_OnlyMatchesPattern()
    {
        await File.WriteAllTextAsync(Path.Combine(this._tempDir, "doc.md"), "markdown");
        await File.WriteAllTextAsync(Path.Combine(this._tempDir, "doc.txt"), "text");
        var loader = new DirectoryLoader(this._tempDir, "*.md");

        var docs = await CollectAsync(loader);

        Assert.Single(docs);
        Assert.Equal("markdown", docs[0].Content);
    }

    /// <summary>
    /// Docs are typically nested under category subdirectories (e.g. docs/api/,
    /// docs/guides/). The loader must traverse all subdirectories so that the
    /// full documentation set is ingested without requiring a loader per folder.
    /// </summary>
    [Fact]
    public async Task LoadAsync_NestedDirectory_YieldsFilesRecursively()
    {
        var subDir = Path.Combine(this._tempDir, "sub");
        Directory.CreateDirectory(subDir);
        await File.WriteAllTextAsync(Path.Combine(this._tempDir, "root.md"), "root");
        await File.WriteAllTextAsync(Path.Combine(subDir, "nested.md"), "nested");
        var loader = new DirectoryLoader(this._tempDir);

        var docs = await CollectAsync(loader);

        Assert.Equal(2, docs.Count);
    }

    /// <summary>
    /// The file_extension metadata key is what SemanticKernelTextSplitter reads
    /// to choose between SplitMarkdownParagraphs and SplitPlainTextParagraphs.
    /// Without this metadata entry, all markdown documents would be split as
    /// plain text and lose header-aware chunking.
    /// </summary>
    [Fact]
    public async Task LoadAsync_SetsFileExtensionMetadata()
    {
        await File.WriteAllTextAsync(Path.Combine(this._tempDir, "readme.md"), "content");
        var loader = new DirectoryLoader(this._tempDir);

        var docs = await CollectAsync(loader);

        Assert.Equal(".md", docs[0].Metadata!["file_extension"]);
    }

    /// <summary>
    /// When the caller cancels mid-stream (e.g. application shutdown or timeout),
    /// the loader must propagate the cancellation rather than continue reading
    /// files. An unresponsive loader would block graceful shutdown and waste I/O.
    /// </summary>
    [Fact]
    public async Task LoadAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        await File.WriteAllTextAsync(Path.Combine(this._tempDir, "a.md"), "a");
        await File.WriteAllTextAsync(Path.Combine(this._tempDir, "b.md"), "b");
        var loader = new DirectoryLoader(this._tempDir);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in loader.LoadAsync(cts.Token))
            {
            }
        });
    }

    private static async Task<List<Document>> CollectAsync(IDocumentLoader loader)
    {
        var result = new List<Document>();
        await foreach (var doc in loader.LoadAsync())
        {
            result.Add(doc);
        }
        return result;
    }
}
