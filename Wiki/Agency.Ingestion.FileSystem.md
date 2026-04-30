# Agency.Ingestion.FileSystem

#ingestion #filesystem #loader #documents

## What It Is

Agency.Ingestion.FileSystem is the file-system ingestion library that provides `IDocumentLoader` implementations for reading documents from the local file system — one for single files and one for directory trees.

**Namespace:** `Agency.Ingestion.FileSystem`

## API Surface

```csharp
// File: src/Ingestion/Agency.Ingestion.FileSystem/FileLoader.cs
using Agency.Ingestion.FileSystem;

// Loads a single file as an async stream of one Document.
public sealed class FileLoader(string filePath) : IDocumentLoader
{
    public IAsyncEnumerable<Document> LoadAsync(CancellationToken ct = default);
}
```

```csharp
// File: src/Ingestion/Agency.Ingestion.FileSystem/DirectoryLoader.cs
using Agency.Ingestion.FileSystem;

// Recursively enumerates files in a directory and loads each as a Document.
public sealed class DirectoryLoader(
    string directoryPath,
    string searchPattern = "*.md") : IDocumentLoader
{
    public IAsyncEnumerable<Document> LoadAsync(CancellationToken ct = default);
}
```

Each `Document` produced by either loader carries the following metadata:

| Key | Value |
|---|---|
| `file_path` | Absolute path to the file |
| `file_name` | File name including extension |
| `file_extension` | Extension including the dot (e.g. `.md`) |

## How It Works

`FileLoader` reads a single file with `File.ReadAllTextAsync` and emits one `Document` whose `SourceId` is the absolute file path.

`DirectoryLoader` calls `Directory.EnumerateFiles` with `SearchOption.AllDirectories` and delegates each file to `FileLoader`, yielding documents one at a time without buffering the entire directory in memory. Cancellation is checked between files.

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.Ingestion]] | Depends on it; implements `IDocumentLoader` and `Document` defined there |
| [[Agency.Ingestion.SemanticKernel]] | Reads the `file_extension` metadata key to choose a Markdown-aware or plain-text split strategy |
| [[Agency.VectorStore.Common]] | Downstream consumer; chunked documents are stored via `IKVStore` after splitting |

## Design Notes

- Both loaders are `sealed` primary-constructor classes with no static factory methods; the internal `BuildDocument` helper is shared between them but is not part of the public API.
- `DirectoryLoader` streams files one at a time rather than collecting all paths first, keeping memory usage proportional to a single document rather than the entire directory.
