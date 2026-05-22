# Agency.KeyValueStore.Sql.Postgre

PostgreSQL implementation of `IKVStore` for the Agency AI Toolkit. Uses `ILIKE` for substring key search — no vector extension required.

## Install

```
dotnet add package Agency.KeyValueStore.Sql.Postgre
```

## Usage

```csharp
services.AddScoped<IKVStore>(sp =>
    new PostgreKVStore(config.GetConnectionString("Default")!));

// Store and retrieve
await kvStore.UpsertAsync(key: "memory:session:abc", value: myObject, metadata: tags);
var hits = await kvStore.SearchAsync<MyType>(new Query { Key = "memory:session" });
```

Part of the [Agency AI Toolkit](https://github.com/emre/Agency) — an open-source .NET agentic AI toolkit.
