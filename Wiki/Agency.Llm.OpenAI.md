# Agency.Llm.OpenAI

#llm #openai #implementation #observability #lmstudio

## What It Is

`Agency.Llm.OpenAI` is the OpenAI-compatible implementation of [[Agency.Llm.Common]]'s `ILlmClient`. It works with any OpenAI-protocol-compatible endpoint: OpenAI cloud, Azure OpenAI, LM Studio, Ollama, and others. The project uses the official `Azure.AI.OpenAI` / `OpenAI` .NET SDK.

## How It Works

```csharp
var client = new OpenAIClient(Options.Create(new LlmClientOptions
{
    ApiKey  = "lm-studio",
    BaseUrl = "http://llm-host.example:1234/v1",   // LM Studio
}));

// Simple prompt
LlmResponse reply = await client.SendAsync("qwen/qwen3-coder-next", "system", "Explain HNSW indexes.");

// Streaming simple prompt
await foreach (LlmStreamChunk chunk in client.StreamAsync("qwen/qwen3-coder-next", "system", "prompt"))
{
    if (chunk.Text is not null) Console.Write(chunk.Text);
}

// Agentic turn
AgentLlmResponse response = await client.SendAgentAsync(
    model:        "qwen/qwen3-coder-next",
    systemPrompt: "You are a helpful assistant.",
    messages:     conversationHistory,
    tools:        toolDefinitions,
    ct:           cancellationToken);

// Streaming agentic turn — tool-use blocks are yielded after the stream ends (OpenAI protocol)
await foreach (AgentStreamChunk chunk in client.StreamAgentAsync("qwen3", systemPrompt, messages, tools, ct))
{
    if (chunk.Text is not null)       Console.Write(chunk.Text);
    if (chunk.ToolUse is not null)    HandleToolUse(chunk.ToolUse);
    if (chunk.StopReason is not null) Console.WriteLine(chunk.Usage);
}

// List available models
IReadOnlyList<Model> models = await client.GetModelsAsync();
```

`ClientType` returns `"OpenAI"`.

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
- Metrics: `llm.client.requests`, `llm.client.errors`, `llm.client.duration`, `llm.client.tokens`
- Tags: `gen_ai.system=openai`, `gen_ai.request.model`, `llm.method`, `gen_ai.token.type`

> **Note**: For OpenAI, `StreamAgentAsync` accumulates all tool-call argument chunks before yielding `ToolUse` blocks (they all arrive after the terminal chunk). Claude's `StreamAgentAsync` yields each tool block as soon as its JSON is fully received.

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.Llm.Common]] | Implements `ILlmClient`; uses all message and tool types |
| [[Agency.Agentic]] | `Agent` calls `SendAgentAsync` on every loop iteration |
| [[Agency.Agentic.Console]] | Default provider when `Agent:Provider = "OpenAI"` in config |
| [[Agency.Embeddings.OpenAI]] | Sibling project — both talk to OpenAI-compatible APIs but for different purposes (chat vs. embeddings) |
