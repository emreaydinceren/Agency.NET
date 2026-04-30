# Agency.Embeddings.Common

#embeddings #abstractions #interface #batching

## What It Is

Agency.Embeddings.Common is the shared abstractions library that defines the embedding generation contract used across the solution and provides a batching decorator that reduces round-trips to the embedding API.

**Namespace:** `Agency.Embeddings.Common`

## API Surface

### Interfaces

```csharp
// File: src/Embeddings/Agency.Embeddings.Common/IEmbeddingGenerator.cs
using System.Collections.Generic;
using System.Threading;

namespace Agency.Embeddings.Common;

public interface IEmbeddingGenerator
{
    Task<ReadOnlyMemory<float>> GenerateEmbeddingAsync(
        string input,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ReadOnlyMemory<float>>> GenerateEmbeddingsAsync(
        IEnumerable<string> inputs,
        CancellationToken cancellationToken = default);
}
```

### Concrete Types

`BatchingEmbeddingGenerator` is a decorator that coalesces individual `GenerateEmbeddingAsync` calls into batch requests. It implements both `IEmbeddingGenerator` and `IAsyncDisposable`.

```csharp
// File: src/Embeddings/Agency.Embeddings.Common/BatchingEmbeddingGenerator.cs
using System;
using System.Collections.Generic;
using System.Threading;

namespace Agency.Embeddings.Common;

public sealed class BatchingEmbeddingGenerator : IEmbeddingGenerator, IAsyncDisposable
{
    public const int DefaultMaxBatchSize = 32;
    public static readonly TimeSpan DefaultMaxDelay = TimeSpan.FromMilliseconds(50);

    public BatchingEmbeddingGenerator(
        IEmbeddingGenerator inner,
        int maxBatchSize = DefaultMaxBatchSize,
        TimeSpan? maxDelay = null);

    // IEmbeddingGenerator
    public Task<ReadOnlyMemory<float>> GenerateEmbeddingAsync(
        string input,
        CancellationToken cancellationToken = default);

    public Task<IReadOnlyList<ReadOnlyMemory<float>>> GenerateEmbeddingsAsync(
        IEnumerable<string> inputs,
        CancellationToken cancellationToken = default);

    // IAsyncDisposable
    public ValueTask DisposeAsync();
}
```

## How It Works

`IEmbeddingGenerator` exposes two methods: a single-text variant for convenience and a batch variant for throughput. The return type `ReadOnlyMemory<float>` is a zero-copy slice over an array, letting callers pass vectors directly to `Span<float>` APIs or pgvector formatters without additional allocation.

`BatchingEmbeddingGenerator` wraps any `IEmbeddingGenerator` using an unbounded `System.Threading.Channels.Channel<PendingRequest>`. Calls to `GenerateEmbeddingAsync` enqueue a `TaskCompletionSource`-backed request and return immediately. A background flush loop reads the channel and groups up to `maxBatchSize` items into a single `GenerateEmbeddingsAsync` call on the inner generator. A batch flushes when it reaches `maxBatchSize` or when the `maxDelay` window expires, whichever comes first. Calls to `GenerateEmbeddingsAsync` bypass the buffer and are forwarded directly to the inner generator. Disposing the decorator completes the channel writer and drains any queued requests before the flush loop exits.

Usage example:

```csharp
using Agency.Embeddings.Common;

// Wrap any IEmbeddingGenerator with batching:
IEmbeddingGenerator inner = /* e.g. OpenAI implementation */;
await using var batching = new BatchingEmbeddingGenerator(inner, maxBatchSize: 64);

// Single call — enqueued and flushed as part of the next batch:
ReadOnlyMemory<float> vector = await batching.GenerateEmbeddingAsync("hello world");

// Bulk call — forwarded directly to inner generator:
IReadOnlyList<ReadOnlyMemory<float>> vectors = await batching.GenerateEmbeddingsAsync(
    new[] { "foo", "bar", "baz" });
```

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.Embeddings.OpenAI]] | Concrete `IEmbeddingGenerator` implementation using the OpenAI-compatible HTTP API |
| [[Agency.Sql.Postgre]] | `SQLQueryEmbedder` injects `IEmbeddingGenerator` to replace `vectorize(…)` placeholders in SQL |
| [[Agency.Sql.Sqlite]] | Same `SQLQueryEmbedder` pattern, SQLite variant |
| [[Agency.VectorStore.Sql.Postgre]] | Injects `IEmbeddingGenerator` to embed stored values and compute query vectors |
| [[Agency.VectorStore.Sql.Sqlite]] | Same vector store pattern, SQLite variant |
| [[Agency.Ingestion]] | Indirectly — the vector store used by the ingestion pipeline depends on `IEmbeddingGenerator` |

## Design Notes

- **No NuGet dependencies** — the `.csproj` is empty beyond the SDK import; it is a pure-abstraction library that any provider or consumer can reference without pulling in transitive dependencies.
- **`ReadOnlyMemory<float>` not `float[]`** — avoids defensive copies at call boundaries; callers that need a writable copy call `.ToArray()`, keeping the common path allocation-free.
- **Channel-based batching** — `System.Threading.Channels` keeps the flush loop lock-free; the delay window uses a `CancellationTokenSource` timeout rather than a `System.Threading.Timer`, simplifying disposal and avoiding timer-callback threading issues.
- **`GenerateEmbeddingsAsync` bypasses the buffer** — callers that already hold a list of inputs should not pay per-item channel write overhead; `BatchingEmbeddingGenerator` forwards them directly to the inner generator.
