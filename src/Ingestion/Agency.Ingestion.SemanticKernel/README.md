# Agency.Ingestion.SemanticKernel

Semantic Kernel-powered text splitter for the Agency ingestion pipeline.

## Install

```
dotnet add package AgencyDotNet.Ingestion.SemanticKernel
```

## Types

- **`SemanticKernelTextSplitter`** — implements `ITextSplitter` using Semantic Kernel's `TextChunker`, which splits on sentence boundaries and respects token limits.

## Usage

```csharp
services.AddScoped<ITextSplitter>(_ =>
    new SemanticKernelTextSplitter(maxTokensPerChunk: 512));
```

Part of the [Agency AI Toolkit](https://github.com/emreaydinceren/Agency.NET) — an open-source .NET RAG pipeline.
