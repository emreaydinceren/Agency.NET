# Agency.GraphRAG.Code

#graphrag #code-index #codegraph #rag

## What It Is

Agency.GraphRAG.Code is the core library that builds and queries a graph-structured index of source code repositories, enabling natural-language questions to be answered by walking a git-backed repo, chunking symbols, parsing manifest dependencies, resolving call sites, clustering related symbols, and synthesising answers through an LLM pipeline.

**Namespace:** `Agency.GraphRAG.Code`

## API Surface

### Interfaces

```csharp
// File: src/GraphRAG.Code/Agency.GraphRAG.Code/Agentic/ICodeIndex.cs
using System.Threading;
using System.Threading.Tasks;

namespace Agency.GraphRAG.Code.Agentic;

public interface ICodeIndex
{
    Task<string> AskAsync(string question, int topK = 5, CancellationToken cancellationToken = default);
}
```

```csharp
// File: src/GraphRAG.Code/Agency.GraphRAG.Code/Storage/IGraphStore.cs
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Agency.GraphRAG.Code.Domain;

namespace Agency.GraphRAG.Code.Storage;

public interface IGraphStore
{
    Task InitializeSchemaAsync(CancellationToken cancellationToken = default);
    Task UpsertRepoAsync(Repo repo, CancellationToken cancellationToken = default);
    Task UpsertProjectAsync(Project project, CancellationToken cancellationToken = default);
    Task UpsertExternalPackageBatchAsync(IReadOnlyList<ExternalPackage> packages, CancellationToken cancellationToken = default);
    Task UpsertFileAsync(SourceFile file, CancellationToken cancellationToken = default);
    Task UpsertModuleAsync(Module module, CancellationToken cancellationToken = default);
    Task UpsertSymbolAsync(Symbol symbol, CancellationToken cancellationToken = default);
    Task UpsertSymbolBatchAsync(IReadOnlyList<Symbol> symbols, CancellationToken cancellationToken = default);
    Task UpsertEdgeBatchAsync(IReadOnlyList<Edge> edges, CancellationToken cancellationToken = default);
    Task DeleteSymbolsByFileAsync(Guid fileId, CancellationToken cancellationToken = default);
    Task DeleteFileAsync(Guid fileId, CancellationToken cancellationToken = default);
    Task RenameFileAsync(Guid fileId, string newPath, CancellationToken cancellationToken = default);
    Task<string?> LoadIndexedCommitAsync(Guid repoId, CancellationToken cancellationToken = default);
    Task SetIndexedCommitAsync(Guid repoId, string indexedCommit, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<VectorSearchResult>> VectorSearchSymbolsAsync(string queryText, int topK, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<VectorSearchResult>> VectorSearchClustersAsync(string queryText, int topK, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TraversalHop>> TraverseFromAsync(TraversalRequest request, CancellationToken cancellationToken = default);
    Task<Symbol?> GetSymbolByIdAsync(Guid symbolId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Symbol>> FindSymbolsByNameAsync(string name, CancellationToken cancellationToken = default);
    Task StageUnresolvedCallSiteBatchAsync(IReadOnlyList<UnresolvedCallSite> callSites, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<UnresolvedCallSite>> DrainUnresolvedCallSitesAsync(Guid? sourceFileId = null, CancellationToken cancellationToken = default);
    Task ApplyClusterAssignmentsAsync(IReadOnlyDictionary<Guid, ValueTuple<Guid, string>> assignments, CancellationToken cancellationToken = default);
    Task ReplaceClusterSummariesAtomicallyAsync(IReadOnlyList<Cluster> clusters, CancellationToken cancellationToken = default);
}
```

```csharp
// File: src/GraphRAG.Code/Agency.GraphRAG.Code/Chunker/IChunker.cs
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Agency.GraphRAG.Code.Chunker;

public interface IChunker
{
    Task<IReadOnlyList<Chunk>> ChunkAsync(ChunkerInput input, CancellationToken cancellationToken = default);
}
```

```csharp
// File: src/GraphRAG.Code/Agency.GraphRAG.Code/Manifest/ManifestParserOrchestrator.cs
using Agency.GraphRAG.Code.Domain;

namespace Agency.GraphRAG.Code.Manifest;

public interface IManifestParser
{
    bool CanParse(string manifestRelativePath);
    Task<IReadOnlyList<ManifestProjectDefinition>> ParseAsync(ManifestParserContext context, CancellationToken cancellationToken = default);
}
```

