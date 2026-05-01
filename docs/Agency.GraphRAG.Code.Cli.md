# Agency.GraphRAG.Code.Cli

#cli #graphrag #code-index #sqlite #postgres

## What It Is

Agency.GraphRAG.Code.Cli is the command-line executable that drives the GraphRAG code-index pipeline, exposing `index` and `query` subcommands that build or query a code graph against a SQLite or PostgreSQL store.

**Namespace:** `Agency.GraphRAG.Code.Cli`

## Prerequisites

- A running SQLite file (created automatically on first use) **or** a reachable PostgreSQL instance when `--store postgres` is used.
- The repository path supplied to `index` must be accessible on the local file system.

## API Surface

`CliApplication` and `CliInvocation` are the only public types. `Program` is internal.

```csharp
// File: src/GraphRAG.Code/Agency.GraphRAG.Code.Cli/CliApplication.cs
using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Storage;
using System.CommandLine;

namespace Agency.GraphRAG.Code.Cli;

public static class CliApplication
{
    // Builds the System.CommandLine root command.
    // workingDirectory is used for the default SQLite file path.
    public static RootCommand BuildRootCommand(string workingDirectory);

    // Resolves a CliInvocation for the index subcommand.
    public static CliInvocation CreateIndexInvocation(
        string repo, string store, string? connection, string workingDirectory);

    // Resolves a CliInvocation for the query subcommand.
    public static CliInvocation CreateQueryInvocation(
        string question, string store, string? connection, int topK, string workingDirectory);
}

// Immutable record carrying all resolved runtime settings for one CLI invocation.
public sealed record CliInvocation(
    CodeIndexOptions Options,
    Repo? Repo = null,
    string? Question = null,
    int TopK = 5);
```

## How It Works

`Program.Main` calls `CliApplication.BuildRootCommand(Directory.GetCurrentDirectory()).Invoke(args)`.

`BuildRootCommand` registers two subcommands that share `--store` (default `sqlite`) and `--connection` options:

| Subcommand | Arguments | Extra options | Action |
|---|---|---|---|
| `index` | `<repo>` path | — | Initializes `IGraphStore` schema, then runs `IndexingPipeline.RunAsync`. |
| `query` | `<question>` text | `--top-k` (default `5`) | Initializes `IGraphStore` schema, runs `QueryPipeline.ExecuteAsync`, prints `QueryResponse.Answer`. |

Each handler calls the private `CreateHost(invocation)`, which builds a `Microsoft.Extensions.Hosting.IHost` and registers all GraphRAG services via `AddCodeIndex` from [[Agency.GraphRAG.Code]].

### Store selection

`BuildOptions` (private) resolves the store like this:

- `sqlite` → `ConnectionString` defaults to `Data Source=<workingDirectory>/graphrag-code.db` when `--connection` is omitted; `SqlitePath` is set to the same path.
- `postgres` → `--connection` is required; throws `InvalidOperationException` if absent.

### Example invocations

```powershell
Agency.GraphRAG.Code.Cli index E:\Repos\Agency --store sqlite
Agency.GraphRAG.Code.Cli query "Where is RepoWalker used?" --store postgres --connection "Host=localhost;Database=dev_db;Username=dev_user;Password=dev_password"
```

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.GraphRAG.Code]] | Supplies `AddCodeIndex`, `IndexingPipeline`, `QueryPipeline`, `IGraphStore`, `Repo`, and `CodeIndexOptions`. |
| [[Agency.GraphRAG.Code.Sqlite]] | Provides the SQLite-backed graph store registered when `--store sqlite` is selected. |
| [[Agency.GraphRAG.Code.Postgres]] | Provides the PostgreSQL-backed graph store registered when `--store postgres` is selected. |

## Design Notes

- `CliInvocation` is a positional primary-constructor record rather than a mutable options bag — this makes handler code read-only by construction and avoids accidental mutation between host creation and pipeline execution.
- The host is created and disposed inside each handler lambda so that each invocation gets a fresh DI container; this avoids cross-command state leakage in tests that call `BuildRootCommand` and `Invoke` directly.
- `--top-k` is parsed and stored on `CliInvocation.TopK` but `QueryPipeline.ExecuteAsync` currently does not accept it as a parameter; the value is wired through `CliInvocation` so it is available when the pipeline gains that capability.
