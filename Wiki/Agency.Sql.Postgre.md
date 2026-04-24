# Agency.Sql.Postgre

#sql #postgresql #pgvector #observability

## What It Is

`Agency.Sql.Postgre` provides two classes for working with PostgreSQL (including the `pgvector` extension):

1. **`PostgreSqlRunner`** — a general-purpose async SQL runner that returns [[Agency.Common]] `Dataset` objects.
2. **`SQLQueryEmbedder`** — a preprocessor that replaces `vectorize('text')` placeholders in SQL strings with real embedding vectors before execution.

## `PostgreSqlRunner`

```csharp
var runner = new PostgreSqlRunner(
    connectionString: "Host=localhost;Database=dev_db;Username=dev_user;Password=dev_password",
    logger: loggerFactory.CreateLogger<PostgreSqlRunner>());

// DDL / DML
await runner.ExecuteAsync("CREATE TABLE IF NOT EXISTS docs (id serial PRIMARY KEY, content text)");

// Query → Dataset
Dataset results = await runner.QueryAsync(
    "SELECT id, content FROM docs WHERE id = @id",
    parameters: new() { ["id"] = 42 });

// Query → strongly-typed list
List<Doc> docs = await runner.QueryAsync<Doc>(
    "SELECT id, content FROM docs",
    predicate: async reader => new Doc(reader.GetInt32(0), reader.GetString(1)));
```

Every method opens a fresh `NpgsqlDataSource` with `UseVector()` enabled, so `pgvector` column types are transparently supported.

## `SQLQueryEmbedder`

Allows writing semantic vector queries in plain SQL using a `vectorize('...')` macro that is resolved at runtime:

```csharp
var embedder = new SQLQueryEmbedder(embeddingGenerator);

string rawSql = """
    SELECT title, body
    FROM documents
    ORDER BY embedding <-> vectorize('what is RAG?')
    LIMIT 5
    """;

string embeddedSql = await embedder.EmbedVectorsInQueryAsync(rawSql);
// embedding <-> vectorize('what is RAG?')  becomes:
// embedding <-> '[0.023,-0.411,...]'::vector

Dataset results = await runner.QueryAsync(embeddedSql);
```

The regex handles escaped single-quotes inside the text (`''` → `'`) and multiple `vectorize()` calls in a single query, replacing them right-to-left to preserve character offsets.

## Observability

`PostgreSqlRunner` instruments every call with:

- **Activity** `postgresql.execute` / `postgresql.query` tagged with `db.system`, `db.operation`, `db.statement`
- **Counter** `postgresql.executions` (tags: `operation`, `status`)
- **Histogram** `postgresql.duration` (ms)

ActivitySource name: `Agency.Sql.Postgre` | Meter name: `Agency.Sql.Postgre`

## Infrastructure

PostgreSQL runs via Docker (`src/docker-compose.yml`):

```yaml
# Connection details for local dev:
Host=localhost; Port=5432; Database=dev_db; Username=dev_user; Password=dev_password
```

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.Sql.Common]] | `PostgreSqlRunner` extends `SqlRunnerBase`; inherits `ExecuteAsync` / `QueryAsync` with OTel |
| [[Agency.Common]] | Returns `Dataset`; `IColumnMetadata` adapter lives in `Agency.Sql.Common` |
| [[Agency.Embeddings.Common]] | `SQLQueryEmbedder` injects `IEmbeddingGenerator` |
| [[Agency.VectorStore.Sql.Postgre]] | `PostgreKVStore` delegates all SQL to `PostgreSqlRunner` |
| [[Agency.RagFormatter]] | Formats the `Dataset` returned by `QueryAsync` |
