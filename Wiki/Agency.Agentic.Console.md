# Agency.Agentic.Console

#console #repl #chat #harness #interactive

## What It Is

`Agency.Agentic.Console` is an interactive REPL (Read-Eval-Print Loop) chat harness that drives [[Agency.Agentic]]'s `Agent` loop from a terminal. It supports:

- **Multi-turn conversation** — each message in the session is appended to the same `Context`, so the model remembers what was said earlier.
- **Ctrl+C interruption** — cancels only the current LLM turn; the session continues.
- **Provider switching** — configure `Agent:Provider` in `appsettings.json` to use `"OpenAI"` or `"Claude"`.
- **Per-turn token delta** — prints `↳ +N in, +N out [Success]` after each response.
- **Session summary** — prints total turns and total tokens on exit.

## How It Works

On startup the console reads `appsettings.json` to select the provider and model, constructs the appropriate `ILlmClient` (`OpenAIClient` or `ClaudeClient`), and enters a REPL loop:

```
> [user types a message]
[Agent] responds here
  ↳ +230 in, +47 out  [Success]

> exit
Session ended  ·  3 turns  ·  1,234 in, 321 out total
```

### Multi-Turn Architecture

The first user message creates the `Context`. Subsequent messages are appended directly to the conversation before calling `RunAsync` again — `Agent.RunAsync` only seeds from `Query.Prompt` when the conversation history is empty:

```csharp
if (ctx is null)
{
    ctx = new Context
    {
        Query    = new QueryContext { Prompt = input },
        Temporal = new TemporalContext { CurrentDateUtc = DateTimeOffset.UtcNow },
    };
}
else
{
    ctx.Conversation.Append(new AgentMessage(MessageRole.User, [new TextBlock(input)]));
}

await foreach (AgentEvent evt in agent.RunAsync(ctx, turnCts.Token)) { ... }
```

### Ctrl+C Handling

Two `CancellationTokenSource` instances are linked together:

- `sessionCts` — triggers when Ctrl+C is pressed a second time to exit the session.
- `turnCts` — linked to `sessionCts` and created fresh per turn; cancels just the current LLM call on first Ctrl+C.

```csharp
using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(sessionCts.Token);
```

## Configuration

```json
{
  "Agent": {
    "Provider": "OpenAI",
    "OpenAI": {
      "BaseUrl": "http://llm-host.example:1234/v1",
      "ApiKey":  "lm-studio",
      "Model":   "qwen/qwen3-coder-next"
    },
    "Claude": {
      "ApiKey": "sk-ant-...",
      "Model":  "claude-opus-4-6"
    }
  }
}
```

Switch provider by changing `"Provider"` — no code changes needed.

## Color Scheme

| Color | Meaning |
|---|---|
| Cyan | Prompt `>` and banner borders |
| Green | `[Agent]` response prefix |
| Yellow | Provider/model names; tool names |
| Magenta | `→ calling <tool>` |
| DarkGray | Token stats and session summary |
| DarkYellow | `[interrupted]` on Ctrl+C |

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.Agentic]] | Drives `Agent.RunAsync`; handles all `AgentEvent` variants |
| [[Agency.Llm.Claude]] | Instantiated when `Provider = "Claude"` |
| [[Agency.Llm.OpenAI]] | Instantiated when `Provider = "OpenAI"` (default) |
| [[Agency.Llm.Common]] | Uses `AgentMessage`, `TextBlock`, `MessageRole` |
