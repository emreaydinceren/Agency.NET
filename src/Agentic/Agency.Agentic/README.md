# Agency.Agentic

Core agentic loop for the Agency AI Toolkit — multi-turn LLM agents with tool calling, structured context, and memory.

## Install

```
dotnet add package Agency.Agentic
```

## Types

- **`Agent`** — runs the agentic loop: builds system prompt from `Context`, calls the LLM, handles tool invocations, and accumulates `ChatSession` history.
- **`ChatSession`** — maintains conversation turns and token usage across the session.
- **`Context`** — composes `QueryContext`, `TemporalContext`, `ToolContext`, `KnowledgeContext`, `MemoryContext`, and more into a single system prompt.
- **`ToolRegistry`** — registers callable tools the agent can invoke during inference.
- **`StopConditions`** — configures when the agentic loop should halt.

## Usage

```csharp
services.AddScoped<Agent>();
services.AddScoped<ToolRegistry>(sp => new ToolRegistry()
    .Register("search", sp.GetRequiredService<SearchTool>()));

// Run a single turn
var response = await agent.RunAsync(userMessage, cancellationToken);
```

Part of the [Agency AI Toolkit](https://github.com/emre/Agency) — an open-source .NET agentic AI toolkit.