```csharp
// File: src/GraphRAG.Code/Agency.GraphRAG.Code/Query/IClusterQuerySource.cs
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Agency.GraphRAG.Code.Query;

public interface IClusterQuerySource
{
    Task<IReadOnlyList<Cluster>> GetClustersAsync(IReadOnlyList<Guid> clusterIds, CancellationToken cancellationToken = default);
}
```

```csharp
// File: src/GraphRAG.Code/Agency.GraphRAG.Code/Query/ISymbolTextProvider.cs
using Agency.GraphRAG.Code.Domain;

namespace Agency.GraphRAG.Code.Query;

public interface ISymbolTextProvider
{
    Task<string?> LoadAsync(Symbol symbol, CancellationToken cancellationToken = default);
}
```

```csharp
// File: src/GraphRAG.Code/Agency.GraphRAG.Code/Cluster/ClusterWorker.cs
using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Storage;

namespace Agency.GraphRAG.Code.Cluster;

public interface IClusterGraphProvider
{
    Task<ClusterWorkspace> LoadAsync(CancellationToken cancellationToken = default);
}
```

### Domain Records

```csharp
// File: src/GraphRAG.Code/Agency.GraphRAG.Code/Domain/Repo.cs
namespace Agency.GraphRAG.Code.Domain;

public record class Repo
{
    public required Guid Id { get; init; }
    public string? RemoteUrl { get; init; }
    public required string LocalPath { get; init; }
    public required bool IsShallow { get; init; }
    public string? IndexedCommit { get; init; }
    public DateTimeOffset? IndexedAt { get; init; }
}
```

```csharp
// File: src/GraphRAG.Code/Agency.GraphRAG.Code/Domain/Symbol.cs
namespace Agency.GraphRAG.Code.Domain;

public record class Symbol
{
    public required Guid Id { get; init; }
    public required Guid FileId { get; init; }
    public Guid? ModuleId { get; init; }
    public required string Name { get; init; }
    public string? FullyQualifiedName { get; init; }
    public required SymbolKind Kind { get; init; }
    public string? Signature { get; init; }
    public string? Summary { get; init; }
    public string? OneLineSummary { get; init; }
    public string? ContentHash { get; init; }
    public float[]? Embedding { get; init; }
    public required bool IsUtility { get; init; }
    public required int SourceRangeStart { get; init; }
    public required int SourceRangeEnd { get; init; }
}
```

```csharp
// File: src/GraphRAG.Code/Agency.GraphRAG.Code/Domain/Edge.cs
namespace Agency.GraphRAG.Code.Domain;

public record class Edge
{
    public required Guid Id { get; init; }
    public required Guid SourceId { get; init; }
    public required string SourceKind { get; init; }
    public required Guid TargetId { get; init; }
    public required string TargetKind { get; init; }
    public required EdgeKind EdgeKind { get; init; }
    public required double Confidence { get; init; }
    public required IReadOnlyList<Signal> Signals { get; init; }
    public required IReadOnlyDictionary<string, object?> Properties { get; init; }
    public string? MemberKind { get; }
}
```

```csharp
// File: src/GraphRAG.Code/Agency.GraphRAG.Code/Domain/Cluster.cs
namespace Agency.GraphRAG.Code.Domain;

public record class Cluster
{
    public required Guid Id { get; init; }
    public required string Label { get; init; }
    public required ClusterType Type { get; init; }
    public required double CoherenceScore { get; init; }
    public string? Summary { get; init; }
    public float[]? Embedding { get; init; }
}
```

### Enumerations

```csharp
// File: src/GraphRAG.Code/Agency.GraphRAG.Code/Domain/EdgeKind.cs
namespace Agency.GraphRAG.Code.Domain;

public enum EdgeKind { Contains, DependsOn, Imports, References, Defines, MemberOf }
```

```csharp
// File: src/GraphRAG.Code/Agency.GraphRAG.Code/Domain/SymbolKind.cs
namespace Agency.GraphRAG.Code.Domain;

public enum SymbolKind { Namespace, Class, Struct, Interface, Enum, Method, Function, Property, Field }
```

