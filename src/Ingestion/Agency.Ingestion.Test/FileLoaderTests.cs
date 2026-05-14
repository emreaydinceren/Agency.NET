namespace Agency.Ingestion.Test;

using Agency.Ingestion.FileSystem;

/// <summary>
/// FileLoader turns a single file path into an <see cref="IAsyncEnumerable{T}"/> of Document.
/// The internal BuildDocument helper is tested directly (via InternalsVisibleTo)
/// to verify metadata construction without touching the filesystem, keeping
/// those tests fast and deterministic. The LoadAsync tests use a real temp file
/// to prove the end-to-end I/O path works correctly.
/// </summary>
public sealed class FileLoaderTests
{
    /// <summary>
    /// The document's Content must be exactly the string passed in — no trimming,
    /// encoding change, or truncation. Downstream splitters and embedders rely on
    /// the verbatim text.
    /// </summary>
    [Fact]
    public void BuildDocument_SetsContent()
    {
        var doc = FileLoader.BuildDocument(@"C:\docs\file.txt", "hello world");

        Assert.Equal("hello world", doc.Content);
    }

    /// <summary>
    /// SourceId is the pipeline's provenance key. It must equal the full file
    /// path so that metadata written to the vector store (source_file) and the
    /// chunk key ({sourceId}:chunk:{i}) both point back to the original file.
    /// </summary>
    [Fact]
    public void BuildDocument_SetsSourceIdToFilePath()
    {
        var doc = FileLoader.BuildDocument(@"C:\docs\file.txt", "content");

        Assert.Equal(@"C:\docs\file.txt", doc.SourceId);
    }

    /// <summary>
    /// The file_path metadata entry is the full path used for debugging and
    /// audit trails. SearchHit results surfaced to the user should be able to
    /// show which exact file a chunk came from.
    /// </summary>
    [Fact]
    public void BuildDocument_SetsFilePathMetadata()
    {
        var doc = FileLoader.BuildDocument(@"C:\docs\file.txt", "content");

        Assert.Equal(@"C:\docs\file.txt", doc.Metadata!["file_path"]);
    }

    /// <summary>
    /// The file_name metadata entry holds just the filename without the directory.
    /// It is used in display contexts (e.g. search result previews) where the
    /// full path would be too verbose.
    /// </summary>
    [Fact]
    public void BuildDocument_SetsFileNameMetadata()
    {
        var doc = FileLoader.BuildDocument(Path.Combine("docs", "file.txt"), "content");

        Assert.Equal("file.txt", doc.Metadata!["file_name"]);
    }

    /// <summary>
    /// The file_extension metadata entry is what SemanticKernelTextSplitter reads
    /// to decide whether to use the markdown or plain-text TextChunker path.
    /// If this is wrong or missing, .md files would be split as plain text
    /// and lose structure-aware chunking.
    /// </summary>
    [Fact]
    public void BuildDocument_SetsFileExtensionMetadata()
    {
        var doc = FileLoader.BuildDocument(@"C:\docs\readme.md", "content");

        Assert.Equal(".md", doc.Metadata!["file_extension"]);
    }

    /// <summary>
    /// FileLoader targets a single file, so LoadAsync must yield exactly one
    /// document. Yielding zero would silently drop content; yielding more
    /// than one would duplicate it in the vector store.
    /// </summary>
    [Fact]
    public async Task LoadAsync_SingleFile_YieldsOneDocument()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, "test content", cancellationToken: TestContext.Current.CancellationToken);
            var loader = new FileLoader(tempFile);

            var docs = new List<Document>();
            await foreach (var doc in loader.LoadAsync(TestContext.Current.CancellationToken))
            {
                docs.Add(doc);
            }

            Assert.Single(docs);
            Assert.Equal("test content", docs[0].Content);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    /// <summary>
    /// Verifies that the content of the yielded document matches what was
    /// written to disk. An encoding mismatch or partial read would corrupt
    /// the text that eventually gets embedded and stored.
    /// </summary>
    [Fact]
    public async Task LoadAsync_ReadsCorrectContent()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, "expected content", cancellationToken: TestContext.Current.CancellationToken );
            var loader = new FileLoader(tempFile);

            Document? doc = null;
            await foreach (var d in loader.LoadAsync(TestContext.Current.CancellationToken))
            {
                doc = d;
            }

            Assert.NotNull(doc);
            Assert.Equal("expected content", doc.Content);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
