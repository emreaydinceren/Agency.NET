# Agency.Ingestion

Document ingestion pipeline abstractions for the Agency AI Toolkit — load, split, and store documents for RAG.

## Install

```
dotnet add package AgencyDotNet.Ingestion
```

## Types

- **`IDocumentLoader`** — `LoadAsync()` streams `Document` records from a source.
- **`ITextSplitter`** — `Split(Document)` chunks a document into smaller pieces.
- **`DefaultIngestionPipeline<TValue>`** — orchestrates load → split → store using any `IDocumentLoader`, `ITextSplitter`, and `IVectorStore<TValue>`.
- **`Document`** — record holding text content and metadata.

## Usage

```csharp
services.AddScoped<IDocumentLoader, DirectoryLoader>(); // from Agency.Ingestion.FileSystem
services.AddScoped<ITextSplitter, SemanticKernelTextSplitter>(); // from Agency.Ingestion.SemanticKernel
services.AddScoped(typeof(IIngestionPipeline<>), typeof(DefaultIngestionPipeline<>));

// Run the pipeline
await pipeline.RunAsync(cancellationToken);
```

Part of the [Agency AI Toolkit](https://github.com/emreaydinceren/Agency.NET) — an open-source .NET RAG pipeline.
