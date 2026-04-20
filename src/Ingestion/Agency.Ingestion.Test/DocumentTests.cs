namespace Agency.Ingestion.Test;

/// <summary>
/// Document is the core data carrier for the ingestion pipeline. Every piece of
/// text flowing through Load → Split → Store is represented as a Document.
/// These tests verify that its record semantics behave correctly: positional
/// construction, optional metadata, and the immutable <c>with</c> expression used
/// by splitters to produce chunk copies.
/// </summary>
public sealed class DocumentTests
{
    /// <summary>
    /// Verifies that all three positional parameters are stored as-is.
    /// The Metadata instance must be the same object (not a copy) so that
    /// callers can pass pre-built metadata dictionaries without unexpected allocation.
    /// </summary>
    [Fact]
    public void Constructor_SetsProperties_Correctly()
    {
        var metadata = new Dictionary<string, object> { ["key"] = "value" };
        var doc = new Document("content", "source-1", metadata);

        Assert.Equal("content", doc.Content);
        Assert.Equal("source-1", doc.SourceId);
        Assert.Same(metadata, doc.Metadata);
    }

    /// <summary>
    /// Metadata is an optional parameter. When omitted, it must be null rather
    /// than an empty dictionary — consumers use a null check to decide whether
    /// metadata operations are needed, so a null default avoids unnecessary
    /// allocations for documents that carry no metadata.
    /// </summary>
    [Fact]
    public void Metadata_DefaultsToNull_WhenNotProvided()
    {
        var doc = new Document("content", "source-1");

        Assert.Null(doc.Metadata);
    }

    /// <summary>
    /// SemanticKernelTextSplitter produces chunk documents using <c>with</c> expressions.
    /// This test confirms that the updated property takes effect and that
    /// unchanged properties (SourceId) are copied from the original — the
    /// standard behaviour for C# positional records.
    /// </summary>
    [Fact]
    public void WithExpression_CreatesNewDocument_WithUpdatedContent()
    {
        var doc = new Document("original", "src");
        var updated = doc with { Content = "updated" };

        Assert.Equal("updated", updated.Content);
        Assert.Equal("src", updated.SourceId);
    }

    /// <summary>
    /// SourceId carries the document's provenance (e.g. the file path). When a
    /// splitter produces chunks it must not alter the SourceId — each chunk
    /// must remain traceable back to the same source file. This test verifies
    /// that a <c>with</c> expression only changes what is explicitly specified.
    /// </summary>
    [Fact]
    public void WithExpression_PreservesSourceId_WhenOnlyContentChanged()
    {
        var original = new Document("text", "my-source");
        var chunk = original with { Content = "chunk text" };

        Assert.Equal("my-source", chunk.SourceId);
    }

    /// <summary>
    /// Records are value-like but the <c>with</c> expression creates a new instance;
    /// the original must remain unchanged. If <c>with</c> mutated the original,
    /// the same Document reference used across multiple split passes would
    /// silently corrupt the pipeline's in-flight data.
    /// </summary>
    [Fact]
    public void WithExpression_DoesNotMutateOriginal()
    {
        var original = new Document("original", "src");
        var _ = original with { Content = "mutated" };

        Assert.Equal("original", original.Content);
    }
}
