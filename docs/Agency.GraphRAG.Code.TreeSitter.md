# Agency.GraphRAG.Code.TreeSitter

tags: graphrag, code-indexing, tree-sitter, ast, parsing, sidecar

**Namespace:** `Agency.GraphRAG.Code.TreeSitter`

## Terminology

An **Abstract Syntax Tree (AST)** is a hierarchical, tree-based representation of the abstract syntactic structure of source code. It is a fundamental data structure used by compilers, interpreters, and static analysis tools to "understand" and manipulate code.

Unlike source code, which is a flat stream of characters or tokens, an AST represents the logical structure of the program by stripping away "syntactic sugar" and non-essential details.

Agency.GraphRAG.Code.TreeSitter is the AST-parsing layer that communicates with the Node.js tree-sitter sidecar process to convert raw source files into structured `AstNode` trees consumed by the language chunkers.

## Prerequisites

- **Node.js** must be installed and accessible on `PATH` (or the executable path passed explicitly).
- **Tree-sitter sidecar** — the `tools/treesitter-sidecar/index.js` script and its `node_modules` must be present. Install once with:
  ```bash
  cd tools/treesitter-sidecar
  npm install
  ```
- No additional .NET infrastructure (no database, no embedding API).

## API Surface

### `TreeSitterClient`

Hosts and manages the lifecycle of the sidecar child process. Implements `IAsyncDisposable`.

```csharp
// File: src/GraphRAG.Code/Agency.GraphRAG.Code.TreeSitter/TreeSitterClient.cs
using Agency.GraphRAG.Code.TreeSitter;
using Agency.GraphRAG.Code.Walker;

// Construction — both parameters are optional
await using var client = new TreeSitterClient(
    nodeExecutable: "node",   // default
    sidecarPath: null         // defaults to tools/treesitter-sidecar/index.js discovered by walking up from AppContext.BaseDirectory
);

// Parse a source file
ParsedFile result = await client.ParseAsync(
    path: "src/Foo.cs",
    lang: Language.CSharp,
    source: File.ReadAllText("src/Foo.cs"),
    cancellationToken: ct);
```

### `ParsedFile`

Record returned by `ParseAsync`. Carries the root AST node.

```csharp
// File: src/GraphRAG.Code/Agency.GraphRAG.Code.TreeSitter/TreeSitterClient.cs
using Agency.GraphRAG.Code.TreeSitter;

ParsedFile file = /* from TreeSitterClient.ParseAsync */;
string   filePath = file.Path;
string?  language = file.Language;   // e.g. "csharp", "typescript"
AstNode  root     = file.Root;
```

### `AstNode`

Immutable record representing a single node in the AST.

```csharp
// File: src/GraphRAG.Code/Agency.GraphRAG.Code.TreeSitter/AstNode.cs
using Agency.GraphRAG.Code.TreeSitter;

AstNode node = /* root or any child */;
string              kind      = node.Kind;       // e.g. "method_declaration"
string?             text      = node.Text;       // leaf text when available
SourceRange?        range     = node.Range;      // zero-based line/column bounds
IReadOnlyList<AstNode> children = node.Children;
string?             fieldName = node.FieldName;  // tree-sitter field name, e.g. "name"
```

### `SourceRange`

```csharp
// File: src/GraphRAG.Code/Agency.GraphRAG.Code.TreeSitter/AstNode.cs
using Agency.GraphRAG.Code.TreeSitter;

SourceRange range = node.Range!;
int startLine   = range.StartLine;    // zero-based
int startColumn = range.StartColumn;
int endLine     = range.EndLine;
int endColumn   = range.EndColumn;
```

### `AstTraversal`

Static helpers for working with `AstNode` trees.

```csharp
// File: src/GraphRAG.Code/Agency.GraphRAG.Code.TreeSitter/AstTraversal.cs
using Agency.GraphRAG.Code.TreeSitter;

// Depth-first search for all nodes of a given kind
IEnumerable<AstNode> methods = AstTraversal.FindNodesOfKind(root, "method_declaration");

// First identifier text within a subtree
string? name = AstTraversal.GetIdentifier(methodNode);

// Source range accessor
SourceRange? range = AstTraversal.GetSourceRange(node);
```

### `WriteRequestBuilder`

Builds `Phase1WriteRequest` instances from repository walk results using tree-sitter parsing and chunking.

