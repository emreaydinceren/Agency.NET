# Agency.VectorStore.Sql.Postgre

PostgreSQL + pgvector implementation of `IVectorStore` for the Agency AI Toolkit.

## Install

```
dotnet add package Agency.VectorStore.Sql.Postgre
```

## Prerequisites

PostgreSQL with the [pgvector](https://github.com/pgvector/pgvector) extension enabled.

```sql
CREATE EXTENSION IF NOT EXISTS vector;
```

## Usage

```csharp
services.AddScoped<IVectorStore>(sp =>
    new PostgreKVStore(config.GetConnectionString("Default")!));

// Initialize schema on startup
await vectorStore.InitializeSchemaAsync();

// Upsert and search
await vectorStore.UpsertAsync(key, value, embedding, metadata);
var hits = await vectorStore.SearchAsync<MyType>(new Query
{
    Value = queryEmbedding,
    Limit = 5
});
```

Part of the [Agency AI Toolkit](https://github.com/emre/Agency) — an open-source .NET RAG pipeline.
