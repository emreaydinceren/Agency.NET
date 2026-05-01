# Agency.Ingestion.SemanticKernel

#ingestion #semantickernel #chunking #textsplitter

## What It Is

`Agency.Ingestion.SemanticKernel` is the `ITextSplitter` implementation that splits documents into overlapping token-bounded chunks using Microsoft Semantic Kernel's `TextChunker`, with automatic Markdown-aware splitting when the document carries a `.md` or `.markdown` file extension in its metadata.

**Namespace:** `Agency.Ingestion.SemanticKernel`

## API Surface

```csharp
// File: src/Ingestion/Agency.Ingestion.SemanticKernel/SemanticKernelTextSplitter.cs
using Agency.Ingestion;
using Microsoft.SemanticKernel.Text;

namespace Agency.Ingestion.SemanticKernel;

/// <summary>Splits documents into chunks using <see cref="TextChunker"/>.</summary>
public sealed class SemanticKernelTextSplitter : ITextSplitter
{
    /// <param name="maxTokens">Maximum tokens per chunk. Must be greater than zero.</param>
    /// <param name="overlapTokens">Overlapping tokens between adjacent chunks. Must be non-negative.</param>
    /// <param name="tokenCounter">
    ///   Optional custom token counting delegate.
    ///   When null, <see cref="TextChunker"/> uses its default approximation (characters ÷ 4).
    /// </param>
    public SemanticKernelTextSplitter(
        int maxTokens,
        int overlapTokens,
        TextChunker.TokenCounter? tokenCounter = null);

    /// <summary>
    ///   Produces zero or more chunk <see cref="Document"/> values from <paramref name="document"/>.
    ///   Each chunk inherits a shallow copy of the source document's metadata.
    /// </summary>
    public IEnumerable<Document> Split(Document document);
}
```

## How It Works

1. `Split` checks `document.Metadata["file_extension"]`. If the value equals `.md` or `.markdown` (case-insensitive) it routes to the Markdown path; otherwise plain text.
2. **Plain-text path** — `TextChunker.SplitPlainTextLines` then `TextChunker.SplitPlainTextParagraphs`.
3. **Markdown path** — `TextChunker.SplitMarkDownLines` then `TextChunker.SplitMarkdownParagraphs`, which respects heading boundaries.
4. Each resulting paragraph string is returned as a new `Document` record (`document with { Content = paragraph, Metadata = shallowCopy }`). No `chunk_index` key is injected by this class — that augmentation belongs to the pipeline layer.

### Custom token counter

By default SK approximates token count as `characters ÷ 4`. Pass a `TextChunker.TokenCounter` delegate to use a real tokenizer:

```csharp
using Agency.Ingestion.SemanticKernel;
using Microsoft.SemanticKernel.Text;

TextChunker.TokenCounter counter = text => myTokenizer.CountTokens(text);

var splitter = new SemanticKernelTextSplitter(
    maxTokens: 512,
    overlapTokens: 64,
    tokenCounter: counter);
```

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.Ingestion]] | Implements `ITextSplitter` defined there; depends on the `Document` record |
| [[Agency.Ingestion.FileSystem]] | `FileLoader` sets `file_extension` in metadata, which drives Markdown detection |
| [[Agency.VectorStore.Common]] | Chunks produced here are ultimately stored via an `IVectorStore` or `IKVStore` in the pipeline |

## Design Notes

- `TextChunker` is marked experimental (`SKEXP0050`) in `Microsoft.SemanticKernel.Core`; the project explicitly suppresses this warning because it is the required API.
- The class is `sealed` with no DI registration helper — it is constructed directly, keeping the API surface minimal and dependency-injection-framework-agnostic.
- Metadata copying is a shallow clone (`new Dictionary<string, object>(source)`) so callers that further enrich chunk metadata do not mutate the source document's dictionary.