```csharp
// File: src/GraphRAG.Code/Agency.GraphRAG.Code.TreeSitter/Pipeline/WriteRequestBuilder.cs
using Agency.GraphRAG.Code.TreeSitter.Pipeline;

var builder = new WriteRequestBuilder(treeSitterClient, chunkerDispatcher);

IReadOnlyDictionary<string, Phase1WriteRequest> requests = await builder.BuildAsync(
    repo: repoObject,
    walkResult: walkResultFromWalker,
    cancellationToken: ct);
```

## How It Works

### TreeSitterClient

1. **Lazy process start** — the first call to `ParseAsync` checks whether the sidecar process is healthy. If not, it starts `node <sidecarPath>` with redirected stdin/stdout/stderr.
2. **JSON-line protocol** — each parse request is serialised as a single JSON line and written to the child's stdin. The sidecar writes one JSON response line per request to stdout.
3. **Request correlation** — requests carry a `Guid`-based `id`. Concurrent callers each own a `TaskCompletionSource<ParsedFile>` registered in a `ConcurrentDictionary` keyed by that id. The stdout reader resolves the correct TCS when the matching response arrives.
4. **Write serialisation** — a `SemaphoreSlim` ensures only one caller writes to stdin at a time; the stdout reader runs on its own background task so concurrent responses are never lost.
5. **Process recovery** — if the sidecar exits unexpectedly, `CleanupProcess` faults all outstanding requests, and the next `ParseAsync` call restarts the process.
6. **AST deserialisation** — `ParsedFile.FromJsonElement` and `AstNode.FromJsonElement` accept several sidecar response shapes (different property names for `kind`, `range`, positions) for resilience across sidecar versions.

### WriteRequestBuilder

1. **File filtering** — iterates `WalkResult.Files`, skipping entries with `Deleted` status or `Unknown` language (unsupported by tree-sitter).
2. **Content read** — reads the full source text for each processable file from disk.
3. **Parse and chunk** — calls `TreeSitterClient.ParseAsync` to obtain the AST root, then `ChunkerDispatcher.ChunkAsync` to extract language-aware chunks.
4. **SourceFile domain object** — builds a stable `SourceFile` entity with:
   - `Id`: deterministic GUID derived from repository ID and file path (via `HydrationIds.StableGuid`)
   - `ContentHash`: SHA256 hash of the file source for change detection
   - `Language`: localized language name (e.g. `"csharp"`, `"typescript"`)
5. **Write request assembly** — wraps the `SourceFile`, chunks, and empty summaries/scopes into a `Phase1WriteRequest` keyed by file path.
6. **Return** — returns a dictionary mapping file paths to `Phase1WriteRequest` for persistence.

## How It Relates to Other Projects

| Project | Relationship |
|---------|--------------|
| **[[Agency.GraphRAG.Code]]** | The chunkers (`CSharpChunker`, `TypeScriptChunker`, `PythonChunker`) reference `TreeSitterClient` by assembly-qualified name and use `ParseAsync` to obtain AST roots before extracting symbols. `WriteRequestBuilder` implements `IWriteRequestBuilder` (an internal interface defined in Code) and uses `ChunkerDispatcher` to process parsed files. |
| **`Agency.GraphRAG.Code.Walker`** | The `Language` enum and `ParsedFile` path conventions originate in the Walker namespace, which this project references. |
| **`tools/treesitter-sidecar`** | The external Node.js process this library manages; not a .NET project, but a required runtime dependency. |

## Design Notes

- `TreeSitterClient` is a `sealed` concrete class rather than an interface because the sidecar lifecycle (process management, background reader tasks, semaphore disposal) is inherently stateful and difficult to mock; integration tests use the real sidecar binary.
- The sidecar path resolution walks upward from `AppContext.BaseDirectory` so the same binary works whether the library is consumed from a test runner, a CLI project, or a published deployment without requiring explicit configuration in most scenarios.
- `AstNode` is a positional record with `IReadOnlyList<AstNode>` children instead of a mutable tree, making subtrees safe to share across concurrent parsing tasks without defensive copying.
- The `AstTraversal.GetIdentifier` helper matches `identifier`, `property_identifier`, and `type_identifier` node kinds — the union of leaf identifier kinds used by the C#, TypeScript, and Python tree-sitter grammars supported by the sidecar.
- `WriteRequestBuilder` lives in the TreeSitter project rather than in [[Agency.GraphRAG.Code]] to avoid a circular project dependency: TreeSitter already references Code (for `Language` enum, `Chunk`, `SourceFile`, `Phase1WriteRequest` types), so Code cannot reference back into TreeSitter. The `IWriteRequestBuilder` interface is defined in Code, and `WriteRequestBuilder` is registered in the DI container via reflection, keeping the call site type-safe without a direct reference.
