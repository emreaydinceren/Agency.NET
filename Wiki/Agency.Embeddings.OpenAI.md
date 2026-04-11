# Agency.Embeddings.OpenAI

#embeddings #openai #implementation #observability

## What It Is

`Agency.Embeddings.OpenAI` is the concrete `IEmbeddingGenerator` implementation that calls any OpenAI-compatible embeddings endpoint (OpenAI, Azure OpenAI, LM Studio, Ollama, etc.). It includes full OpenTelemetry instrumentation — distributed traces via `ActivitySource` and metrics via `Meter`.

## How It Works

`EmbeddingGenerator` takes `IOptions<EmbeddingGeneratorOptions>` in its constructor:

```csharp
var generator = new EmbeddingGenerator(Options.Create(new EmbeddingGeneratorOptions
{
    ApiKey  = "sk-...",
    BaseUrl = "http://localhost:1234/v1",   // LM Studio / Ollama
    Model   = "text-embedding-3-small",
}));

ReadOnlyMemory<float> vector = await generator.GenerateEmbeddingAsync("hello world");
```

Internally it uses the `OpenAI` NuGet SDK's `EmbeddingClient`. For batch requests it calls the SDK's multi-input overload and returns all vectors in order.

## Observability

Every call writes to:

| Signal | Name | Tags |
|---|---|---|
| Activity | `embeddings.generate` | `model`, `input_count` |
| Counter | `embeddings.requests` | `model`, `status` |
| Counter | `embeddings.errors` | `model` |
| Histogram | `embeddings.duration` (ms) | `model` |
| Histogram | `embeddings.tokens` | `model`, `token_type` (`input`) |

Expose the `ActivitySource` (`Agency.Embeddings.OpenAI`) and `Meter` (`Agency.Embeddings.OpenAI`) names to your OpenTelemetry pipeline.

## Configuration

```json
{
  "Embeddings": {
    "ApiKey":  "lm-studio",
    "BaseUrl": "http://llm-host.example:1234/v1",
    "Model":   "text-embedding-3-small"
  }
}
```

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.Embeddings.Common]] | Implements `IEmbeddingGenerator` |
| [[Agency.Sql.Postgre]] | `SQLQueryEmbedder` receives this via DI |
| [[Agency.Sql.Sqlite]] | Same, SQLite variant |
| [[Agency.VectorStore.Sql.Postgre]] | `PostgreKVStore` uses it to generate query vectors and store embeddings |
| [[Agency.VectorStore.Sql.Sqlite]] | `SqliteKVStore` uses it similarly |
| [[Agency.Ingestion]] | Indirectly — store layer depends on it |
