# Agency.KeyValueStore.Sql.Sqlite

SQLite implementation of `IKVStore` for the Agency AI Toolkit. Uses SQLite's `instr()` function for substring key search — no extensions required.

## Install

```
dotnet add package AgencyDotNet.KeyValueStore.Sql.Sqlite
```

## Usage

```csharp
services.AddScoped<IKVStore>(_ =>
    new SqliteKVStore("Data Source=store.sqlite"));

// Store and retrieve
await kvStore.UpsertAsync(key: "memory:session:abc", value: myObject, metadata: tags);
var hits = await kvStore.SearchAsync<MyType>(new Query { Key = "memory:session" });
```

Good for development and low-volume scenarios where a PostgreSQL server isn't available.

Part of the [Agency AI Toolkit](https://github.com/emreaydinceren/Agency.NET) — an open-source .NET agentic AI toolkit.
