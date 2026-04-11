# Agency.VectorStore.Common

#vectorstore #abstractions #interface #search

## What It Is

`Agency.VectorStore.Common` defines the `IKVStore` interface — a semantic key-value store abstraction where each value is stored alongside its embedding vector, enabling both exact-key lookup and approximate-nearest-neighbor (ANN) similarity search.

## Key Types

### `IKVStore`

```csharp
public interface IKVStore
{
    /// <summary>Stores or updates a value under the given key.</summary>
    Task UpsertAsync<TValue>(
        string key,
        TValue value,
        IDictionary<string, object>? metadata = null,
        CancellationToken ct = default);

    /// <summary>Searches for entries similar to the query.</summary>
    Task<IReadOnlyList<SearchHit<TValue>>> SearchAsync<TValue>(
        Query query,
        CancellationToken ct = default);
}
```

### `Query`

```csharp
public sealed record Query(
    string? Value = null,         // semantic search text (embedded at query time)
    string? Key   = null,         // optional exact key filter
    int?    Limit = null,         // max results (default 10)
    IDictionary<string, object>? metadataFilter = null);  // metadata containment filter
```

### `SearchHit<TValue>`

```csharp
public sealed record SearchHit<TValue>(
    string                     Key,
    TValue                     Value,
    Dictionary<string, object>? Metadata,
    double                     Distance,    // cosine distance (0 = identical)
    DateTimeOffset             UpdatedOn);
```

## Usage

```csharp
// Store a document
await store.UpsertAsync("doc:001",
    new { Title = "Introduction to RAG", Body = "..." },
    metadata: new Dictionary<string, object> { ["category"] = "technical" });

// Semantic search
IReadOnlyList<SearchHit<MyDoc>> hits = await store.SearchAsync<MyDoc>(
    new Query(Value: "how does retrieval augmented generation work?", Limit: 5));

foreach (var hit in hits)
{
    Console.WriteLine($"{hit.Key}  distance={hit.Distance:F4}");
}

// Metadata filter (subset containment)
IReadOnlyList<SearchHit<MyDoc>> filtered = await store.SearchAsync<MyDoc>(
    new Query(Value: "RAG", metadataFilter: new Dictionary<string, object> { ["category"] = "technical" }));
```

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.VectorStore.Sql.Postgre]] | `PostgreKVStore` implements `IKVStore` backed by PostgreSQL + pgvector |
| [[Agency.VectorStore.Sql.Sqlite]] | `SqliteKVStore` implements `IKVStore` backed by SQLite + UDF cosine distance |
| [[Agency.Ingestion]] | `DefaultIngestionPipeline` calls `IKVStore.UpsertAsync` to store chunked documents |
| [[Agency.Embeddings.Common]] | Both implementations inject `IEmbeddingGenerator` to produce vectors |
