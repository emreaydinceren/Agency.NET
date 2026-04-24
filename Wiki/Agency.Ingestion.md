# Agency.Ingestion

#ingestion #pipeline #documents #chunking #abstractions

## What It Is

`Agency.Ingestion` defines the abstractions and default orchestration for the document ingestion pipeline — the process of loading raw documents, splitting them into chunks, and storing them in a vector store. It contains no provider-specific code.

The project references [[Agency.VectorStore.Common]] and uses `Microsoft.Extensions.Logging.Abstractions` for the default pipeline logger integration.

## Key Types

### `Document`

```csharp
public record Document(
    string Content,
    string SourceId,
    Dictionary<string, object>? Metadata = null);
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
        IVectorStore store,
        string userId,
        string? sessionId,
        CancellationToken ct = default);
}
```

### `DefaultIngestionPipeline<TValue>`

The default implementation uses `Parallel.ForEachAsync` with configurable parallelism. `maxDegreeOfParallelism` defaults to `Environment.ProcessorCount` when not provided. Chunk keys follow the pattern `{sourceId}:chunk:{index}`. Each chunk's metadata is augmented with `source_file`, `chunk_index`, and `ingested_at`.

```csharp
var pipeline = new DefaultIngestionPipeline<string>(
    chunkConverter: chunk => chunk.Content,
    maxDegreeOfParallelism: 4,
    logger: logger);

IngestionResult result = await pipeline.ExecuteAsync(
    loader: loader,
    splitter: splitter,
    store: vectorStore,
    userId: "user-123",
    sessionId: "session-abc",
    ct: cancellationToken);

if (!result.IsSuccess)
{
    Console.WriteLine(string.Join(", ", result.FailedKeys ?? []));
}
```

### `IngestionResult`

```csharp
public record IngestionResult(
    int Succeeded,
    int Failed,
    IReadOnlyList<string>? FailedKeys = null)
{
    public bool IsSuccess => Failed == 0;
}
```

## Observability

- **Activity** `ingestion.execute`
- **Counter** `ingestion.documents_total` (tags: `status`)
- **Histogram** `ingestion.duration_ms`
- **Logs** start/completion info logs and per-chunk upsert error logs

ActivitySource name: `Agency.Ingestion` | Meter name: `Agency.Ingestion`

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.Ingestion.FileSystem]] | Provides `IDocumentLoader` implementations for ingestion sources |
| [[Agency.Ingestion.SemanticKernel]] | Provides `ITextSplitter` implementations used by the pipeline |
| [[Agency.VectorStore.Common]] | `DefaultIngestionPipeline` calls `IVectorStore.UpsertAsync` |
| [[Agency.VectorStore.Sql.Postgre]] | Typical production store backend |
| [[Agency.VectorStore.Sql.Sqlite]] | Typical dev/test store backend |
