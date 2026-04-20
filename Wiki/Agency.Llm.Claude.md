# Agency.Llm.Claude

#llm #claude #anthropic #implementation #observability

## What It Is

`Agency.Llm.Claude` is the Anthropic Claude implementation of [[Agency.Llm.Common]]'s `ILlmClient`. It wraps the Stainless-generated `Anthropic` .NET SDK and maps between the solution's provider-agnostic message types and the Anthropic wire format.

## How It Works

```csharp
var client = new ClaudeClient(Options.Create(new LlmClientOptions
{
    ApiKey  = "sk-ant-...",
    BaseUrl = null,   // defaults to api.anthropic.com
}));

// Simple prompt
LlmResponse reply = await client.SendAsync("claude-opus-4-6", "system prompt", "What is 2+2?");

// Streaming simple prompt
await foreach (LlmStreamChunk chunk in client.StreamAsync("claude-opus-4-6", "system", "prompt"))
{
    if (chunk.Text is not null) Console.Write(chunk.Text);
}

// Agentic turn with tools and conversation history
AgentLlmResponse response = await client.SendAgentAsync(
    model:        "claude-opus-4-6",
    systemPrompt: "You are a helpful assistant.",
    messages:     conversationHistory,
    tools:        toolDefinitions,
    ct:           cancellationToken);

// Streaming agentic turn
await foreach (AgentStreamChunk chunk in client.StreamAgentAsync("claude-opus-4-6", systemPrompt, messages, tools, ct))
{
    if (chunk.Text is not null)        Console.Write(chunk.Text);        // live token
    if (chunk.ToolUse is not null)     HandleToolUse(chunk.ToolUse);    // complete tool call
    if (chunk.StopReason is not null)  Console.WriteLine(chunk.Usage);  // terminal chunk
}

// List available models
IReadOnlyList<Model> models = await client.GetModelsAsync();
```

`ClientType` returns `"Claude"`.

## Anthropic SDK Mapping

The Anthropic SDK uses generated discriminated-union types. Key mapping decisions:

| Our type | Anthropic SDK type | Notes |
|---|---|---|
| `TextBlock` | `ContentBlock.TryPickText()` | Returns `ATextBlock` |
| `ToolUseBlock` | `ContentBlock.TryPickToolUse()` | Returns `AToolUseBlock`; input serialized to `JsonElement` |
| `ThinkingBlock` | `ContentBlock.TryPickThinking()` | Returns `AThinkingBlock` |
| `ToolDefinition.InputSchema` | `InputSchema.FromRawUnchecked(dict)` | Only way to build from `JsonElement` |
| `IReadOnlyList<ToolDefinition>` | `IReadOnlyList<ToolUnion>` | Each `Tool` is explicitly cast: `(ToolUnion)tool` |
| `IReadOnlyList<ContentBlockParam>` | `MessageParamContent` | Requires double explicit cast |

**Naming collision resolution** — both `Agency.Llm.Common.Messages` and `Anthropic.Models.Messages` export `TextBlock`, `ToolUseBlock`, and `ThinkingBlock`. The file uses `using` aliases to disambiguate:

```csharp
using OurTextBlock  = Agency.Llm.Common.Messages.TextBlock;
using ATextBlock    = Anthropic.Models.Messages.TextBlock;
```

## Observability

`SendAsync` and `StreamAsync` instrument every call with OpenTelemetry using semantic conventions. `SendAgentAsync` and `StreamAgentAsync` record token usage and log at `Information` level but do not start their own activity (the agent loop owns the span).

| Signal | Name | Tags |
|---|---|---|
| Activity | `SendAsync` / `StreamAsync` | `gen_ai.system=anthropic`, `gen_ai.request.model` |
| Counter | `llm.client.requests` / `llm.client.errors` | same tags + `llm.method` |
| Histogram | `llm.client.duration` (ms) | same tags |
| Counter | `llm.client.tokens` | same tags + `gen_ai.token.type=input\|output` |

Expose `ActivitySourceName` (`Agency.Llm.Claude`) and `MeterName` (`Agency.Llm.Claude`) to your OTel pipeline.

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.Llm.Common]] | Implements `ILlmClient`; uses all message and tool types |
| [[Agency.Agentic]] | `Agent` calls `SendAgentAsync` on every loop iteration |
| [[Agency.Agentic.Console]] | Instantiated when `Agent:Provider = "Claude"` in config |