```csharp
// File: src/GraphRAG.Code/Agency.GraphRAG.Code/Walker/Language.cs
namespace Agency.GraphRAG.Code.Walker;

public enum Language { Unknown, CSharp, TypeScript, Tsx, JavaScript, Jsx, Python }
```

```csharp
// File: src/GraphRAG.Code/Agency.GraphRAG.Code/Query/QueryCategory.cs
namespace Agency.GraphRAG.Code.Query;

public enum QueryCategory { Local, Subsystem, Global, Impact, Dependency }
```

### Configuration

```csharp
// File: src/GraphRAG.Code/Agency.GraphRAG.Code/DependencyInjection/CodeIndexOptions.cs
namespace Agency.GraphRAG.Code.DependencyInjection;

public enum CodeIndexStore { Sqlite, Postgres }

public sealed class CodeIndexOptions
{
    public CodeIndexStore Store { get; set; } = CodeIndexStore.Sqlite;
    public string? ConnectionString { get; set; }
    public string? SqlitePath { get; set; }
    public string WorkingDirectory { get; set; }
    public string DefaultSqliteFileName { get; set; }
}
```

### Query types

```csharp
// File: src/GraphRAG.Code/Agency.GraphRAG.Code/Query/QueryOptions.cs
namespace Agency.GraphRAG.Code.Query;

public sealed class QueryOptions
{
    public string CheapestModel { get; init; }
    public string AnswerModel { get; init; }
    public int ContextTokenBudget { get; init; }
}
```

```csharp
// File: src/GraphRAG.Code/Agency.GraphRAG.Code/Query/QueryResponse.cs
namespace Agency.GraphRAG.Code.Query;

public sealed record QueryResponse
{
    public required string Answer { get; init; }
    public required QueryPlan Plan { get; init; }
    public required QueryContextAssembly Context { get; init; }
}
```

## Registration

```csharp
// File: src/GraphRAG.Code/Agency.GraphRAG.Code/DependencyInjection/CodeIndexServiceCollectionExtensions.cs
using Agency.GraphRAG.Code.Agentic;
using Agency.GraphRAG.Code.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

// Minimal registration — SQLite, no LLM or embeddings (uses ZeroEmbeddingGenerator + NullChatClient fallbacks)
services.AddCodeIndex(options =>
{
    options.Store = CodeIndexStore.Sqlite;
    options.SqlitePath = @"C:\data\graphrag-code.db";
});

// Full registration — PostgreSQL, real LLM and embeddings supplied by the host
services.AddCodeIndex(options =>
{
    options.Store = CodeIndexStore.Postgres;
    options.ConnectionString = "Host=localhost;Database=graphrag;Username=dev_user;Password=dev_password";
});
// IEmbeddingGenerator and IChatClient must be registered separately by the host before calling AddCodeIndex.

// Resolve and use
ICodeIndex codeIndex = serviceProvider.GetRequiredService<ICodeIndex>();
string answer = await codeIndex.AskAsync("What does QueryPipeline do?");
```

`AddCodeIndex` registers these services with `TryAddSingleton`, so any host-registered override takes precedence. Default fallbacks are `ZeroEmbeddingGenerator`, `NullChatClient`, `EmptyClusterQuerySource`, and `EmptySymbolTextProvider`.

## How It Works

### Indexing (`IndexingPipeline.RunAsync`)

1. **`RepoWalker.WalkAsync`** — inspects git state to produce a `WalkResult` containing `WalkedFile` entries. The walk mode is `Full` for first-time or shallow repos, `Incremental` when the previously indexed commit is an ancestor of `HEAD`, or `RecoveryFull` otherwise.
2. **`ManifestParserOrchestrator.ParseAsync`** — discovers `.csproj`, `package.json`, and `pyproject.toml` files; uses `IManifestParser` implementations to upsert `Project`, `ExternalPackage`, and `DependsOn` edges into `IGraphStore`.
3. **Build `Phase1WriteRequest` values** — each supported source file is chunked by `ChunkerDispatcher` (`CSharpChunker`, `TypeScriptChunker`, or `PythonChunker`) into `Chunk` records.
4. **`SymbolSummarizer.SummarizeAsync`** — generates one-line and detailed summaries for each chunk via `IChatClient`, caches by SHA-256 content hash, and produces embeddings via `IEmbeddingGenerator`.
5. **`ChangeDetector.Detect`** — compares stored symbol hashes against current chunk hashes to produce a `ChangeSet` (added, modified, deleted, renamed files).
6. **`IncrementalHydrator.HydrateAsync`** — applies the `ChangeSet`: deletes stale files, renames files, runs `Phase1Writer` for new/changed files (upserts `SourceFile`, `Module`, `Symbol`, and `Contains`/`Imports`/`Defines` edges; stages `UnresolvedCallSite` records), then runs `Phase2Resolver` for affected files (drains staged call sites, scores them with `ScopeResolver` + `ReferenceScorer`, and writes `EdgeKind.References` edges).
7. **Checkpoint** — calls `IGraphStore.SetIndexedCommitAsync` with the current `HEAD` commit SHA.

