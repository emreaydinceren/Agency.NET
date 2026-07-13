# Agency.Ingestion.FileSystem

File system document loaders for the Agency ingestion pipeline.

## Install

```
dotnet add package AgencyDotNet.Ingestion.FileSystem
```

## Types

- **`FileLoader`** — loads a single file as a `Document`.
- **`DirectoryLoader`** — recursively enumerates a directory and loads all matching files (supports glob patterns).

## Usage

```csharp
// Load all .txt files from a directory
services.AddScoped<IDocumentLoader>(_ =>
    new DirectoryLoader("/data/docs", "*.txt"));

// Or load a single file
services.AddScoped<IDocumentLoader>(_ =>
    new FileLoader("/data/manual.pdf"));
```

Part of the [Agency AI Toolkit](https://github.com/emreaydinceren/Agency.NET) — an open-source .NET RAG pipeline.
