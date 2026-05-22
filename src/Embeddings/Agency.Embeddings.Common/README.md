# Agency.Embeddings.Common

Abstractions for generating text embeddings — provider-agnostic interfaces used across the Agency AI Toolkit.

## Install

```
dotnet add package Agency.Embeddings.Common
```

## Types

- **`IEmbeddingGenerator`** — `GenerateEmbeddingAsync(string)` / `GenerateEmbeddingsAsync(IEnumerable<string>)`.
- **`BatchingEmbeddingGenerator`** — wraps any `IEmbeddingGenerator` and coalesces concurrent single-item calls into batch API requests, reducing round-trips.

## Usage

```csharp
// Register a concrete implementation (e.g. Agency.Embeddings.OpenAI)
services.AddSingleton<IEmbeddingGenerator, EmbeddingGenerator>();

// Optionally wrap with batching
services.Decorate<IEmbeddingGenerator>(
    (inner, _) => new BatchingEmbeddingGenerator(inner));
```

Part of the [Agency AI Toolkit](https://github.com/emre/Agency) — an open-source .NET RAG pipeline.