### Clustering (`ClusterWorker.RunAsync`)

1. Loads the symbol graph via `IClusterGraphProvider`.
2. Runs `ITwoPassClusterer` (Leiden algorithm via `LeidenRunner`) to produce `ClusterAssignment` records.
3. Applies symbol-to-cluster membership via `IGraphStore.ApplyClusterAssignmentsAsync`.
4. Summarises each cluster with `IClusterSummarizer` (LLM-backed), then atomically replaces all cluster summaries via `IGraphStore.ReplaceClusterSummariesAtomicallyAsync`.

### Query (`QueryPipeline.ExecuteAsync`)

1. `QueryPlanner.PlanAsync` — classifies the question into `Local`, `Subsystem`, `Global`, `Impact`, or `Dependency` using `QueryClassifier`, and builds a `QueryPlan`.
2. `HybridRetriever.RetrieveAsync` — runs vector search over symbols and clusters, then graph-traverses from high-scoring seeds via `IGraphStore.TraverseFromAsync`.
3. `ContextAssembler.Assemble` — packs symbol summaries and cluster text into bounded context under `QueryOptions.ContextTokenBudget` tokens.
4. `IChatClient.GetResponseAsync` — synthesises the final answer using `QueryOptions.AnswerModel`.

## Agent Tools

`CodeIndexAgentTool` exposes `ICodeIndex` as the `code_index_query` tool for use in [[Agency.Agentic]] agent loops.

```csharp
// File: src/GraphRAG.Code/Agency.GraphRAG.Code/Agentic/CodeIndexAgentTool.cs
using Agency.GraphRAG.Code.Agentic;
using Agency.Llm.Common.Tools;

// Registered as ITool; the agent invokes it with:
// { "question": "...", "topK": 5 }
CodeIndexAgentTool tool = new(codeIndex);
ToolResult result = await tool.InvokeAsync(inputJson, cancellationToken);
```

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.Agentic]] | `CodeIndexAgentTool` implements `ITool` for use inside agent loops |
| [[Agency.Embeddings.Common]] | `SymbolSummarizer` depends on `IEmbeddingGenerator` to produce symbol embeddings |
| [[Agency.Llm.Common]] | `QueryClassifier`, `QueryPipeline`, and `SymbolSummarizer` depend on `IChatClient` |
| [[Agency.GraphRAG.Code.Sqlite]] | Provides the SQLite-backed `IGraphStore` implementation wired by `AddCodeIndex` |
| [[Agency.GraphRAG.Code.Postgres]] | Provides the PostgreSQL-backed `IGraphStore` implementation wired by `AddCodeIndex` |

## Design Notes

- `IGraphStore` is the single storage boundary: schema migration, all upserts, vector search, graph traversal, unresolved call-site staging, and atomic cluster-summary replacement all go through it. Concrete implementations live in separate projects, keeping this library storage-agnostic.
- `ChangeDetector` and `Phase1Writer` both use SHA-256 content hashes to determine whether a symbol has changed since the last index run; this prevents unnecessary LLM summarisation calls on unchanged symbols.
- `AddCodeIndex` uses `TryAddSingleton` throughout, so host applications can override any internal component (e.g. replace `IEmbeddingGenerator` or `IChatClient`) without forking the registration logic.
- The two-phase hydration design (Phase 1 = definitions, Phase 2 = references) avoids forward-reference problems: all symbols are written before any cross-file call site resolution begins.
- `ClusterWorker` and the query pipeline are decoupled from indexing — clustering can be rerun on demand without re-parsing source files.
