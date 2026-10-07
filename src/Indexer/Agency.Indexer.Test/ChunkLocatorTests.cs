using Agency.Ingestion;
using Agency.Ingestion.SemanticKernel;

namespace Agency.Indexer.Test;

/// <summary>Tests for <see cref="ChunkLocator"/>.</summary>
public sealed class ChunkLocatorTests
{
    /// <summary>Verifies line spans and the nearest heading for hand-made chunks.</summary>
    [Fact]
    public void Locate_MarkdownChunks_ReturnsLineSpansAndHeading()
    {
        const string content = "# Title\n\nintro line\n\n## Releases\n\ntag the commit\npush the tag\n\n## Logging\n\nturn on debug\n";

        IReadOnlyList<ChunkSpan> spans = ChunkLocator.Locate(content, ["intro line", "tag the commit\npush the tag", "turn on debug"], markdown: true);

        Assert.Equal(new ChunkSpan("Title", 3, 3), spans[0]);
        Assert.Equal(new ChunkSpan("Releases", 7, 8), spans[1]);
        Assert.Equal(new ChunkSpan("Logging", 12, 12), spans[2]);
    }

    /// <summary>Verifies that a <c>#</c> line inside a code fence is not taken for a heading.</summary>
    [Fact]
    public void Locate_HashInsideFence_IsNotAHeading()
    {
        const string content = "# Real\n\n```bash\n# not a heading\nrun it\n```\n";

        IReadOnlyList<ChunkSpan> spans = ChunkLocator.Locate(content, ["run it"], markdown: true);

        Assert.Equal("Real", spans[0].Heading);
    }

    /// <summary>Verifies that plain text gets lines but no heading, and CRLF line endings are handled.</summary>
    [Fact]
    public void Locate_PlainTextWithCrlf_HasLinesButNoHeading()
    {
        IReadOnlyList<ChunkSpan> spans = ChunkLocator.Locate("# not markdown\r\nsecond\r\nthird", ["second\nthird"], markdown: false);

        Assert.Equal(new ChunkSpan(null, 2, 3), spans[0]);
    }

    /// <summary>Verifies that an unmatched chunk gets no span and does not derail the following chunks.</summary>
    [Fact]
    public void Locate_UnmatchedChunk_GetsNoSpan()
    {
        IReadOnlyList<ChunkSpan> spans = ChunkLocator.Locate("alpha\nbeta\ngamma", ["alpha", "never in the file", "gamma"], markdown: false);

        Assert.Equal(new ChunkSpan(null, 1, 1), spans[0]);
        Assert.Equal(new ChunkSpan(null, null, null), spans[1]);
        Assert.Equal(new ChunkSpan(null, 3, 3), spans[2]);
    }

    /// <summary>
    /// Verifies against the real splitter (with overlap) that every chunk of a multi-section document is located,
    /// spans only move forward, and each span's lines really contain the chunk text.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    public void Locate_RealSplitterOutput_EveryChunkIsFoundInsideItsSpan(int overlap)
    {
        string content = string.Join("\n\n", Enumerable.Range(0, 12).Select(i =>
            $"## Section {i}\n\n" + string.Join("\n", Enumerable.Range(0, 6).Select(j => $"Section {i} line {j} explains deployment topic number {i * 10 + j} in some detail."))));
        var splitter = new SemanticKernelTextSplitter(64, overlap);
        var document = new Document(content, "x.md", new Dictionary<string, object> { ["file_extension"] = ".md" });
        List<string> chunks = splitter.Split(document).Select(d => d.Content).ToList();
        Assert.True(chunks.Count > 12);

        IReadOnlyList<ChunkSpan> spans = ChunkLocator.Locate(content, chunks, markdown: true);

        string[] lines = content.Split('\n');
        int previousStart = 0;
        for (int i = 0; i < chunks.Count; i++)
        {
            ChunkSpan span = spans[i];
            Assert.NotNull(span.StartLine);
            Assert.True(span.StartLine >= previousStart, $"chunk {i} starts before chunk {i - 1}");
            previousStart = span.StartLine!.Value;

            string spanText = string.Join(' ', lines[(span.StartLine.Value - 1)..span.EndLine!.Value]);
            Assert.Contains(string.Join(' ', chunks[i].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), string.Join(' ', spanText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
            Assert.StartsWith("Section ", span.Heading);
        }
    }
}
