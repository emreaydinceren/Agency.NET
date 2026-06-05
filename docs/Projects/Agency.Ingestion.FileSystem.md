# Agency.Ingestion.FileSystem
#ingestion #filesystem #loader #documents

## What It Is

Agency.Ingestion.FileSystem is the file-system ingestion library that provides `IDocumentLoader` implementations for reading documents from the local file system — one for single files and one for directory trees.

Namespace: `Agency.Ingestion.FileSystem`

## API Surface

```csharp
using Agency.Ingestion.FileSystem;
```

| Type | Member | Signature |
|---|---|---|
| `FileLoader` | Constructor | `FileLoader(string filePath)` |
| `FileLoader` | `LoadAsync` | `IAsyncEnumerable<Document> LoadAsync(CancellationToken ct = default)` |
| `DirectoryLoader` | Constructor | `DirectoryLoader(string directoryPath, string searchPattern = "*.md")` |
| `DirectoryLoader` | `LoadAsync` | `IAsyncEnumerable<Document> LoadAsync(CancellationToken ct = default)` |

## Dependencies

- `Agency.Ingestion`

## Related

- [[Agency.Ingestion]]
- [[Agency.Ingestion.SemanticKernel]]
