# Agency.VectorStore.Common

#vectorstore #abstractions #interface #search

## What It Is

Agency.VectorStore.Common defines the [[IVectorStore]] interface — a semantic key-value store abstraction where each value is stored alongside its embedding vector, enabling both exact-key lookup and approximate-nearest-neighbor (ANN) similarity search.

The project also provides shared query/result models plus helpers for metadata JSON conversion and Dataset projection.

## Key Types

### [[IVectorStore]]

```csharp
public interface IVectorStore
{
    Task UpsertAsync<TValue>(
        string userId,
        string? sessionId,
        string key,
        TValue value,
        IDictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SearchHit<TValue>>> SearchAsync<TValue>(
        Query query,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(
        string userId,
        string? sessionId,
        string key,
        CancellationToken cancellationToken = default);
}
```

### Query

```csharp
public record class Query(
    string UserId,                              // owner of the entries
    string? SessionId,                          // optional session scope; null means all user sessions
    string? Key,                                // optional exact key filter
    string? Value,                              // optional value/search text
    IDictionary<string, object>? MetadataFilter = null,
    int? Limit = 10,
    bool? IncludeMetadataInResults = false);
```

### SearchHit<TValue>

```csharp
public record class SearchHit<TValue>(
    string UserId,
    string? SessionId,
    string Key,
    TValue Value,
    Dictionary<string, object>? Metadata,
    double Distance,                            // distance metric (interpretation depends on implementation)
    DateTimeOffset UpdatedOn)
{
    public double SimilarityPercentage { get; }  // (1.0 - Distance) * 100
    public double RecencyMinutes       { get; }  // minutes since UpdatedOn
    public double RecencyHours         { get; }  // hours since UpdatedOn
}
```

### SearchHitExtensions

```csharp
// Convert search results to a Dataset for use with Agency.RagFormatter
IReadOnlyList<SearchHit<string>> hits = await store.SearchAsync<string>(query);
Dataset table = hits.ToDataset();
// Columns: Key, Value, Distance, SimilarityPercentage, UpdatedOn
```

### JsonMetadataHelpers

Static helpers for round-tripping metadata through JSON while preserving CLR types:

```csharp
// Deserialize JSON → Dictionary<string, object>
Dictionary<string, object>? meta = JsonMetadataHelpers.DeserializeMetadata(jsonString);

// Convert JsonElement to string, long, double, decimal, bool, null, Dictionary, or List
object clrValue = JsonMetadataHelpers.ConvertJsonElementToObject(element);
```

## Usage

```csharp
// Store a document for a specific user and session
await store.UpsertAsync(
    userId: "user_123",
    sessionId: "chat_456",
    key: "doc:001",
    value: new { Title = "Introduction to RAG", Body = "..." },
    metadata: new Dictionary<string, object> { ["category"] = "technical" });

// Semantic search across all sessions for a user
var hits = await store.SearchAsync<MyDoc>(new Query(
    UserId: "user_123",
    SessionId: null,
    Key: null,
    Value: "how does retrieval augmented generation work?",
    Limit: 5));

foreach (var hit in hits)
{
    Console.WriteLine($"[{hit.SessionId}] {hit.Key} similarity={hit.SimilarityPercentage:F1}%");
}
```

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.VectorStore.Sql.Postgre]] | Implements [[IVectorStore]] using PostgreSQL + pgvector |
| [[Agency.VectorStore.Sql.Sqlite]] | Implements [[IVectorStore]] using SQLite + vector extensions |
| [[Agency.Common]] | SearchHitExtensions produces Dataset which is defined in this project |
| [[Agency.Ingestion]] | Uses [[IVectorStore]] in `IIngestionPipeline` and `DefaultIngestionPipeline` |
