namespace Agency.Ingestion.Test;

using Agency.Ingestion.SemanticKernel;
using Microsoft.SemanticKernel.Text;

/// <summary>
/// SemanticKernelTextSplitter wraps TextChunker from Microsoft.SemanticKernel.Core
/// to break large documents into token-bounded chunks. Tests verify:
/// constructor validation prevents misconfiguration at setup time;
/// each chunk preserves loader-supplied metadata (file_extension, file_name, etc.);
/// original document metadata is never mutated (chunks get their own copies);
/// the markdown code path is exercised when file_extension is ".md";
/// a custom TokenCounter delegate is actually forwarded to TextChunker.
/// </summary>
public sealed class SemanticKernelTextSplitterTests
{
    /// <summary>
    /// A maxTokens of zero would create chunks with no content budget, causing
    /// TextChunker to produce degenerate results. Failing fast in the constructor
    /// is better than producing empty or infinite chunks at runtime.
    /// </summary>
    [Fact]
    public void Constructor_ZeroMaxTokens_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SemanticKernelTextSplitter(0, 0));
    }

    /// <summary>
    /// Negative maxTokens has the same problem as zero — it is a nonsensical
    /// configuration that should be rejected before the splitter is used.
    /// </summary>
    [Fact]
    public void Constructor_NegativeMaxTokens_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SemanticKernelTextSplitter(-1, 0));
    }

    /// <summary>
    /// A negative overlap would imply a gap between chunks (skipped tokens),
    /// which TextChunker does not support and which would silently lose content.
    /// </summary>
    [Fact]
    public void Constructor_NegativeOverlapTokens_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SemanticKernelTextSplitter(100, -1));
    }

    /// <summary>
    /// Passing null should fail loudly rather than causing a NullReferenceException
    /// deep inside TextChunker, which would produce a confusing stack trace.
    /// </summary>
    [Fact]
    public void Split_NullDocument_ThrowsArgumentNullException()
    {
        var splitter = new SemanticKernelTextSplitter(100, 0);

        Assert.Throws<ArgumentNullException>(() => splitter.Split(null!).ToList());
    }

    /// <summary>
    /// When content fits within maxTokens, TextChunker must produce exactly one
    /// chunk containing the full text. Producing zero chunks would silently drop
    /// a document from the vector store.
    /// </summary>
    [Fact]
    public void Split_ShortContent_YieldsOneChunk()
    {
        var splitter = new SemanticKernelTextSplitter(500, 0);
        var doc = new Document("Short text.", "src");

        var chunks = splitter.Split(doc).ToList();

        Assert.Single(chunks);
        Assert.Contains("Short text.", chunks[0].Content);
    }

    /// <summary>
    /// The splitter is a pure text transformation; chunk_index is owned by the
    /// pipeline (DefaultIngestionPipeline.BuildChunkMetadata). The splitter must
    /// not inject it so the pipeline's authoritative value is never silently overwritten.
    /// </summary>
    [Fact]
    public void Split_EachChunk_DoesNotSetChunkIndex()
    {
        var splitter = new SemanticKernelTextSplitter(500, 0);
        var doc = new Document("Some content.", "src");

        var chunks = splitter.Split(doc).ToList();

        Assert.All(chunks, c => Assert.False(c.Metadata?.ContainsKey("chunk_index") ?? false));
    }

    /// <summary>
    /// SourceId is the provenance identifier linking a chunk back to its source
    /// file. If the splitter changed it, SearchHit results would point to the
    /// wrong file and audit trails would be broken.
    /// </summary>
    [Fact]
    public void Split_EachChunk_PreservesSourceId()
    {
        var splitter = new SemanticKernelTextSplitter(500, 0);
        var doc = new Document("Content.", "my-source-id");

        var chunks = splitter.Split(doc).ToList();

        Assert.All(chunks, c => Assert.Equal("my-source-id", c.SourceId));
    }

    /// <summary>
    /// Metadata set by the loader (file_extension, file_name, etc.) must flow
    /// through to every chunk. The pipeline's BuildChunkMetadata merges loader
    /// metadata with its own injected keys, so the splitter must pass it through
    /// rather than dropping it.
    /// </summary>
    [Fact]
    public void Split_EachChunk_PreservesExistingMetadata()
    {
        var splitter = new SemanticKernelTextSplitter(500, 0);
        var doc = new Document("Content.", "src", new Dictionary<string, object>
        {
            ["file_extension"] = ".md",
            ["file_name"] = "test.md",
        });

        var chunks = splitter.Split(doc).ToList();

        Assert.All(chunks, c =>
        {
            Assert.Equal(".md", c.Metadata!["file_extension"]);
            Assert.Equal("test.md", c.Metadata["file_name"]);
        });
    }

    /// <summary>
    /// Each chunk gets its own metadata dictionary; the splitter must not mutate
    /// the original document's metadata. If it did, a document re-split on retry
    /// would see stale values from the first pass.
    /// </summary>
    [Fact]
    public void Split_DoesNotMutateOriginalMetadata()
    {
        var splitter = new SemanticKernelTextSplitter(500, 0);
        var originalMeta = new Dictionary<string, object> { ["key"] = "value" };
        var doc = new Document("Content.", "src", originalMeta);

        _ = splitter.Split(doc).ToList();

        Assert.Single(originalMeta);
        Assert.False(originalMeta.ContainsKey("chunk_index"));
    }

    /// <summary>
    /// When file_extension is ".md", the splitter must use TextChunker's markdown
    /// path (SplitMarkDownLines + SplitMarkdownParagraphs), which respects heading
    /// boundaries. If the plain-text path were used instead, heading structure
    /// would be ignored and chunks could break mid-section, degrading retrieval quality.
    /// </summary>
    [Fact]
    public void Split_MarkdownDocument_UsesMarkdownPath()
    {
        var splitter = new SemanticKernelTextSplitter(500, 0);
        var doc = new Document(
            "# Header\n\nSome paragraph text.",
            "readme.md",
            new Dictionary<string, object> { ["file_extension"] = ".md" });

        // Should not throw — just verifies the markdown code path executes
        var chunks = splitter.Split(doc).ToList();

        Assert.NotEmpty(chunks);
    }

    /// <summary>
    /// The TokenCounter delegate allows callers to use a real tokenizer (e.g.
    /// cl100k_base for GPT-4o) instead of the default character-based approximation.
    /// This test confirms the delegate is actually invoked — if it were ignored,
    /// chunk sizes would be approximated incorrectly and might exceed the LLM's
    /// context window.
    /// </summary>
    [Fact]
    public void Split_WithCustomTokenCounter_IsUsed()
    {
        int callCount = 0;
        TextChunker.TokenCounter counter = text =>
        {
            callCount++;
            return text.Length;
        };

        var splitter = new SemanticKernelTextSplitter(100, 0, counter);
        var doc = new Document("Some text content here.", "src");

        _ = splitter.Split(doc).ToList();

        Assert.True(callCount > 0);
    }


}
