# Agency.Llm.Common

#llm #abstractions #interface #messages #tools

## What It Is

`Agency.Llm.Common` is the LLM abstraction layer. It defines:

1. **`ILlmClient`** — the single interface all LLM provider implementations must satisfy.
2. **Message types** — provider-agnostic conversation objects (`AgentMessage`, `TextBlock`, `ToolUseBlock`, `ToolResultBlock`, `ThinkingBlock`, `ContentBlock`).
3. **Tool types** — `ToolDefinition`, `ToolResult`, `ITool`, `IToolRegistry`, `LlmTokenUsage`.

All other projects communicate with LLM providers through these abstractions, keeping the call sites completely provider-agnostic.

## Key Types

### `ILlmClient`

```csharp
public interface ILlmClient
{
    /// <summary>Provider display name ("Claude", "OpenAI", …).</summary>
    string ClientType { get; }

    /// <summary>Single-turn request/response (non-agentic).</summary>
    Task<LlmResponse> SendAsync(
        string model, string systemPrompt, string userPrompt,
        long? maxTokens = 1024, float? temperature = null,
        CancellationToken ct = default);

    /// <summary>Streaming single-turn — yields text-delta chunks, then a terminal chunk with usage.</summary>
    IAsyncEnumerable<LlmStreamChunk> StreamAsync(
        string model, string systemPrompt, string userPrompt,
        long? maxTokens = 1024, float? temperature = null,
        CancellationToken ct = default);

    /// <summary>Full agentic turn — system prompt + conversation history + tools.</summary>
    Task<AgentLlmResponse> SendAgentAsync(
        string model, string systemPrompt,
        IReadOnlyList<AgentMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken ct = default);

    /// <summary>Streaming agentic turn — yields text deltas live and complete tool-use blocks.</summary>
    IAsyncEnumerable<AgentStreamChunk> StreamAgentAsync(
        string model, string systemPrompt,
        IReadOnlyList<AgentMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken ct = default);

    /// <summary>Lists available models from the provider.</summary>
    Task<IReadOnlyList<Model>> GetModelsAsync(CancellationToken ct = default);
}
```

### Streaming Chunk Types

```csharp
// Simple streaming (SendAsync → StreamAsync)
record LlmStreamChunk(string? Text, StopReason? StopReason, LlmTokenUsage? Usage);
// Text chunks: Text set, others null.
// Terminal chunk: Text null, StopReason + Usage set.

// Agentic streaming (StreamAgentAsync)
record AgentStreamChunk(string? Text, ToolUseBlock? ToolUse, StopReason? StopReason, LlmTokenUsage? Usage);
// Text delta chunks: Text set, others null.
// Tool-use chunks: ToolUse set, others null (emitted once a full tool-call JSON is received).
// Terminal chunk: StopReason + Usage set, Text + ToolUse null.
```

### Message Types

```csharp
// Conversation building blocks
AgentMessage message = new(MessageRole.User, [new TextBlock("Hello!")]);
AgentMessage assistant = new(MessageRole.Assistant, [
    new TextBlock("I'll look that up."),
    new ToolUseBlock(id: "call_1", name: "search", input: jsonElement),
]);
AgentMessage toolResult = new(MessageRole.User, [
    new ToolResultBlock(toolUseId: "call_1", content: "Result text", isError: false),
]);
```

### Tool Types

```csharp
// Defining a tool
var tool = new ToolDefinition(
    Name: "get_weather",
    Description: "Returns current weather for a city.",
    InputSchema: JsonDocument.Parse("""{"type":"object","properties":{"city":{"type":"string"}}}""").RootElement);

// Implementing a tool
public sealed class WeatherTool : ITool
{
    public string Name => "get_weather";
    public string Description => "Returns current weather for a city.";
    public JsonElement InputSchema => ...;
    public Task<ToolResult> InvokeAsync(JsonElement input, CancellationToken ct)
        => Task.FromResult(new ToolResult("Sunny, 22°C"));
}
```

### `LlmTokenUsage` and `Model`

```csharp
public sealed record LlmTokenUsage(long InputTokens, long OutputTokens)
{
    public long TotalTokens => InputTokens + OutputTokens;
}

public sealed record Model(string Id, string Name);
```

### `StopReason`

Values include `Stop`, `EndTurn`, `MaxTokens`, `ToolUse`, `ToolCalls`, `FunctionCall`, `ContentFilter`, `Refusal`, `PauseTurn`, and `Unknown`.

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.Llm.Claude]] | Implements `ILlmClient` using the Anthropic SDK |
| [[Agency.Llm.OpenAI]] | Implements `ILlmClient` using the OpenAI SDK |
| [[Agency.Agentic]] | `Agent` takes `ILlmClient` and calls `SendAgentAsync` on each iteration |
| [[Agency.Agentic.Console]] | Wires up a concrete client and passes it to `Agent` |
