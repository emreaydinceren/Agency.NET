# Agency.Ingestion.SemanticKernel

#ingestion #semantickernel #chunking #textsplitter

## What It Is

`Agency.Ingestion.SemanticKernel` provides a `ITextSplitter` implementation backed by Microsoft Semantic Kernel's `TextChunker`. It automatically detects Markdown documents (via the `file_extension` metadata key set by [[Agency.Ingestion.FileSystem]]) and applies the appropriate splitting strategy.

## How It Works

```csharp
var splitter = new SemanticKernelTextSplitter(
    maxTokens:    512,   // maximum tokens per chunk
    overlapTokens: 64);  // tokens shared between adjacent chunks

// Plain text: SplitPlainTextLines + SplitPlainTextParagraphs
// Markdown:   SplitMarkDownLines + SplitMarkdownParagraphs
IEnumerable<Document> chunks = splitter.Split(document);
```

### Markdown Detection

The splitter checks `document.Metadata["file_extension"]`. If the value is `.md` or `.markdown` (case-insensitive), it uses the Markdown-aware SK splitter, which respects heading boundaries. Otherwise it uses plain text splitting.

```csharp
// Markdown-aware (preserves heading structure)
var lines = TextChunker.SplitMarkDownLines(content, maxTokens, tokenCounter);
return TextChunker.SplitMarkdownParagraphs(lines, maxTokens, overlapTokens, tokenCounter: tokenCounter);

// Plain text (paragraph-based)
var lines = TextChunker.SplitPlainTextLines(content, maxTokens, tokenCounter);
return TextChunker.SplitPlainTextParagraphs(lines, maxTokens, overlapTokens, tokenCounter: tokenCounter);
```

### Custom Token Counter

For accurate token counts, inject a model-specific tokenizer:

```csharp
// Using TiktokenSharp (example)
var splitter = new SemanticKernelTextSplitter(
    maxTokens:    512,
    overlapTokens: 64,
    tokenCounter: text => TiktokenSharp.TikToken.EncodingForModel("gpt-4").Encode(text).Count);
```

When `null`, SK's default approximation (characters ÷ 4) is used.

### Chunk Metadata

Each produced chunk inherits the source document's metadata and gets an additional `chunk_index` key:

```csharp
// Original metadata: { file_extension: ".md", file_name: "readme" }
// Chunk metadata:    { file_extension: ".md", file_name: "readme", chunk_index: 3 }
```

The `DefaultIngestionPipeline` further augments chunk metadata with `source_file`, `chunk_index` (overwriting), and `ingested_at`.

## Integration

```csharp
var loader   = new DirectoryLoader("/knowledge-base", "*.md");
var splitter = new SemanticKernelTextSplitter(maxTokens: 512, overlapTokens: 64);
var pipeline = new DefaultIngestionPipeline<string>(doc => doc.Content);

await pipeline.ExecuteAsync(loader, splitter, kvStore);
```

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.Ingestion]] | Implements `ITextSplitter` defined there |
| [[Agency.Ingestion.FileSystem]] | Relies on `file_extension` metadata set by `FileLoader.BuildDocument` |
| [[Agency.VectorStore.Common]] | Chunks are ultimately stored via `IKVStore.UpsertAsync` |
