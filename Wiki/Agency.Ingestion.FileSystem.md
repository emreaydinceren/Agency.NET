# Agency.Ingestion.FileSystem

#ingestion #filesystem #loader #documents

## What It Is

`Agency.Ingestion.FileSystem` provides `IDocumentLoader` implementations that read documents from the local file system. It contains two loaders: one for individual files (`FileLoader`) and one for directories (`DirectoryLoader`).

## How It Works

### `DirectoryLoader`

Recursively walks a directory and yields one `Document` per file. Files are read lazily — they are not all buffered in memory at once.

```csharp
var loader = new DirectoryLoader(
    directoryPath: "/path/to/docs",
    searchPattern: "*.md");     // default: "*.md"

await foreach (Document doc in loader.LoadAsync(cancellationToken))
{
    Console.WriteLine($"Loaded: {doc.SourceId}");
    // doc.SourceId  = absolute file path
    // doc.Content   = full file text
    // doc.Metadata  = { "file_name", "file_extension", "file_size_bytes", ... }
}
```

Under the hood, `DirectoryLoader` calls `FileLoader.BuildDocument(filePath, content)` for each file, which populates standard metadata keys:

| Metadata key | Value |
|---|---|
| `file_name` | File name without extension |
| `file_extension` | Extension including the dot (e.g., `.md`) |
| `file_size_bytes` | File size in bytes |
| `source_path` | Absolute file path |

### `FileLoader`

Loads a single file:

```csharp
Document doc = await FileLoader.LoadAsync("/path/to/file.md", cancellationToken);
```

Can also be used to build a `Document` from already-read content:

```csharp
Document doc = FileLoader.BuildDocument(filePath, existingContent);
```

## Integration with the Pipeline

```csharp
var pipeline = new DefaultIngestionPipeline<string>(doc => doc.Content);

await pipeline.ExecuteAsync(
    loader:   new DirectoryLoader("/knowledge-base"),
    splitter: new SemanticKernelTextSplitter(512, 64),
    store:    kvStore);
```

The `file_extension` metadata key populated by `FileLoader` is also used by [[Agency.Ingestion.SemanticKernel]]'s `SemanticKernelTextSplitter` to detect Markdown files and apply markdown-aware chunking.

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.Ingestion]] | Implements `IDocumentLoader` defined in this package |
| [[Agency.Ingestion.SemanticKernel]] | Reads `file_extension` metadata to choose split strategy |
| [[Agency.VectorStore.Common]] | Documents end up stored via `IKVStore` after splitting |
