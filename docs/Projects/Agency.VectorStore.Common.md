# Agency.VectorStore.Common
#vectorstore #abstractions #interface #search #pgvector #metadata #projects

## What It Is

`Agency.VectorStore.Common` is the shared abstractions library that defines the vector store contract, query/result models, project & document descriptors, metadata utilities, and JSON helpers used by all concrete vector store implementations in the Agency solution. It carries no storage logic of its own — only the types that consumers and backends agree on, including the three-scope (user / session / project) addressing model used by the ingestion and semantic-search feature.

**Namespace:** `Agency.VectorStore.Common`

## API Surface

### Interfaces

#### IVectorStore

```csharp
// File: src/VectorStore/Agency.VectorStore.Common/IVectorStore.cs
public interface IVectorStore
{
    Task UpsertAsync<TValue>(
        string userId,
        string? sessionId,
        string key,
        TValue value,
        IDictionary<string, object>? metadata = null,
        string? projectId = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SearchHit<TValue>>> SearchAsync<TValue>(
        Query query,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(
        string userId,
        string? sessionId,
        string key,
        string? projectId = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ListProjectsAsync(
        string userId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DocumentInfo>> ListDocumentsAsync(
        string userId,
        string? sessionId,
        IReadOnlyList<string>? projectIds,
        CancellationToken cancellationToken = default);
}
```

- `UpsertAsync` inserts or replaces a keyed entry; a `null` `sessionId` is stored as `"*"` (user-global scope), and a `null` `projectId` is stored as `"*"` (global project scope).
- `SearchAsync` returns ranked results for a `Query`, ordered by ascending vector distance.
- `DeleteAsync` returns `true` when an entry was removed, `false` if none existed; `projectId` narrows the delete to a specific project scope.
- `ListProjectsAsync` returns the distinct project identifiers that have at least one stored entry for the user.
- `ListDocumentsAsync` returns the distinct documents (source file + session + project) for the user, optionally restricted to a session and/or a set of project identifiers.

### Value Types

#### Query

```csharp
// File: src/VectorStore/Agency.VectorStore.Common/Query.cs
public record class Query(
    string UserId,
    string? SessionId,
    string? Key,
    string? Value,
    IDictionary<string, object>? MetadataFilter = null,
    int? Limit = 10,
    bool? IncludeMetadataInResults = false,
    IReadOnlyList<string>? ProjectIds = null);
```

`SessionId = null` means search across all sessions for the user. `Key` and `Value` are optional exact-match filters layered on top of ANN search. `Limit` defaults to 10; pass `null` to remove the cap. `ProjectIds` optionally restricts the search to one or more project scopes; `null` leaves the search unscoped by project.

#### DocumentInfo

```csharp
// File: src/VectorStore/Agency.VectorStore.Common/DocumentInfo.cs
public record DocumentInfo(string SourceFile, string SessionId, string ProjectId);
```

Describes a single ingested document as returned by `ListDocumentsAsync` — its originating `SourceFile`, the `SessionId` it was stored under, and the owning `ProjectId`.

#### SearchHit\<TValue\>

```csharp
// File: src/VectorStore/Agency.VectorStore.Common/SearchHit.cs
public record class SearchHit<TValue>(
    string UserId,
    string? SessionId,
    string Key,
    TValue Value,
    Dictionary<string, object>? Metadata,
    double Distance,
    DateTimeOffset UpdatedOn)
{
    public double SimilarityPercentage { get; }  // Math.Max(0, (1.0 - Distance) * 100)
    public double RecencyMinutes       { get; }  // elapsed minutes since UpdatedOn
    public double RecencyHours         { get; }  // elapsed hours since UpdatedOn
}
```

`Distance` is the raw cosine distance returned by pgvector (0 = identical, 2 = opposite). `SimilarityPercentage` converts that to a 0–100 score. `RecencyMinutes` and `RecencyHours` are computed from a `TimeSpan` captured at construction time (`DateTimeOffset.UtcNow - UpdatedOn`).

### Static Helpers

#### SearchHitExtensions

```csharp
// File: src/VectorStore/Agency.VectorStore.Common/SearchHitExtensions.cs
public static class SearchHitExtensions
{
    // Columns: Key, Value, Distance, SimilarityPercentage, UpdatedOn
    public static Dataset ToDataset<TValue>(this IReadOnlyList<SearchHit<TValue>> hits);
}
```

Converts search results into an [[Agency.Common]] `Dataset` for consumption by [[Agency.RagFormatter]].

#### JsonMetadataHelpers

```csharp
// File: src/VectorStore/Agency.VectorStore.Common/JsonMetadataHelpers.cs
public static class JsonMetadataHelpers
{
    // Returns null when metadataJson is null or whitespace.
    public static Dictionary<string, object>? DeserializeMetadata(string? metadataJson);

    // Recursively maps JsonElement to string, long, double, decimal, bool, null,
    // Dictionary<string, object>, or List<object>.
    public static object ConvertJsonElementToObject(JsonElement element);
}
```

Used by concrete implementations ([[Agency.VectorStore.Sql.Postgres]], [[Agency.VectorStore.Sql.Sqlite]]) to deserialize metadata JSON columns back into typed CLR dictionaries after a database read.

