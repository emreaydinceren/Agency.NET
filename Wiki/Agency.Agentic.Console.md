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
[Agent] responds here (rendered as Markdown)
  ↳ +230 in, +47 out  [Success]

> /exit
Session ended  ·  3 turns  ·  1,234 in, 321 out total
```

### Multi-Turn Architecture

Multi-turn state is managed by `Agent.ChatAsync`. The console calls it with the current `Context` on every turn; the method handles seeding on the first turn and appending on subsequent turns:

```csharp
ctx ??= Agent.CreateContext(input);

await foreach (AgentEvent evt in _agent.ChatAsync(input, ctx, _options, sessionCts.Token))
{
    // handle events ...
}
```

### Streaming Display

When `Agent` is configured with `stream=true` (the default), the console receives `TextDeltaEvent`s and buffers them internally. When `AssistantTurnEvent` arrives, the buffer is flushed and rendered as Markdown via `NTokenizers.Extensions.Spectre.Console`:

```csharp
case TextDeltaEvent delta:
    streamingBuffer.Append(delta.Delta);   // accumulate live tokens
    break;

case AssistantTurnEvent:
    // flush + render the full buffered text as Markdown
    await AnsiConsole.Console.WriteMarkdownAsync(ms, MarkdownStyles.Default, ...);
    break;
```

If streaming was not used (batch mode), `AssistantTurnEvent` is handled directly with `PrintAssistantTurnAsync`.

### Command System

Input lines starting with `/` are dispatched to `CommandManager`, which matches against `CommandRegistery.Commands`:

| Command | Behaviour |
|---|---|
| `/exit` | Returns `CommandContinuation.ExitSession` — terminates the REPL |
| `/help` | Returns `CommandContinuation.Continue` (no-op placeholder) |
| `/model` | Opens `ConsolePicker` to select from all available models |

When the user types `/`, `ConsolePicker` appears inline to autocomplete from the registered command list. Selecting a model via `/model` calls `session.SetAgent(newAgent)` to hot-swap the active `Agent`.

### Ctrl+C Handling

A single `sessionCts` covers the whole session. `Console.CancelKeyPress` cancels it but suppresses process termination (`e.Cancel = true`). Subsequent turns re-check `sessionCts.IsCancellationRequested` to decide whether to exit the REPL.

## Configuration

```json
{
  "Agent": {
    "Provider": "OpenAI",
    "OpenAI": {
      "BaseUrl": "http://llm-host.example:1234/v1",
      "ApiKey":  "lm-studio",
      "Model":   "google/gemma-4-e2b"
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
