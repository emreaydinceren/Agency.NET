# Agency.Sql.Sqlite

#sql #sqlite #lightweight #observability

## What It Is

`Agency.Sql.Sqlite` is the SQLite counterpart of [[Agency.Sql.Postgre]]. It provides:

1. **`SqliteRunner`** — async SQL runner over `Microsoft.Data.Sqlite`, returning [[Agency.Common]] `Dataset` objects.
2. **`SQLQueryEmbedder`** — identical `vectorize('text')` placeholder substitution as the PostgreSQL variant.

SQLite is useful for local development, testing, and edge deployments where standing up a PostgreSQL server is impractical.

## `SqliteRunner`

A key design difference from `PostgreSqlRunner`: `SqliteRunner` accepts an optional `onConnectionOpen` callback invoked after every connection is opened. This is used to register SQLite user-defined functions (UDFs) such as the `vec_distance_cosine` function required by [[Agency.VectorStore.Sql.Sqlite]].

```csharp
var runner = new SqliteRunner(
    connectionString: "Data Source=agency.db",
    onConnectionOpen: SqliteKVStore.RegisterVectorFunctions, // registers vec_distance_cosine
    logger: loggerFactory.CreateLogger<SqliteRunner>());

// DDL
await runner.ExecuteAsync("CREATE TABLE IF NOT EXISTS docs (id INTEGER PRIMARY KEY, content TEXT)");

// Query → Dataset
Dataset dataset = await runner.QueryAsync("SELECT id, content FROM docs");

// Query → strongly-typed
List<string> contents = await runner.QueryAsync<string>(
    "SELECT content FROM docs",
    predicate: reader => Task.FromResult(reader.GetString(0)));
```

## `SQLQueryEmbedder`

Identical API to the PostgreSQL variant:

```csharp
var embedder = new SQLQueryEmbedder(embeddingGenerator);
string sql = "SELECT key, value FROM semantic_kv_store ORDER BY vec_distance_cosine(embedding, vectorize('search text')) LIMIT 5";
string resolved = await embedder.EmbedVectorsInQueryAsync(sql);
```

> **Note:** The SQLite vector store uses a custom UDF (`vec_distance_cosine`) rather than a native operator, so the macro expands to the same `[f1,f2,...]` literal format but is called inside the UDF rather than a `<=>` operator.

## Observability

Same OpenTelemetry pattern as [[Agency.Sql.Postgre]]:

- **Activity** `sqlite.execute` / `sqlite.query`
- **Counter** `sqlite.executions` (tags: `operation`, `status`)
- **Histogram** `sqlite.duration` (ms)

ActivitySource name: `Agency.Sql.Sqlite` | Meter name: `Agency.Sql.Sqlite`

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.Sql.Common]] | `SqliteRunner` extends `SqlRunnerBase`; inherits `ExecuteAsync` / `QueryAsync` with OTel |
| [[Agency.Common]] | Returns `Dataset`; `IColumnMetadata` adapter lives in `Agency.Sql.Common` |
| [[Agency.Embeddings.Common]] | `SQLQueryEmbedder` injects `IEmbeddingGenerator` |
| [[Agency.VectorStore.Sql.Sqlite]] | `SqliteKVStore` delegates all SQL to `SqliteRunner` |
| [[Agency.RagFormatter]] | Formats the `Dataset` returned by `QueryAsync` |
