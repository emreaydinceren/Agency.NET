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
    /// <summary>Single-turn request/response (non-agentic).</summary>
    Task<string> SendAsync(string model, string prompt, CancellationToken ct = default);

    /// <summary>Streaming single-turn response.</summary>
    IAsyncEnumerable<string> StreamAsync(string model, string prompt, CancellationToken ct = default);

    /// <summary>Full agentic turn — system prompt + conversation history + tools.</summary>
    Task<AgentLlmResponse> SendAgentAsync(
        string model,
        string systemPrompt,
        IReadOnlyList<AgentMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken ct = default);
}
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

### `LlmTokenUsage`

```csharp
public sealed record LlmTokenUsage(long InputTokens, long OutputTokens)
{
    public long TotalTokens => InputTokens + OutputTokens;
}
```

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.Llm.Claude]] | Implements `ILlmClient` using the Anthropic SDK |
| [[Agency.Llm.OpenAI]] | Implements `ILlmClient` using the OpenAI SDK |
| [[Agency.Agentic]] | `Agent` takes `ILlmClient` and calls `SendAgentAsync` on each iteration |
| [[Agency.Agentic.Console]] | Wires up a concrete client and passes it to `Agent` |
