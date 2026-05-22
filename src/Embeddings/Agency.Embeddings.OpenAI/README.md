# Agency.Embeddings.OpenAI

OpenAI-compatible embedding implementation for the Agency AI Toolkit. Works with OpenAI, Azure OpenAI, LM Studio, and any other OpenAI-compatible endpoint.

## Install

```
dotnet add package Agency.Embeddings.OpenAI
```

## Configuration

```json
{
  "Embeddings": {
    "BaseUrl": "https://api.openai.com/v1",
    "ModelId": "text-embedding-3-small",
    "ApiKey": "sk-...",
    "Dimensions": 1536
  }
}
```

## Usage

```csharp
services.Configure<EmbeddingOptions>(config.GetSection("Embeddings"));
services.AddSingleton<IEmbeddingGenerator, EmbeddingGenerator>();

// Inject and use:
var embedding = await generator.GenerateEmbeddingAsync("search query");
```

Part of the [Agency AI Toolkit](https://github.com/emre/Agency) — an open-source .NET RAG pipeline.
