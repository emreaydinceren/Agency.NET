# Agency.Embeddings.Common

#embeddings #abstractions #interface

## What It Is

`Agency.Embeddings.Common` defines the `IEmbeddingGenerator` interface — the single contract every embedding provider in the solution must implement. It is a pure-abstraction library with no provider-specific code and no runtime dependencies beyond the .NET BCL.

## Key Types

### `IEmbeddingGenerator`

```csharp
public interface IEmbeddingGenerator
{
    /// <summary>Generates a single embedding vector for the given text.</summary>
    Task<ReadOnlyMemory<float>> GenerateEmbeddingAsync(
        string text,
        CancellationToken cancellationToken = default);

    /// <summary>Generates embedding vectors for multiple texts in one batch call.</summary>
    Task<IReadOnlyList<ReadOnlyMemory<float>>> GenerateEmbeddingsAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default);
}
```

The return type `ReadOnlyMemory<float>` is a zero-copy slice over an array, enabling the caller to convert to `float[]`, pass to `Span<float>` APIs, or use with pgvector's `Vector` type without extra allocation.

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.Embeddings.OpenAI]] | Concrete implementation using the OpenAI-compatible HTTP API |
| [[Agency.Sql.Postgre]] | `SQLQueryEmbedder` injects `IEmbeddingGenerator` to replace `vectorize(…)` placeholders |
| [[Agency.Sql.Sqlite]] | Same `SQLQueryEmbedder` pattern, SQLite variant |
| [[Agency.VectorStore.Sql.Postgre]] | `PostgreKVStore` injects `IEmbeddingGenerator` to embed stored values and query vectors |
| [[Agency.VectorStore.Sql.Sqlite]] | `SqliteKVStore` injects `IEmbeddingGenerator` for the same purpose |
| [[Agency.Ingestion]] | Indirectly — the vector store used by the ingestion pipeline depends on this interface |

## Design Notes

- **Batch vs. single** — `GenerateEmbeddingsAsync` allows callers to coalesce multiple texts into a single HTTP request, which is important for throughput during ingestion. The single-text variant is a convenience wrapper.
- **`ReadOnlyMemory<float>` not `float[]`** — avoids defensive copies when passing vectors to native libraries or SQL formatters. Callers that need a writable copy can always call `.ToArray()`.
