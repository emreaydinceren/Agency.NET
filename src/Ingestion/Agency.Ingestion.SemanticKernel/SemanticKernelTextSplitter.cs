namespace Agency.Ingestion.SemanticKernel;

using Microsoft.SemanticKernel.Text;

/// <summary>
/// Splits documents into chunks using <see cref="TextChunker"/> from Microsoft.SemanticKernel.
/// </summary>
public sealed class SemanticKernelTextSplitter : ITextSplitter
{
    private static readonly string[] MarkdownExtensions = [".md", ".markdown"];

    private readonly int _maxTokens;
    private readonly int _overlapTokens;
    private readonly TextChunker.TokenCounter? _tokenCounter;

    /// <summary>
    /// Creates a splitter with the specified chunk and overlap sizes.
    /// </summary>
    /// <param name="maxTokens">Maximum tokens per chunk. Must be greater than zero.</param>
    /// <param name="overlapTokens">Number of overlapping tokens between adjacent chunks. Must be non-negative.</param>
    /// <param name="tokenCounter">
    /// Optional custom token counting delegate. When null, <see cref="TextChunker"/> uses its default approximation.
    /// </param>
    public SemanticKernelTextSplitter(
        int maxTokens,
        int overlapTokens,
        TextChunker.TokenCounter? tokenCounter = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTokens);
        ArgumentOutOfRangeException.ThrowIfNegative(overlapTokens);

        this._maxTokens = maxTokens;
        this._overlapTokens = overlapTokens;
        this._tokenCounter = tokenCounter;
    }

    /// <inheritdoc/>
    public IEnumerable<Document> Split(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);

        bool isMarkdown = IsMarkdown(document.Metadata);

        List<string> paragraphs = isMarkdown
            ? this.SplitMarkdown(document.Content)
            : this.SplitPlainText(document.Content);

        return paragraphs.Select(text => document with
        {
            Content = text,
            Metadata = document.Metadata is null ? null : new Dictionary<string, object>(document.Metadata, StringComparer.Ordinal),
        });
    }

    private List<string> SplitPlainText(string content)
    {
        var lines = TextChunker.SplitPlainTextLines(content, this._maxTokens, this._tokenCounter);
        return TextChunker.SplitPlainTextParagraphs(lines, this._maxTokens, this._overlapTokens, tokenCounter: this._tokenCounter);
    }

    private List<string> SplitMarkdown(string content)
    {
        var lines = TextChunker.SplitMarkDownLines(content, this._maxTokens, this._tokenCounter);
        return TextChunker.SplitMarkdownParagraphs(lines, this._maxTokens, this._overlapTokens, tokenCounter: this._tokenCounter);
    }

    private static bool IsMarkdown(Dictionary<string, object>? metadata)
    {
        if (metadata is null || !metadata.TryGetValue("file_extension", out object? ext))
        {
            return false;
        }

        string? extStr = ext?.ToString();
        return extStr is not null &&
               MarkdownExtensions.Contains(extStr, StringComparer.OrdinalIgnoreCase);
    }

}
