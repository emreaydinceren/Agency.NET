# Agency.Llm.OpenAI

#llm #openai #implementation #observability #lmstudio

## What It Is

`Agency.Llm.OpenAI` is the OpenAI-compatible implementation of [[Agency.Llm.Common]]'s `ILlmClient`. It works with any OpenAI-protocol-compatible endpoint: OpenAI cloud, Azure OpenAI, LM Studio, Ollama, and others. The project uses the official `Azure.AI.OpenAI` / `OpenAI` .NET SDK.

## How It Works

```csharp
var client = new OpenAIClient(Options.Create(new OpenAIClientOptions
{
    ApiKey  = "lm-studio",
    BaseUrl = "http://llm-host.example:1234/v1",   // LM Studio
}));

// Simple prompt
string reply = await client.SendAsync("qwen/qwen3-coder-next", "Explain HNSW indexes.");

// Agentic turn
AgentLlmResponse response = await client.SendAgentAsync(
    model:        "qwen/qwen3-coder-next",
    systemPrompt: "You are a helpful assistant.",
    messages:     conversationHistory,
    tools:        toolDefinitions,
    ct:           cancellationToken);
```

## OpenAI SDK Mapping

The OpenAI SDK has a different protocol structure from Anthropic — especially for tool results:

| Our type | OpenAI SDK type | Notes |
|---|---|---|
| `TextBlock` | `ChatMessageContentPart.Text` | |
| `ToolUseBlock` | `ChatToolCall.CreateFunctionToolCall(id, name, BinaryData)` | Input serialized to JSON |
| `ToolResultBlock` | `ToolChatMessage(toolCallId, content)` | **Each result is a separate message**, unlike Anthropic's single user message |
| `ToolDefinition` | `ChatTool.CreateFunctionTool(name, desc, BinaryData)` | Schema passed as `BinaryData` from `JsonElement.GetRawText()` |

### Multi-message tool result expansion

```csharp
// One ToolResultBlock → one separate ToolChatMessage
private static IEnumerable<ChatMessage> ConvertToOpenAIMessages(AgentMessage message)
{
    if (message.Role == MessageRole.User)
    {
        var toolResults = message.Content.OfType<ToolResultBlock>().ToList();
        if (toolResults.Count > 0)
        {
            foreach (var trb in toolResults)
                yield return new ToolChatMessage(trb.ToolUseId, trb.Content);
        }
        else
        {
            yield return new UserChatMessage(...);
        }
    }
    // ...
}
```

## Observability

Same pattern as [[Agency.Llm.Claude]]:
- `ActivitySource` name: `Agency.Llm.OpenAI`
- `Meter` name: `Agency.Llm.OpenAI`
- Metrics: `openai.requests`, `openai.errors`, `openai.duration`, `openai.tokens`

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.Llm.Common]] | Implements `ILlmClient`; uses all message and tool types |
| [[Agency.Agentic]] | `Agent` calls `SendAgentAsync` on every loop iteration |
| [[Agency.Agentic.Console]] | Default provider when `Agent:Provider = "OpenAI"` in config |
| [[Agency.Embeddings.OpenAI]] | Sibling project — both talk to OpenAI-compatible APIs but for different purposes (chat vs. embeddings) |
