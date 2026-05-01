# Agency.Llm.OpenAI

#llm #openai #factory #meai #observability

## What It Is

Agency.Llm.OpenAI is the OpenAI-compatible `IChatClient` factory that wraps the official `OpenAI` .NET SDK and wires in OpenTelemetry and logging middleware via `Microsoft.Extensions.AI`. It works with any OpenAI-protocol-compatible endpoint: OpenAI cloud, Azure OpenAI, LM Studio, Ollama, and others.

**Namespace:** `Agency.Llm.OpenAI`

## Prerequisites

- An API key supplied via `LlmClientOptions.ApiKey`. For local endpoints (LM Studio, Ollama) any non-empty placeholder such as `"lm-studio"` is accepted.
- Optionally a `BaseUrl` in `LlmClientOptions` pointing to the endpoint's `/v1` root (e.g. `http://llm-host.example:1234/v1`). When omitted, the official OpenAI API endpoint is used.
- The `OpenAI` NuGet package and `Microsoft.Extensions.AI.OpenAI` are required dependencies (managed centrally via `Directory.Build.props`).

## API Surface

### `OpenAIClient`

```csharp
// File: src/Llm/Agency.Llm.OpenAI/OpenAIClient.cs
using Agency.Llm.Common;
using Agency.Llm.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed class OpenAIClient : IModelProvider
{
    // Constructors
    public OpenAIClient(IOptions<LlmClientOptions> options, ILoggerFactory? loggerFactory = null);
    public OpenAIClient(LlmClientOptions options, ILoggerFactory? loggerFactory = null);

    /// <summary>
    /// Creates an IChatClient wired with OpenTelemetry and logging middleware.
    /// The model is selected per-request via ChatOptions.ModelId.
    /// </summary>
    public IChatClient CreateChatClient();

    /// <inheritdoc cref="IModelProvider"/>
    public Task<IReadOnlyList<Model>> GetModelsAsync(CancellationToken cancellationToken = default);
}
```

## How It Works

`OpenAIClient` is a factory, not a chat client itself. Each call to `CreateChatClient()` constructs an underlying `OpenAI.OpenAIClient`, obtains its `ChatClient` via `GetChatClient("default")`, converts it to `IChatClient` (MEAI abstraction), then chains:

1. `UseOpenTelemetry()` — records spans and gen_ai semantic-convention metrics automatically.
2. `UseLogging(loggerFactory)` — emits request/response log entries.

The `"default"` model placeholder is overridden at call time by setting `ChatOptions.ModelId`, so a single factory instance serves any model the caller names.

```csharp
// File: src/Llm/Agency.Llm.OpenAI/OpenAIClient.cs
using Agency.Llm.Common;
using Agency.Llm.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

// Create the factory
var factory = new OpenAIClient(new LlmClientOptions
{
    ApiKey  = "lm-studio",
    BaseUrl = "http://llm-host.example:1234/v1",
});

// Obtain an IChatClient
IChatClient chat = factory.CreateChatClient();

// Send a prompt — model selected via ChatOptions
ChatResponse response = await chat.GetResponseAsync(
    "Explain HNSW indexes.",
    new ChatOptions { ModelId = "google/gemma-4-e2b" });

// Enumerate available models
IReadOnlyList<Model> models = await factory.GetModelsAsync();
```

Optional `Timeout` and `MaxRetries` on `LlmClientOptions` are forwarded to `OpenAIClientOptions.NetworkTimeout` and retry policy respectively.

## Observability

OpenTelemetry instrumentation is provided by the `UseOpenTelemetry()` middleware from `Microsoft.Extensions.AI.OpenAI`, which follows the OpenTelemetry Generative AI semantic conventions:

- Distributed traces recorded under the `OpenAI` activity source name.
- Tags include `gen_ai.system`, `gen_ai.request.model`, `gen_ai.usage.input_tokens`, `gen_ai.usage.output_tokens`.

No additional `ActivitySource` or `Meter` is declared in this project; telemetry is entirely delegated to the MEAI middleware pipeline.

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.Llm.Common]] | Implements `IModelProvider`; uses `LlmClientOptions` and `Model` |
| [[Agency.Agentic]] | `Models.cs` calls `new OpenAIClient(options).CreateChatClient()` when `ClientType` is `"OpenAI"` |
| [[Agency.Agentic.Console]] | Configures `ClientType = "OpenAI"` in `appsettings.json` to select this factory |
| [[Agency.Embeddings.OpenAI]] | Sibling project — both wrap OpenAI-compatible APIs, but for embeddings rather than chat |
| [[Agency.Llm.Claude]] | Peer factory implementing the same `IModelProvider` pattern for the Anthropic SDK |

## Design Notes

- `OpenAIClient` follows the factory pattern: it is not itself an `IChatClient` but creates one on demand. This allows the MEAI middleware pipeline to be assembled fresh per usage site and keeps `OpenAIClient` lightweight and DI-friendly.
- The model is resolved per-request via `ChatOptions.ModelId` rather than at construction time, so a single `OpenAIClient` instance can serve requests to different models without re-instantiation.
- The `"default"` string passed to `GetChatClient("default")` is a placeholder required by the SDK; it is always overridden by `ChatOptions.ModelId` at the call site.
- Both constructor overloads (`IOptions<LlmClientOptions>` and plain `LlmClientOptions`) are provided so the class works equally well with Microsoft DI and in manual/test scenarios.
