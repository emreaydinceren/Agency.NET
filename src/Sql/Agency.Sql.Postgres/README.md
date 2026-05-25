# Agency.Sql.Postgres

PostgreSQL SQL runner for the Agency AI Toolkit.

## Install

```
dotnet add package Agency.Sql.Postgres
```

## Configuration

```json
{
  "ConnectionStrings": {
    "Default": "Host=localhost;Database=mydb;Username=myuser;Password=mypassword"
  }
}
```

## Usage

```csharp
services.AddSingleton(sp =>
    new PostgreSqlRunner(config.GetConnectionString("Default")!));

// Run a query
Dataset result = await runner.QueryAsync("SELECT * FROM documents LIMIT 10");
```

Part of the [Agency AI Toolkit](https://github.com/emre/Agency) — an open-source .NET RAG pipeline.
