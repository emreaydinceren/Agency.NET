# Agency.KeyValueStore.Sql.Postgres

PostgreSQL implementation of `IKVStore` for the Agency AI Toolkit. Uses `ILIKE` for substring key search — no vector extension required.

## Install

```
dotnet add package Agency.KeyValueStore.Sql.Postgres
```

## Usage

```csharp
services.AddScoped<IKVStore>(sp =>
    new PostgresKVStore(config.GetConnectionString("Default")!));

// Store and retrieve
await kvStore.UpsertAsync(key: "memory:session:abc", value: myObject, metadata: tags);
var hits = await kvStore.SearchAsync<MyType>(new Query { Key = "memory:session" });
```

Part of the [Agency AI Toolkit](https://github.com/emre/Agency) — an open-source .NET agentic AI toolkit.
