# Agency.Ingestion

#ingestion #pipeline #documents #chunking #abstractions

## What It Is

`Agency.Ingestion` defines the abstractions and default orchestration for the document ingestion pipeline — the process of loading raw documents, splitting them into chunks, and storing them in a vector store. It contains no provider-specific code.

## Key Types

### `Document`

```csharp
public sealed record Document(
    string SourceId,                        // unique identifier (e.g., file path)
    string Content,                         // raw text of this document or chunk
    Dictionary<string, object>? Metadata);  // arbitrary key-value pairs (file type, etc.)
```

### `IDocumentLoader`

Streams documents from a source without buffering all in memory:

```csharp
public interface IDocumentLoader
{
    IAsyncEnumerable<Document> LoadAsync(CancellationToken ct = default);
}
```

### `ITextSplitter`

Breaks a `Document` into smaller chunk `Document`s:

```csharp
public interface ITextSplitter
{
    IEnumerable<Document> Split(Document document);
}
```

### `IIngestionPipeline<TValue>`

Orchestrates load → split → store:

```csharp
public interface IIngestionPipeline<TValue>
{
    Task<IngestionResult> ExecuteAsync(
        IDocumentLoader loader,
        ITextSplitter splitter,
        IKVStore store,
        CancellationToken ct = default);
}
```

### `DefaultIngestionPipeline<TValue>`

The default implementation uses `Parallel.ForEachAsync` with configurable parallelism. Chunk keys follow the pattern `{sourceId}:chunk:{index}`. Each chunk's metadata is augmented with `source_file`, `chunk_index`, and `ingested_at`.

```csharp
var pipeline = new DefaultIngestionPipeline<string>(
    chunkConverter: doc => doc.Content,    // store the text itself as TValue
    maxDegreeOfParallelism: 4);

IngestionResult result = await pipeline.ExecuteAsync(
    loader:   new DirectoryLoader("/docs", "*.md"),
    splitter: new SemanticKernelTextSplitter(maxTokens: 512, overlapTokens: 64),
    store:    kvStore);

Console.WriteLine($"Succeeded: {result.Succeeded}, Failed: {result.Failed}");
```

### `IngestionResult`

```csharp
public sealed record IngestionResult(
    int Succeeded,
    int Failed,
    IReadOnlyList<string>? FailedKeys);
```

## Observability

- **Activity** `ingestion.execute`
- **Counter** `ingestion.documents_total` (tags: `status`)
- **Histogram** `ingestion.duration_ms`

ActivitySource name: `Agency.Ingestion` | Meter name: `Agency.Ingestion`

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.Ingestion.FileSystem]] | Implements `IDocumentLoader` for files and directories |
| [[Agency.Ingestion.SemanticKernel]] | Implements `ITextSplitter` using SK's `TextChunker` |
| [[Agency.VectorStore.Common]] | `DefaultIngestionPipeline` calls `IKVStore.UpsertAsync` |
| [[Agency.VectorStore.Sql.Postgre]] | Typical production store backend |
| [[Agency.VectorStore.Sql.Sqlite]] | Typical dev/test store backend |
