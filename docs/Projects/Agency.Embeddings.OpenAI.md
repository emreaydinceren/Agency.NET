# Agency.Embeddings.OpenAI
#embeddings #openai #implementation #observability

## What It Is
Agency.Embeddings.OpenAI is the OpenAI-compatible embeddings provider that generates vector embeddings by calling an OpenAI-compatible HTTP endpoint and returns them as `ReadOnlyMemory<float>` arrays implementing the `IEmbeddingGenerator` contract for use by higher-level retrieval and storage components.

**Namespace:** `Agency.Embeddings.OpenAI`

## Prerequisites
- A reachable OpenAI-compatible embeddings endpoint (e.g., LM Studio) configured via `EmbeddingOptions.BaseUrl`.
- A non-empty API key configured via `EmbeddingOptions.ApiKey` (the OpenAI SDK requires a non-empty value; LM Studio does not validate it).
- A model identifier configured via `EmbeddingOptions.ModelId`.

## API Surface

### `EmbeddingOptions`

```csharp
// File: src/Embeddings/Agency.Embeddings.OpenAI/EmbeddingOptions.cs
public sealed class EmbeddingOptions
{
    public const string SectionName = "Embedding";
    public string? BaseUrl { get; set; }
    public string? ModelId { get; set; }
    public string? ApiKey { get; set; }
    public int? Dimensions { get; set; }
}
```

### `EmbeddingGenerator`

```csharp
// File: src/Embeddings/Agency.Embeddings.OpenAI/EmbeddingGenerator.cs
using Agency.Embeddings.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed class EmbeddingGenerator : IEmbeddingGenerator
{
    public const string ActivitySourceName = "Agency.Embeddings.OpenAI";
    public const string MeterName = "Agency.Embeddings.OpenAI";

    public EmbeddingGenerator(IOptions<EmbeddingOptions> options, ILogger<EmbeddingGenerator>? logger = null);
    public EmbeddingGenerator(EmbeddingOptions options, ILogger<EmbeddingGenerator>? logger = null);

    public Task<ReadOnlyMemory<float>> GenerateEmbeddingAsync(string input, CancellationToken cancellationToken = default);
    public Task<IReadOnlyList<ReadOnlyMemory<float>>> GenerateEmbeddingsAsync(IEnumerable<string> inputs, CancellationToken cancellationToken = default);
}
```

## How It Works
On construction, the generator validates `BaseUrl` and `ApiKey`, then creates an `OpenAI.Embeddings.EmbeddingClient` pointing at the configured endpoint. Each call to `GenerateEmbeddingAsync` or `GenerateEmbeddingsAsync` starts a distributed-tracing activity, starts a stopwatch, sends the request, and on completion records duration, request count, and (for batch calls) input token usage. Errors are logged, marked on the activity with exception details, and rethrown unchanged.

```csharp
using Agency.Embeddings.OpenAI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

var options = new EmbeddingOptions
{
    BaseUrl = "http://llm.test:1234/v1",
    ModelId = "text-embedding-qwen3-embedding-0.6b",
    ApiKey = "lmstudio",
    Dimensions = 1024
};

var generator = new EmbeddingGenerator(options, NullLogger<EmbeddingGenerator>.Instance);

ReadOnlyMemory<float> single = await generator.GenerateEmbeddingAsync("hello world");
IReadOnlyList<ReadOnlyMemory<float>> batch = await generator.GenerateEmbeddingsAsync(["first", "second"]);
```

## Observability
- **ActivitySource name:** `"Agency.Embeddings.OpenAI"`
- **Meter name:** `"Agency.Embeddings.OpenAI"`

| Instrument | Name | Unit | Description | Tags |
|---|---|---|---|---|
| `Counter<long>` | `embedding.requests` | `{request}` | Total number of embedding requests. | `operation` (`single`/`batch`), `status` (`success`/`error`) |
| `Histogram<double>` | `embedding.duration` | `ms` | Duration of embedding requests in milliseconds. | `operation` |
| `Counter<long>` | `embedding.tokens` | `{token}` | Total input tokens consumed (batch requests only). | `operation` |

Activity tags follow the OpenTelemetry GenAI semantic conventions: `gen_ai.system`, `gen_ai.operation.name`, `gen_ai.request.model`, and on batch success `gen_ai.response.usage.input_tokens` and `gen_ai.response.embedding_count`.

## How It Relates to Other Projects
| Project | Relationship |
|---|---|
| [[Agency.Embeddings.Common]] | Defines the `IEmbeddingGenerator` interface that `EmbeddingGenerator` implements. |
| [[Agency.VectorStore.Sql.Postgres]] | Consumes `IEmbeddingGenerator` to produce vectors for storage and similarity search. |
| [[Agency.VectorStore.Sql.Sqlite]] | Consumes `IEmbeddingGenerator` to produce vectors for storage and similarity search; uses `Dimensions` to create schemas with the correct column width. |
| [[Agency.Sql.Postgres]] | Consumes embeddings indirectly via `SQLQueryEmbedder` in retrieval/query pipelines. |

## Design Notes
- Two public constructors are provided so callers can use either the DI options pattern (`IOptions<EmbeddingOptions>`) or a direct `EmbeddingOptions` instance; an additional `internal` constructor accepts a custom `HttpMessageHandler` to enable unit testing without a live endpoint.
- The `Dimensions` property is optional and is not used by `EmbeddingGenerator` itself; it is a schema hint consumed by vector store projects (e.g., SQLite) that need to know the output width at table-creation time rather than at first insert.
- Telemetry names are exposed as public constants (`ActivitySourceName`, `MeterName`) so host setup can reference stable string literals without duplication across test and production code.
