# Agency.Llm.Claude

#llm #claude #anthropic #implementation #microsoft-extensions-ai

## What It Is

`Agency.Llm.Claude` is the Anthropic Claude provider factory that creates `IChatClient` instances backed by the Anthropic API and implements `IModelProvider` to enumerate available models.

**Namespace:** `Agency.Llm.Claude`

## Prerequisites

An Anthropic API key must be supplied via `LlmClientOptions.ApiKey`. For local development, store it in .NET user secrets under the `LlmTest:Claude:ApiKey` key or pass it directly through `IOptions<LlmClientOptions>`.

## API Surface

```csharp
// File: src/Llm/Agency.Llm.Claude/ClaudeClient.cs
using Agency.Llm.Common;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// Factory — creates IChatClient instances for the Anthropic Claude API.
// Also implements IModelProvider to list available models.
public sealed class ClaudeClient : IModelProvider
{
    public ClaudeClient(IOptions<LlmClientOptions> options, ILoggerFactory? loggerFactory = null);
    public ClaudeClient(LlmClientOptions options, ILoggerFactory? loggerFactory = null);

    // Creates an IChatClient wired with OpenTelemetry and logging middleware.
    // Model is selected per-request via ChatOptions.ModelId.
    public IChatClient CreateChatClient();

    // Lists all models available through this Anthropic account.
    public Task<IReadOnlyList<Model>> GetModelsAsync(CancellationToken cancellationToken = default);
}
```

## How It Works

`CreateChatClient()` builds an instrumented `IChatClient` in three steps:

1. Constructs an `AnthropicClient` from the Anthropic SDK using the configured `ApiKey`, `BaseUrl`, `MaxRetries`, and `Timeout` from `LlmClientOptions`.
2. Calls the SDK's `.AsIChatClient()` extension (with an empty default model; the model is chosen per-call via `ChatOptions.ModelId`).
3. Chains middleware via the `IChatClientBuilder`:
   - `UseOpenTelemetry()` — records activities, request/response details, and token usage following OpenTelemetry Gen AI semantic conventions.
   - `UseLogging(loggerFactory)` — logs requests and responses at `Information` level using the supplied `ILoggerFactory` (or `NullLoggerFactory` if none is provided).

`GetModelsAsync()` constructs a separate `AnthropicClient`, calls `anthropic.Models.List(...)`, and maps each result to a `Model(Id, DisplayName)` record. If the SDK throws `AnthropicInvalidDataException` when reading `DisplayName`, the model `Id` is used as a fallback.

Example usage:

```csharp
using Agency.Llm.Claude;
using Agency.Llm.Common;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

var factory = new ClaudeClient(Options.Create(new LlmClientOptions
{
    ApiKey  = "sk-ant-...",
    BaseUrl = null,   // defaults to api.anthropic.com
}));

IChatClient client = factory.CreateChatClient();

// Single-turn request
ChatResponse response = await client.GetResponseAsync(
    [new ChatMessage(ChatRole.User, "What is 2 + 2?")],
    new ChatOptions { ModelId = "claude-opus-4-5", MaxOutputTokens = 256 });

// Streaming
await foreach (StreamingChatResponseUpdate update in client.GetStreamingResponseAsync(
    [new ChatMessage(ChatRole.User, "Tell me a joke.")],
    new ChatOptions { ModelId = "claude-opus-4-5" }))
{
    foreach (var text in update.Contents.OfType<TextContent>())
        Console.Write(text.Text);
}

// List available models
IReadOnlyList<Model> models = await factory.GetModelsAsync();
```

## Observability

Observability is provided by the `Microsoft.Extensions.AI` middleware pipeline rather than a custom `ActivitySource` or `Meter`. Calling `UseOpenTelemetry()` on the builder automatically instruments every `IChatClient` call with:

- Distributed tracing activities following the OpenTelemetry Gen AI semantic conventions (`gen_ai.system`, `gen_ai.request.model`, etc.).
- Token usage recorded on the activity.

Logging of request and response details is enabled via `UseLogging()`.

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.Llm.Common]] | Implements `IModelProvider`; uses `LlmClientOptions` and `Model` from this assembly |
| [[Agency.Agentic]] | `Models.cs` calls `new ClaudeClient(options).CreateChatClient()` when the provider is `"CLAUDE"` |
| [[Agency.Agentic.Console]] | Selects the Claude provider via configuration (`Agent:Provider = "Claude"`) |

## Design Notes

- `ClaudeClient` is a **factory**, not a long-lived client. Each call to `CreateChatClient()` constructs a new `AnthropicClient` and middleware pipeline; callers should cache the returned `IChatClient`.
- Observability is fully delegated to the `Microsoft.Extensions.AI` middleware, keeping `ClaudeClient` free of any manual `ActivitySource` or `Meter` bookkeeping and ensuring consistent telemetry behaviour across all `IChatClient` implementations in the solution.
- `LlmClientOptions.BaseUrl` can redirect requests to any OpenAI-compatible endpoint (e.g. a proxy or LM Studio), enabling local testing without a real Anthropic key.
