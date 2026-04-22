# Agency.VectorStore.Common

#vectorstore #abstractions #interface #search

## What It Is

`Agency.VectorStore.Common` defines the `IKVStore` interface — a semantic key-value store abstraction where each value is stored alongside its embedding vector, enabling both exact-key lookup and approximate-nearest-neighbor (ANN) similarity search.

## Key Types

### `IKVStore`

```csharp
public interface IKVStore
{
    Task UpsertAsync<TValue>(string key, TValue value,
        IDictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SearchHit<TValue>>> SearchAsync<TValue>(
        Query query, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default);
}
```

### `Query`

```csharp
public record class Query(
    string? Key,                                // optional exact key filter
    string? Value,                              // semantic search text (embedded at query time)
    IDictionary<string, object>? MetadataFilter = null,
    int? Limit = 10,
    bool? IncludeMetadataInResults = false);
```

### `SearchHit<TValue>`

```csharp
public record class SearchHit<TValue>(
    string Key,
    TValue Value,
    Dictionary<string, object>? Metadata,
    double Distance,           // cosine distance: 0 = identical, 2 = opposite
    DateTimeOffset UpdatedOn)
{
    public double SimilarityPercentage { get; }  // (1 - Distance) * 100, clamped to [0, 100]
    public double RecencyMinutes       { get; }  // minutes since UpdatedOn
    public double RecencyHours         { get; }  // hours since UpdatedOn
}
```

### `SearchHitExtensions`

```csharp
// Convert search results to a Dataset for use with Agency.RagFormatter
IReadOnlyList<SearchHit<string>> hits = await store.SearchAsync<string>(query);
Dataset table = hits.ToDataset();
// Columns: Key, Value, Distance, SimilarityPercentage, UpdatedOn
```

### `JsonMetadataHelpers`

Static helpers used by both SQL implementations to round-trip metadata through JSON:

```csharp
// Deserialize JSON → Dictionary<string, object> with native CLR types
Dictionary<string, object>? meta = JsonMetadataHelpers.DeserializeMetadata(jsonString);

// Recursively convert a JsonElement to string / long / double / bool / null / List / Dictionary
object clrValue = JsonMetadataHelpers.ConvertJsonElementToObject(element);
```

## Usage

```csharp
// Store a document
await store.UpsertAsync("doc:001",
    new { Title = "Introduction to RAG", Body = "..." },
    metadata: new Dictionary<string, object> { ["category"] = "technical" });

// Semantic search
IReadOnlyList<SearchHit<MyDoc>> hits = await store.SearchAsync<MyDoc>(
    new Query(Key: null, Value: "how does retrieval augmented generation work?", Limit: 5));

foreach (var hit in hits)
{
    Console.WriteLine($"{hit.Key}  similarity={hit.SimilarityPercentage:F1}%  age={hit.RecencyMinutes:F0}min");
}

// Metadata filter (subset containment)
IReadOnlyList<SearchHit<MyDoc>> filtered = await store.SearchAsync<MyDoc>(
    new Query(Key: null, Value: "RAG",
              MetadataFilter: new Dictionary<string, object> { ["category"] = "technical" }));

// Delete
bool removed = await store.DeleteAsync("doc:001");
```

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.VectorStore.Sql.Postgre]] | `PostgreKVStore` implements `IKVStore` backed by PostgreSQL + pgvector |
| [[Agency.VectorStore.Sql.Sqlite]] | `SqliteKVStore` implements `IKVStore` backed by SQLite + UDF cosine distance |
| [[Agency.Ingestion]] | `DefaultIngestionPipeline` calls `IKVStore.UpsertAsync` to store chunked documents |
| [[Agency.Embeddings.Common]] | Both implementations inject `IEmbeddingGenerator` to produce vectors |
| [[Agency.Mcp.Memory]] | `MemoryTool` calls `UpsertAsync`, `SearchAsync`, and `DeleteAsync` to back MCP memory tools |
