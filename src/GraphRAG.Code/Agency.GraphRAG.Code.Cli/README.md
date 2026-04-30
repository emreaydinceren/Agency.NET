# Agency.GraphRAG.Code.Cli

Command-line entry point for indexing a repository into the GraphRAG code graph and querying that graph.

## Commands

### SQLite backend

Index a repo into the default SQLite file in the current working directory:

```powershell
dotnet run --project src\GraphRAG.Code\Agency.GraphRAG.Code.Cli -- index E:\Repos\Agency
```

Index a repo with an explicit SQLite connection string:

```powershell
dotnet run --project src\GraphRAG.Code\Agency.GraphRAG.Code.Cli -- index E:\Repos\Agency --store sqlite --connection "Data Source=E:\Repos\Agency\graphrag-code.db"
```

Query the SQLite-backed index:

```powershell
dotnet run --project src\GraphRAG.Code\Agency.GraphRAG.Code.Cli -- query "How does QueryPipeline assemble context?" --store sqlite
```

### PostgreSQL backend

Index a repo into PostgreSQL:

```powershell
dotnet run --project src\GraphRAG.Code\Agency.GraphRAG.Code.Cli -- index E:\Repos\Agency --store postgres --connection "Host=localhost;Port=5432;Database=dev_db;Username=dev_user;Password=dev_password"
```

Query the PostgreSQL-backed index:

```powershell
dotnet run --project src\GraphRAG.Code\Agency.GraphRAG.Code.Cli -- query "Where is RepoWalker used?" --store postgres --connection "Host=localhost;Port=5432;Database=dev_db;Username=dev_user;Password=dev_password"
```

## Notes

- `index <repo>` initializes the selected `IGraphStore` schema, then runs `IndexingPipeline.RunAsync`.
- `query <question>` initializes the selected `IGraphStore` schema, then runs `QueryPipeline.ExecuteAsync`.
- `--store` defaults to `sqlite`.
- `--connection` is optional for SQLite and required for PostgreSQL.
- When SQLite is selected and `--connection` is omitted, the CLI uses `Data Source=<working-directory>\graphrag-code.db`.
- `query` accepts `--top-k`, but the current implementation stores that value on `CliInvocation` without forwarding it into `QueryPipeline`.
