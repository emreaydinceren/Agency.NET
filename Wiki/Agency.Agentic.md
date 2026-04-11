# Agency.Agentic

#agent #loop #tools #events #orchestration

## What It Is

`Agency.Agentic` is the autonomous agent loop library. It takes an [[Agency.Llm.Common]] `ILlmClient`, a `Context` object (user query + conversation + tools + memory), and drives a think → act → observe loop until a stop condition fires. The loop yields a stream of typed `AgentEvent` records so callers can react to each stage without polling.

## How It Works

The loop, implemented in `Agent.RunAsync`, follows this pattern on every iteration:

```
1. Seed conversation with the user prompt (first iteration only)
2. Build system prompt from Context
3. Call ILlmClient.SendAgentAsync → AssistantTurnEvent
4. Evaluate StopCondition
   └── Stop? → AgentResultEvent → break
5. Extract ToolUseBlocks from the assistant response
6. Execute all tool calls in parallel → ToolInvokedEvent (one per tool)
7. Append tool results to conversation as a User message
8. Repeat
```

### Basic Usage

```csharp
var agent = new Agent(llmClient, "claude-opus-4-6", stream: false);

var ctx = new Context
{
    Query    = new QueryContext { Prompt = "What files are in the current directory?" },
    Temporal = new TemporalContext { CurrentDateUtc = DateTimeOffset.UtcNow },
    Tools    = new ToolContext { Registry = myToolRegistry },
};

await foreach (AgentEvent evt in agent.RunAsync(ctx, cancellationToken))
{
    switch (evt)
    {
        case AssistantTurnEvent turn:
            Console.WriteLine(turn.Message);
            break;
        case ToolInvokedEvent tool:
            Console.WriteLine($"Called {tool.ToolName}");
            break;
        case AgentResultEvent result:
            Console.WriteLine($"Done: {result.Status} — {result.FinalText}");
            break;
    }
}
```

### Multi-Turn Conversations

Reuse the same `Context` across REPL iterations. `RunAsync` only seeds the conversation when `ctx.Conversation.Messages.Count == 0`, so you can append user messages manually for subsequent turns:

```csharp
// First turn: ctx.Query.Prompt seeds the conversation
ctx = new Context { Query = new QueryContext { Prompt = input }, ... };

// Subsequent turns: append before calling RunAsync again
ctx.Conversation.Append(new AgentMessage(MessageRole.User, [new TextBlock(input)]));
await foreach (var evt in agent.RunAsync(ctx, ct)) { ... }
```

## Context Object

`Context` is a `sealed record` that aggregates all session state:

| Property | Type | Purpose |
|---|---|---|
| `Query` | `QueryContext` | The user's initial prompt |
| `Knowledge` | `KnowledgeContext` | Facts injected into every system prompt |
| `Memory` | `MemoryContext` | Short-term (prior turns) and long-term (system prompt) memory |
| `Tools` | `ToolContext` | `IToolRegistry` with available tools |
| `User` | `UserSpecificContext` | Caller display name |
| `Temporal` | `TemporalContext` | UTC timestamp for grounding |
| `Environment` | `EnvironmentalContext` | OS info for grounding |
| `Conversation` | `IConversationManager` | Mutable message history |
| `TotalUsage` | `LlmTokenUsage` | Accumulated token counts |

## Stop Conditions

```csharp
// Default: stop when no tool calls OR after 20 iterations
StopConditions.Any(StopConditions.NoToolCalls, StopConditions.StepCountIs(20))

// Custom compositions
StopConditions.Any(
    StopConditions.NoToolCalls,
    StopConditions.BudgetExceeded(0.50m),   // $0.50 USD
    StopConditions.TokensExceeded(100_000));
```

## Agent Events

| Event | When |
|---|---|
| `SessionStartedEvent` | First, before any LLM call |
| `AssistantTurnEvent` | After each LLM response |
| `ToolInvokedEvent` | After each tool execution |
| `IterationCompletedEvent` | After each full iteration (LLM + tools) |
| `TextDeltaEvent` | Streaming path only — each text token chunk |
| `AgentResultEvent` | Last event — contains status, final text, total usage |

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.Llm.Common]] | `Agent` depends on `ILlmClient` and all message/tool types |
| [[Agency.Llm.Claude]] | Concrete `ILlmClient` that can be injected |
| [[Agency.Llm.OpenAI]] | Concrete `ILlmClient` that can be injected |
| [[Agency.Agentic.Console]] | REPL harness that drives `Agent` interactively |