## How It Works

1. A caller constructs a `Query` with a `UserId`, optional `SessionId`, optional key/value filters, optional `MetadataFilter`, an optional `ProjectIds` scope, and a `Limit`.
2. The concrete `IVectorStore` implementation converts the query's `Value` text into an embedding vector via [[Agency.Embeddings.Common]], executes an ANN search against the backing store (honoring the session and project scopes), and returns a list of `SearchHit<TValue>` ordered by ascending cosine distance.
3. Each `SearchHit<TValue>` exposes the raw `Distance` plus derived `SimilarityPercentage`, `RecencyMinutes`, and `RecencyHours` for downstream ranking or filtering.
4. Callers that feed results into the RAG pipeline call `.ToDataset()` to produce a `Dataset` accepted by [[Agency.RagFormatter]].
5. To enumerate what has been ingested, callers use `ListProjectsAsync` to discover project scopes and `ListDocumentsAsync` (returning `DocumentInfo` records) to list the source files stored under a user/session/project.
6. Implementations use `JsonMetadataHelpers.DeserializeMetadata` when reading metadata back from JSON-serialized storage columns.

```csharp
using Agency.Common;
using Agency.VectorStore.Common;

// Store a document for a specific user, session, and project
await store.UpsertAsync(
    userId: "user_123",
    sessionId: "chat_456",
    key: "doc:001",
    value: "Introduction to RAG",
    metadata: new Dictionary<string, object> { ["category"] = "technical" },
    projectId: "proj_handbook");

// Semantic search across all sessions, scoped to one project
IReadOnlyList<SearchHit<string>> hits = await store.SearchAsync<string>(new Query(
    UserId: "user_123",
    SessionId: null,
    Key: null,
    Value: "how does retrieval augmented generation work?",
    Limit: 5,
    ProjectIds: new[] { "proj_handbook" }));

foreach (SearchHit<string> hit in hits)
{
    Console.WriteLine($"[{hit.SessionId}] {hit.Key} similarity={hit.SimilarityPercentage:F1}%");
}

// Enumerate ingested projects and documents
IReadOnlyList<string> projects = await store.ListProjectsAsync("user_123");
IReadOnlyList<DocumentInfo> docs = await store.ListDocumentsAsync(
    userId: "user_123",
    sessionId: null,
    projectIds: projects);

// Convert hits to a Dataset for RAG formatting
Dataset table = hits.ToDataset();
```

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.Common]] | Provides `Dataset` and `IColumnMetadata`; `SearchHitExtensions.ToDataset` returns a `Dataset` |
| [[Agency.Embeddings.Common]] | Project dependency; concrete implementations use `IEmbeddingGenerator` to vectorize query text before ANN search |
| [[Agency.VectorStore.Sql.Postgres]] | Implements `IVectorStore` using PostgreSQL + pgvector; uses `JsonMetadataHelpers` for metadata round-tripping |
| [[Agency.VectorStore.Sql.Sqlite]] | Implements `IVectorStore` using SQLite; uses `JsonMetadataHelpers` for metadata round-tripping |
| [[Agency.RagFormatter]] | Consumes the `Dataset` produced by `ToDataset()` to render search results as Markdown context for LLM prompts |
| [[Agency.Mcp.Memory]] | Registers and uses an `IVectorStore` implementation to back agent memory tools |

## Design Notes

- The `null` session-ID and `null` project-ID conventions are encoded at the interface level: callers pass `null` for user-global / global-project entries, and implementations are responsible for storing and querying the sentinel value `"*"`. This keeps the storage scoping model out of consumer code while still expressing three independent scopes (user, session, project).
- Project scoping is expressed as an optional `IReadOnlyList<string>? ProjectIds` on `Query` rather than a single id, so a single search can span several projects without forcing callers into multiple round-trips; `null` deliberately means "unscoped" rather than "global project only".
- `DocumentInfo` is a deliberately flat descriptor (source file + session + project) returned by `ListDocumentsAsync`, giving the ingestion/REPL tooling enough to list and re-scope documents without exposing chunk-level storage rows.
- `SearchHit<TValue>` is generic so implementations can deserialize stored JSON directly into a caller-supplied type, avoiding an extra projection step in consumer code.
- `SimilarityPercentage` uses `Math.Max(0, ...)` to guard against floating-point rounding that could produce a marginally negative value from a cosine distance slightly above 1.0.
- `RecencyMinutes` and `RecencyHours` derive from a `TimeSpan` captured once at construction (`DateTimeOffset.UtcNow - UpdatedOn`), so the values are stable for the lifetime of the `SearchHit` instance rather than drifting on each property access.
- `JsonMetadataHelpers` is a standalone static class (no DI) so it can be shared by any implementation without requiring a service registration, and it lives in the abstractions project to eliminate duplication across the PostgreSQL and SQLite implementations.
- The `Npgsql` and `pgvector` NuGet packages are referenced in this project's `.csproj`, which is unusual for an abstractions layer. Concrete implementations inherit these transitively rather than declaring them independently.
