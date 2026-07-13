# Agency.Sql.Sqlite

SQLite SQL runner for the Agency AI Toolkit.

## Install

```
dotnet add package AgencyDotNet.Sql.Sqlite
```

## Usage

```csharp
services.AddSingleton(_ =>
    new SqliteRunner("Data Source=mydb.sqlite"));

// Optionally run setup on each new connection
services.AddSingleton(_ =>
    new SqliteRunner("Data Source=mydb.sqlite",
        onConnectionOpen: conn => conn.EnableExtensions(true)));

// Run a query
Dataset result = await runner.QueryAsync("SELECT * FROM documents LIMIT 10");
```

Part of the [Agency AI Toolkit](https://github.com/emreaydinceren/Agency.NET) — an open-source .NET RAG pipeline.
