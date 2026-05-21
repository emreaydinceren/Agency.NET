# Agency.Ingestion

#ingestion #pipeline #documents #chunking #abstractions

## What It Is

`Agency.Ingestion` is the core abstraction layer that defines and orchestrates the document ingestion pipeline — the process of loading raw documents, splitting them into chunks, and storing each chunk in a vector store.

**Namespace:** `Agency.Ingestion`

## API Surface

### Interfaces

```csharp
// File: src/Ingestion/Agency.Ingestion/IDocumentLoader.cs
using Agency.Ingestion;

public interface IDocumentLoader
{
    IAsyncEnumerable<Document> LoadAsync(CancellationToken ct = default);
}
```

```csharp
// File: src/Ingestion/Agency.Ingestion/ITextSplitter.cs
using Agency.Ingestion;

public interface ITextSplitter
{
    IEnumerable<Document> Split(Document document);
}
```

```csharp
// File: src/Ingestion/Agency.Ingestion/IIngestionPipeline.cs
using Agency.Ingestion;
using Agency.VectorStore.Common;

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

### Value Types

```csharp
// File: src/Ingestion/Agency.Ingestion/Document.cs
using Agency.Ingestion;

public record Document(
    string Content,
    string SourceId,
    Dictionary<string, object>? Metadata = null);
```

```csharp
// File: src/Ingestion/Agency.Ingestion/IngestionResult.cs
using Agency.Ingestion;

public record IngestionResult(
    int Succeeded,
    int Failed,
    IReadOnlyList<string>? FailedKeys = null)
{
    public bool IsSuccess => Failed == 0;
}
```

### Concrete Types

```csharp
// File: src/Ingestion/Agency.Ingestion/DefaultIngestionPipeline.cs
using Agency.Ingestion;
using Microsoft.Extensions.Logging;

public sealed class DefaultIngestionPipeline<TValue> : IIngestionPipeline<TValue>
{
    public const string ActivitySourceName = "Agency.Ingestion";
    public const string MeterName = "Agency.Ingestion";

    public DefaultIngestionPipeline(
        Func<Document, TValue> chunkConverter,
        int maxDegreeOfParallelism = 0,
        ILogger<DefaultIngestionPipeline<TValue>>? logger = null);

    public Task<IngestionResult> ExecuteAsync(
        IDocumentLoader loader,
        ITextSplitter splitter,
        IVectorStore store,
        string userId,
        string? sessionId,
        CancellationToken ct = default);
}
```

## How It Works

1. `DefaultIngestionPipeline<TValue>.ExecuteAsync` starts an `ingestion.execute` activity and a stopwatch.
2. It calls `loader.LoadAsync(ct)` to stream `Document` objects from the source.
3. Documents are processed concurrently via `Parallel.ForEachAsync` — parallelism defaults to `Environment.ProcessorCount` when `maxDegreeOfParallelism` is `0` or unset.
4. For each document, `splitter.Split(document)` produces an ordered list of chunk `Document`s.
5. Each chunk is keyed as `{sourceId}:chunk:{index}`, and its metadata is augmented with `source_file`, `chunk_index`, and `ingested_at`.
6. `chunkConverter` transforms each chunk `Document` into the `TValue` expected by the vector store, then `store.UpsertAsync` persists it.
7. Success and failure counts are aggregated thread-safely; failed chunk keys are collected in a `ConcurrentBag<string>`.
8. Duration is recorded to the `ingestion.duration_ms` histogram; activity tags `ingestion.succeeded` and `ingestion.failed` are set, and the activity status is set to `Error` if any chunks failed.
9. An `IngestionResult` is returned with the final counts and, when failures occurred, the list of failed keys.

```csharp
using Agency.Ingestion;
using Agency.VectorStore.Common;
using Microsoft.Extensions.Logging;

ILogger<DefaultIngestionPipeline<string>> logger = loggerFactory
    .CreateLogger<DefaultIngestionPipeline<string>>();

var pipeline = new DefaultIngestionPipeline<string>(
    chunkConverter: chunk => chunk.Content,
    maxDegreeOfParallelism: 4,
    logger: logger);

IngestionResult result = await pipeline.ExecuteAsync(
    loader: myLoader,
    splitter: myTextSplitter,
    store: myVectorStore,
    userId: "user-123",
    sessionId: "session-abc",
    ct: cancellationToken);

if (!result.IsSuccess)
{
    Console.WriteLine($"Failed chunks: {string.Join(", ", result.FailedKeys ?? [])}");
}
```

## Observability

**ActivitySource name:** `"Agency.Ingestion"`  
**Meter name:** `"Agency.Ingestion"`

| Metric | Kind | Unit | Tags |
|--------|------|------|------|
| `ingestion.documents_total` | Counter | — | `status` (`"success"` \| `"failure"`) |
| `ingestion.duration_ms` | Histogram | ms | — |

Activity `ingestion.execute` carries tags `ingestion.succeeded` and `ingestion.failed`. When any chunks fail, the activity status is set to `ActivityStatusCode.Error` with a message indicating the failure count.

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.Ingestion.FileSystem]] | Provides `IDocumentLoader` implementations for ingestion sources |
| [[Agency.Ingestion.SemanticKernel]] | Provides `ITextSplitter` implementations used by the pipeline |
| [[Agency.VectorStore.Common]] | `DefaultIngestionPipeline` calls `IVectorStore.UpsertAsync` |
| [[Agency.VectorStore.Sql.Postgre]] | Typical production store backend |
| [[Agency.VectorStore.Sql.Sqlite]] | Typical dev/test store backend |

## Design Notes

- `DefaultIngestionPipeline<TValue>` is generic over `TValue` so that callers supply the conversion from `Document` to whatever the target vector store expects (e.g., `float[]`, `string`, or a custom embedding type), keeping this project free of any embedding-generation dependency.
- Chunk keys use the deterministic pattern `{sourceId}:chunk:{index}`, which makes re-ingestion idempotent — re-running the pipeline on the same source overwrites existing chunks rather than duplicating them.
- `DefaultIngestionPipeline<TValue>` is `sealed`, so it cannot be subclassed; custom orchestration logic should implement `IIngestionPipeline<TValue>` directly.
- The constructor rejects a null `chunkConverter` with `ArgumentNullException` at construction time rather than at first use, failing fast before any I/O is attempted.
