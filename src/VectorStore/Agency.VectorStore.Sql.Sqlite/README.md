# Agency.VectorStore.Sql.Sqlite

SQLite implementation of `IVectorStore` for the Agency AI Toolkit. Uses JSON-array TEXT columns and a cosine similarity user-defined function — no native vector extension required.

## Install

```
dotnet add package AgencyDotNet.VectorStore.Sql.Sqlite
```

## Usage

```csharp
services.AddScoped<IVectorStore>(_ =>
    new SqliteKVStore("Data Source=vectors.sqlite"));

// Upsert and search
await vectorStore.UpsertAsync(key, value, embedding, metadata);
var hits = await vectorStore.SearchAsync<MyType>(new Query
{
    Value = queryEmbedding,
    Limit = 5
});
```

Good for development and low-volume scenarios where a PostgreSQL server isn't available.

Part of the [Agency AI Toolkit](https://github.com/emreaydinceren/Agency.NET) — an open-source .NET RAG pipeline.
