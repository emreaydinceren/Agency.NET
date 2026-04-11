# Agency.Llm.Claude

#llm #claude #anthropic #implementation #observability

## What It Is

`Agency.Llm.Claude` is the Anthropic Claude implementation of [[Agency.Llm.Common]]'s `ILlmClient`. It wraps the Stainless-generated `Anthropic` .NET SDK and maps between the solution's provider-agnostic message types and the Anthropic wire format.

## How It Works

```csharp
var client = new ClaudeClient(Options.Create(new ClaudeClientOptions
{
    ApiKey  = "sk-ant-...",
    BaseUrl = null,   // defaults to api.anthropic.com
}));

// Simple prompt
string reply = await client.SendAsync("claude-opus-4-6", "What is 2+2?");

// Agentic turn with tools and conversation history
AgentLlmResponse response = await client.SendAgentAsync(
    model:        "claude-opus-4-6",
    systemPrompt: "You are a helpful assistant.",
    messages:     conversationHistory,
    tools:        toolDefinitions,
    ct:           cancellationToken);
```

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

Every `SendAsync` / `SendAgentAsync` call records:

- **Activity** `claude.send` / `claude.send_agent` with `model` and `stop_reason` tags
- **Counter** `claude.requests` / `claude.errors` with `model` tag
- **Histogram** `claude.duration` (ms) and `claude.tokens` (`input`/`output`)

Expose `ActivitySourceName` (`Agency.Llm.Claude`) and `MeterName` to your OTel pipeline.

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.Llm.Common]] | Implements `ILlmClient`; uses all message and tool types |
| [[Agency.Agentic]] | `Agent` calls `SendAgentAsync` on every loop iteration |
| [[Agency.Agentic.Console]] | Instantiated when `Agent:Provider = "Claude"` in config |
