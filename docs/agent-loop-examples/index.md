# Agent Loop Reference: 20 Implementations

> Curated for `src/Agency.Agentic/`. Studies how real agent loops shape their
> in-memory `Context`, store tool calls + tool results, and serialize that
> shape into the `messages[]` array sent to the LLM on each iteration.

## Why this doc exists

`Agency.Agentic/Class1.cs` already defines a layered context skeleton
(`QueryContext`, `KnowledgeContext`, `MemoryContext`, `ToolContext`,
`UserSpecificContext`, `TemporalContext`, `EnvironmentalContext`, plus a
flat `MessageHistory : List<string>`) but no loop, no tool plumbing, no
LLM call yet. Before writing the loop, we want to answer three questions
by example:

1. **History shape** — what type stores the running conversation?
2. **Tool encoding** — exactly how are `tool_use` and `tool_result` recorded?
3. **Serialization** — how does that internal shape become the `messages[]`
   array on the wire?

Each entry below answers all three in <250 words.

---

## TL;DR comparison table

| #  | Name                                       | Lang     | Category             | History shape                                       | Tool result encoding                              | Compaction         |
|----|--------------------------------------------|----------|----------------------|-----------------------------------------------------|---------------------------------------------------|--------------------|
| 1  | Anthropic computer-use `loop.py`           | Python   | Minimal / vendor     | `list[BetaMessageParam]` (role + content blocks)    | `{type:"tool_result",tool_use_id,content,is_error}` inside a `user` message | Image-only window  |
| 2  | shareAI mini-claude-code v1                | Python   | Minimal              | flat `messages` list, Anthropic blocks              | tool_result block back as user                    | none               |
| 3  | shareAI learn-claude-code s01              | Python   | Minimal pedagogy     | flat `messages` list                                | tool_result block back as user                    | none               |
| 4  | Simon Willison — "unreasonable loop"       | mixed    | Blog / minimalism    | flat `messages` list                                | provider-native blocks                            | none               |
| 5  | Braintrust — canonical while-loop          | TS       | Blog / minimal       | flat `messages` array                               | inline `messages.push(...toolResults)`            | none               |
| 6  | Victor Dibia — Agent Execution Loop        | Python   | Blog tutorial        | flat `messages` list                                | OpenAI `tool` role w/ `tool_call_id`              | discussed          |
| 7  | Anthropic Agent SDK — `agent-loop` docs    | both     | Vendor reference     | `AssistantMessage` / `UserMessage` stream           | `ToolUseBlock` / `ToolResultBlock`                | **auto-compact**   |
| 8  | Temporal AI cookbook — agentic loop        | Python   | Vendor cookbook      | OpenAI `messages` w/ durable workflow state        | OpenAI `tool` role                                | workflow replay    |
| 9  | Vercel AI SDK — Loop Control               | TS       | Vendor framework     | `ModelMessage[]` (role + parts)                     | `tool-call` / `tool-result` parts                 | `stopWhen`         |
| 10 | smolagents `MultiStepAgent`                | Python   | Framework            | **`memory.steps[]`** (TaskStep, ActionStep, …)      | `ActionStep.observations` (string)                | `step.write_memory_to_messages()` |
| 11 | AWS Strands Agents — Agent Loop            | Python   | Framework            | accumulated history via "conversation manager"     | tool result message appended                      | conversation mgr   |
| 12 | LangGraph `create_react_agent`             | Python   | Framework            | **`MessagesState`** + reducer (additive)            | `ToolMessage` (LangChain)                         | summarization node |
| 13 | Claude Agent SDK (Python `query()`)        | Python   | Vendor SDK           | async iterator of `Message` subclasses              | `ToolUseBlock` / `ToolResultBlock` content blocks | auto-compact       |
| 14 | Microsoft Agent Framework (.NET)           | C# / Py  | Framework            | graph workflow + `ChatMessage` list                 | `ChatMessage` w/ `FunctionCall` / `FunctionResult`| graph nodes        |
| 15 | MS Foundry — .NET AI Skills Executor       | **C#**   | Blog (highest value) | **`List<ChatMessage>`** (System/User/Assistant/Tool)| **`ToolChatMessage(toolCall.Id, result)`**        | `MaxIterations`    |
| 16 | AIAgentSharp                               | **C#**   | Framework            | `ConversationHistory` class, ReAct/ToT/CoT          | provider-specific function-call message           | n/a                |
| 17 | LLMTornado (.NET)                          | **C#**   | Framework            | orchestrator/runner/advancer + handoff messages    | per-connector message types                       | handoffs           |
| 18 | Vercel AI SDK `generateText` w/ `stopWhen` | TS       | Framework            | `CoreMessage[]` w/ `tool-call` / `tool-result` parts| message parts (typed union)                       | step counter       |
| 19 | nibzard — "The Agent is The Loop"          | mixed    | Blog                 | flat `messages`                                     | provider-native                                   | none               |
| 20 | Oracle Devs — "What is the AI agent loop?" | mixed    | Blog (architecture)  | accumulated conversation history                    | tool result feedback                              | conceptual         |

**Key contrast (axis A):** Anthropic vs OpenAI shape.
**Anthropic style** (rows 1, 7, 13): tool calls live as `tool_use` content
blocks *inside an assistant message*; tool results live as `tool_result`
content blocks *inside a user message*. Both share the same `id` /
`tool_use_id` to pair them. There is no dedicated "tool" role.
**OpenAI style** (rows 6, 8, 9, 14, 15, 18): there *is* a dedicated `tool`
role. Tool calls live in the assistant message as a `tool_calls` array
with an `id`; tool results are emitted as separate `ToolMessage` /
`ToolChatMessage` rows referencing that `tool_call_id`.

**Key contrast (axis B):** Messages-as-history vs Steps-as-history.
**Messages-as-history** (most rows): the canonical state *is* the
`messages[]` array; appending = remembering. Simple, but couples wire
format to memory layout.
**Steps-as-history** (row 10 smolagents, row 14 MS Agent Framework, partly
row 11 Strands): the canonical state is a list of richer `Step` records
(task / planning / action / observation) and `messages[]` is *derived*
from it just before each LLM call via `write_memory_to_messages()` or
equivalent. This is what `Agency.Agentic`'s layered `Context` is
trending toward.

---

## 1. Anthropic `claude-quickstarts/computer-use-demo/loop.py`

Source: https://github.com/anthropics/claude-quickstarts/blob/main/computer-use-demo/computer_use_demo/loop.py

The canonical reference loop. ~200 LOC, used for computer-use demos.

```python
while True:
    raw = client.beta.messages.with_raw_response.create(
        max_tokens=max_tokens, messages=messages, model=model,
        system=[system], tools=tool_collection.to_params(), betas=betas)
    response_params = _response_to_params(raw.parse())
    messages.append({"role": "assistant", "content": response_params})

    tool_result_content = []
    for block in response_params:
        if block.get("type") == "tool_use":
            result = await tool_collection.run(block["name"], block.get("input", {}))
            tool_result_content.append(_make_api_tool_result(result, block["id"]))

    if not tool_result_content:
        return messages
    messages.append({"role": "user", "content": tool_result_content})
```

**History shape.** `messages: list[BetaMessageParam]`. Each element is
`{"role": "user" | "assistant", "content": list[BetaContentBlockParam]}`.

**Block types.** `text` / `tool_use` / `tool_result` / `image` / `thinking`.
`tool_use` carries `{type, id, name, input}`. `tool_result` carries
`{type, tool_use_id, content, is_error}`.

**Tool result encoding.** Built by `_make_api_tool_result(result, id)` and
appended as a single `user` message whose `content` is a *list* of
`tool_result` blocks (one per parallel tool call). This is the Anthropic
convention: tool *outputs* are user turns.

**Compaction.** `_maybe_filter_to_n_most_recent_images()` walks all
messages, finds `tool_result` blocks containing image content, and drops
older ones in chunks aligned to the prompt-cache breakpoints set by
`_inject_prompt_caching()` on the 3 most recent user messages.

**For Agency.Agentic.** This is the model to copy if `Agency.Llm.Claude` is
the primary backend. Replace `messages: List<string>` with
`List<MessageParam>` where `MessageParam` mirrors role + content-block
union.

---

## 2. shareAI `mini-claude-code` v1_basic_agent.py

Source: https://github.com/shareAI-lab/mini-claude-code

Progressive series — `v0_bash_agent.py` (~50 LOC, one tool, one loop),
`v1_basic_agent.py` (~200 LOC, 4 tools, full loop), then v2 todo, v3
sub-agents, v4 skills.

**Loop.** The README states it explicitly:
```
while True:
    response = model(messages, tools)
    if response.stop_reason != "tool_use":
        return response.text
    results = execute(response.tool_calls)
    messages.append(results)
```

**History / encoding.** Standard Anthropic blocks (same as #1) — the
project's whole point is "this is all you need." Worth reading v0 → v4 in
order to see how subagents (v3) are layered on without changing the inner
loop.

**For Agency.Agentic.** Best teaching gradient — read v0, v1, v3 in order
before writing the first loop iteration.

---

## 3. shareAI `learn-claude-code` (sessions s01–s12)

Source: https://github.com/shareAI-lab/learn-claude-code

12 sessions building from "one loop & Bash is all you need" (s01) up to
isolated autonomous execution. Same Anthropic message shape as #1/#2;
value here is the *progression* — each session adds one capability.

**For Agency.Agentic.** Use as the curriculum for layering: s01 = the
loop, then add tools, then memory, then sub-agents.

---

## 4. Simon Willison — "the unreasonable effectiveness of an LLM agent loop with tool use"

Sources:
- https://simonwillison.net/tags/llm-tool-use/
- HN thread: https://news.ycombinator.com/item?id=43998472

Position piece. The insight: an agent is a `while` loop with tools, and
the *interesting* engineering is in the tool design and the loop
termination, not the LLM call. Willison's `llm` CLI shipped tool use in
0.26 and the `llm-loop-plugin` adds autonomous looping over a stated
goal.

**For Agency.Agentic.** The right mental anchor before writing any code:
keep the loop trivial; spend the budget on tool quality and loop exit
conditions.

---

## 5. Braintrust — "The canonical agent architecture: a while loop with tools"

Source: https://www.braintrust.dev/blog/agent-while-loop

```typescript
while (!done) {
  const response = await callLLM();
  messages.push(response);
  if (response.toolCalls) {
    messages.push(...await Promise.all(
      response.toolCalls.map(tc => tool(tc.args))));
  } else {
    messages.push(getUserMessage());
  }
}
```

**History.** Single `messages` array. The article calls it "the
transcript" and explicitly says: *"The transcript is where a lot of
reasoning actually happens."* — meaning tool results are not just
breadcrumbs, they're load-bearing context.

**For Agency.Agentic.** Read this *first* — it's the shortest argument for
why messages-as-transcript is enough for the v1 loop.

---

## 6. Victor Dibia — "The Agent Execution Loop"

Source: https://newsletter.victordibia.com/p/the-agent-execution-loop-how-to-build

Full Python walkthrough. OpenAI-style messaging: assistant message has
`tool_calls: [{id, type:"function", function:{name, arguments}}]`; tool
results are emitted as `{role:"tool", tool_call_id, content}`.

**For Agency.Agentic.** Best example to study if `Agency.Llm.OpenAI` is
ever the primary backend, or if you want the loop to be provider-agnostic
internally.

---

## 7. Anthropic Agent SDK — "How the agent loop works"

Source: https://platform.claude.com/docs/en/agent-sdk/agent-loop

Official documentation. The five message types yielded by `query()`:

- `SystemMessage` — session lifecycle (`init`, `compact_boundary`)
- `AssistantMessage` — Claude's per-turn response (text + tool calls)
- `UserMessage` — emitted *after* each tool execution, carrying the
  tool result content sent back to Claude
- `StreamEvent` — partial-message deltas
- `ResultMessage` — final, with `result`, `total_cost_usd`, `usage`,
  `session_id`

**Critical detail: Automatic compaction.** When context approaches the
limit, the SDK summarizes older history and emits a `compact_boundary`
message. CLAUDE.md is *re-injected on every request* — so persistent
rules belong there, not in the initial user prompt. This is the most
important architectural decision in the doc.

**Turns.** Capped by `max_turns` (counts tool-use turns only) and
`max_budget_usd`. Result subtypes: `success`, `error_max_turns`,
`error_max_budget_usd`, `error_during_execution`,
`error_max_structured_output_retries`.

**For Agency.Agentic.** Steal: (a) the five-message-type taxonomy as the
public API of the loop; (b) `max_turns` + `max_budget_usd` as built-in
guardrails; (c) the *re-inject on every request* pattern for
`KnowledgeContext` (treat it like CLAUDE.md, not like history).

---

## 8. Temporal AI cookbook — "Basic Agentic Loop with Tool Calling"

Source: https://docs.temporal.io/ai-cookbook/agentic-loop-tool-call-openai-python

Python + OpenAI, but the loop runs *inside a Temporal workflow*, so the
`messages` list is durable workflow state — survives crashes, can be
replayed.

**History.** Standard OpenAI `messages` list, but persisted as workflow
state instead of process memory. Tool calls become Temporal *activities*.

**For Agency.Agentic.** Read for the durability angle — it's the closest
public example of the "stateful handoff" pattern called out in the
`Class1.cs` comment block (durable progress note across sessions).

---

## 9. Vercel AI SDK — Agents: Loop Control

Source: https://ai-sdk.dev/docs/agents/loop-control

```typescript
const result = await generateText({
  model, tools, messages,
  stopWhen: stepCountIs(20),  // default
});
```

**History.** `ModelMessage[]`. Each message has a `role` and a `content`
that is either a string or an array of *parts*: `text-part`,
`tool-call-part`, `tool-result-part`, etc. The SDK appends each step's
output to the conversation automatically and re-invokes until either
`stopWhen` triggers or a text-only response is produced.

**Stopping.** `stopWhen` accepts an array; the loop halts on the first
satisfied condition. `stepCountIs(20)` is the default safety net.
`isLoopFinished()` removes the cap entirely.

**For Agency.Agentic.** The `stopWhen` pattern is worth porting verbatim
— it's `Func<Context, bool>` in C# terms, and a clean place to slot in
budget/turn/custom predicates.

---

## 10. smolagents — `MultiStepAgent`

Source: https://github.com/huggingface/smolagents

Different paradigm. Instead of a `messages[]` transcript, smolagents
stores `agent.memory.steps: list[MemoryStep]`. The conceptual loop:

```python
memory = [user_defined_task]  # actually agent.memory.steps
while llm_should_continue(memory):
    action = llm_get_next_action(memory)   # serializes steps -> messages
    observations = execute_action(action)
    memory += [action, observations]
```

**Step types.** `TaskStep`, `PlanningStep`, `ActionStep`. Each
`ActionStep` carries the model output, the parsed tool call (or code),
the observations (string), and any error. **`messages[]` is derived from
`memory.steps` on each LLM call** via a step-to-messages serializer —
this is the cleanest example of the steps-as-history pattern.

**Two agent classes.** `CodeAgent` (LLM writes Python; executed by a
sandboxed `PythonExecutor`) vs `ToolCallingAgent` (LLM emits JSON tool
calls). The memory layer is the same; only the action format differs.

**For Agency.Agentic.** This is the closest match to the layered
`Context` skeleton already in `Class1.cs`. The pattern to steal:
**`Context` is the canonical state, `BuildMessages(Context)` is a pure
function called per iteration**. Don't store wire-format messages
directly.

---

## 11. AWS Strands Agents — Agent Loop

Source: https://strandsagents.com/docs/user-guide/concepts/agents/agent-loop/

Conceptual flow (their docs don't expose concrete class names but the
shape is clear):

1. Invoke model with accumulated context
2. Branch on `stop_reason`: `end_turn` → exit; `tool_use` → execute;
   `cancelled` / `max_tokens` → terminate
3. Execute tools with error handling (failures become *error result*
   messages, not exceptions)
4. Append results to history via a *conversation manager*
5. Loop

**History.** Managed by a "conversation manager" abstraction —
explicitly factored out so different strategies (full history, sliding
window, summarization) are pluggable.

**For Agency.Agentic.** The conversation-manager abstraction is worth
copying. Define `IConversationManager.Append(...)` and
`IConversationManager.BuildMessages()` as a seam from day one.

---

## 12. LangGraph `create_react_agent`

Source: https://github.com/langchain-ai/langgraph

LangGraph models the agent as a state graph. State is a `MessagesState`
TypedDict with a `messages` field whose reducer is *additive* (each node
returns new messages, the framework appends).

**Messages.** LangChain message classes: `HumanMessage`, `AIMessage`
(carries `tool_calls`), `ToolMessage` (carries `tool_call_id` + result),
`SystemMessage`. The ReAct agent is a 2-node graph: `agent` (LLM call) →
conditional → `tools` (executor) → back to `agent`.

**Compaction.** A separate "summarization" node can be inserted before
the agent node; it replaces older messages with a summary message.

**For Agency.Agentic.** The reducer pattern (state + node-returns-delta)
is overkill for v1, but the `MessagesState` shape is a clean middle
ground if you ever want graph-style branching.

---

## 13. Claude Agent SDK (Python `query()`)

Source: https://github.com/anthropics/claude-agent-sdk-python

The Python implementation of the SDK described in #7. `query(prompt,
options) -> AsyncIterator[Message]` where `Message` is one of
`SystemMessage` / `AssistantMessage` / `UserMessage` / `StreamEvent` /
`ResultMessage`. Content blocks are typed: `TextBlock`, `ToolUseBlock`,
`ToolResultBlock`, `ThinkingBlock`.

**For Agency.Agentic.** The async-iterator surface (`IAsyncEnumerable<AgentEvent>`
in C#) is the right shape for the public loop API — matches what
`ILlmClient.StreamAsync` already returns.

---

## 14. Microsoft Agent Framework (.NET + Python)

Source: https://github.com/microsoft/agent-framework

Graph-based workflows, explicit support for both Python and .NET. Uses
`ChatMessage` (from `Microsoft.Extensions.AI`) for history; tool calls
ride as `FunctionCallContent` and results as `FunctionResultContent`
inside the message's content list — closer to Anthropic's
content-blocks-in-a-message shape than to OpenAI's separate `tool` role.

**For Agency.Agentic.** This is the framework whose abstractions
(`ChatMessage`, `IChatClient`, `AIFunction`) `Agency.Llm.Abstractions`
will most likely converge with — worth reading before deciding whether
to invent or adopt.

---

## 15. Microsoft Foundry — "Building an AI Skills Executor in .NET" ⭐

Source: https://devblogs.microsoft.com/foundry/dotnet-ai-skills-executor-azure-openai-mcp/

**Highest-signal C# example in this list.** Direct port of the agent
pattern to `Microsoft.Extensions.AI` / OpenAI .NET SDK with MCP tools:

```csharp
public async Task<SkillResult> ExecuteAsync(SkillDefinition skill, string userInput)
{
    var messages = new List<ChatMessage>
    {
        new SystemChatMessage(skill.Instructions ?? "You are a helpful assistant."),
        new UserChatMessage(userInput)
    };
    var tools = BuildToolDefinitions(_mcpClient.GetAllTools());
    var iterations = 0;

    while (iterations++ < MaxIterations)
    {
        var response = await _openAI.GetCompletionAsync(messages, tools);
        messages.Add(new AssistantChatMessage(response));

        var toolCalls = response.ToolCalls;
        if (toolCalls is null || toolCalls.Count == 0)
            return new SkillResult { Response = response.Content.FirstOrDefault()?.Text ?? "", ToolCallCount = iterations - 1 };

        foreach (var toolCall in toolCalls)
        {
            var args = JsonSerializer.Deserialize<Dictionary<string, object?>>(toolCall.FunctionArguments);
            var result = await _mcpClient.ExecuteToolAsync(toolCall.FunctionName, args);
            messages.Add(new ToolChatMessage(toolCall.Id, result));
        }
    }
    throw new InvalidOperationException("Max iterations exceeded");
}
```

**History.** `List<ChatMessage>` with four concrete subclasses:
`SystemChatMessage`, `UserChatMessage`, `AssistantChatMessage`,
`ToolChatMessage`. OpenAI-style: tool results are their own message
*role*, not content blocks inside a user message.

**Tool result encoding.** `new ToolChatMessage(toolCall.Id, result)`.
Pairing key is `toolCall.Id` ↔ `ToolChatMessage.ToolCallId`.

**Compaction.** None — `MaxIterations` is the only safety net. Loop
throws on overrun rather than compacting.

**For Agency.Agentic.** **Single most relevant prior art.** The shape of
this loop is essentially what `Agency.Agentic.Agent.RunAsync` should
look like in v1 — except substitute Anthropic content blocks if Claude
is the primary backend, and replace `MaxIterations` with a richer
`stopWhen`-style predicate (#9).

---

## 16. AIAgentSharp

Source: https://github.com/erwin-beckers/AIAgentSharp

Lightweight C# framework supporting OpenAI / Anthropic / Gemini /
Mistral with function calling. Implements ReAct, Tree-of-Thoughts, and
Chain-of-Thought reasoning strategies on top of a shared
`ConversationHistory` class.

**For Agency.Agentic.** Useful as a *counter-example*: it bakes the
reasoning strategy into the loop. The lesson is the opposite — keep the
loop strategy-agnostic, and let `Context` carry whatever reasoning
metadata a strategy needs.

---

## 17. LLMTornado

Source: https://github.com/lofcz/LlmTornado

.NET agent orchestration library with 30+ provider connectors.
Introduces `Orchestrator` / `Runner` / `Advancer` concepts and explicit
*handoffs* between specialist agents.

**For Agency.Agentic.** Handoff semantics are the closest public example
of the "stateful handoff" pattern called out in the `Class1.cs`
comments. Worth reading the handoff serialization format specifically.

---

## 18. Vercel AI SDK — `generateText` with `stopWhen`

Source: https://github.com/vercel/ai

Cleanest TypeScript example of the messages-as-typed-parts pattern.
`CoreMessage[]` where each message's `content` is `string |
Array<ContentPart>` and `ContentPart` is a discriminated union including
`text`, `tool-call`, `tool-result`. Same loop control as #9.

**For Agency.Agentic.** If C# discriminated unions ever ship (or via
`OneOf<>` today), this is the cleanest type design to mimic.

---

## 19. nibzard — "The Agent is The Loop"

Source: https://www.nibzard.com/theloop

Log-style annotated build of an agent from scratch — useful as a *prose*
companion to #1 / #5. No new techniques but explicit about *why* each
loop decision is made.

---

## 20. Oracle Developers — "What Is the AI Agent Loop?"

Source: https://blogs.oracle.com/developers/what-is-the-ai-agent-loop-the-core-architecture-behind-autonomous-ai-systems

Architecture-diagram-level overview: Prepare Context → Call Model →
Handle Response (text → done; tool calls → execute → append → repeat).
No code, but the diagram is the cleanest one-pager to share with
non-engineers when explaining what the loop is for.

---

## Cross-cutting takeaways for `Agency.Agentic`

Map the patterns above onto the existing skeleton in
`src/Agency.Agentic/Class1.cs`.

### 1. Replace `MessageHistory : List<string>` immediately

Strings can't represent tool calls or tool results faithfully, and every
example above uses a structured message type. Recommended replacement,
modeled on Anthropic's content-block shape (since `Agency.Llm.Claude`
exists and the loop will likely target it first):

```csharp
public abstract record ContentBlock;
public sealed record TextBlock(string Text) : ContentBlock;
public sealed record ToolUseBlock(string Id, string Name, JsonElement Input) : ContentBlock;
public sealed record ToolResultBlock(string ToolUseId, string Content, bool IsError) : ContentBlock;

public sealed record AgentMessage(MessageRole Role, IReadOnlyList<ContentBlock> Content);
public enum MessageRole { User, Assistant, System }
```

This mirrors example #1 directly and can serialize trivially to OpenAI
shape (#15) when needed by exploding `ToolUseBlock` into a `tool_calls`
array on the assistant message and `ToolResultBlock` into a separate
`ToolChatMessage`.

### 2. Make `Context` the canonical state, not `messages[]`

Per the smolagents pattern (#10) and the steps-as-history axis: keep
`QueryContext`, `KnowledgeContext`, `MemoryContext`, `ToolContext`,
`UserSpecificContext`, `TemporalContext`, `EnvironmentalContext` as the
*source of truth*. Add a pure function:

```csharp
internal static class MessageBuilder
{
    public static IReadOnlyList<AgentMessage> Build(Context ctx) { ... }
}
```

called once per iteration. This decouples context layers (which evolve
slowly and may grow new fields) from wire format (which depends on the
backend `ILlmClient`).

Specifically, the existing layers should map roughly as:

| Context layer            | Where it lands in `messages[]`                           |
|--------------------------|----------------------------------------------------------|
| `QueryContext.Prompt`    | First `UserMessage`                                      |
| `KnowledgeContext`       | Re-injected into the system prompt **every iteration** (per #7's CLAUDE.md pattern — *not* into history) |
| `MemoryContext.ShortTerm`| Appended as recent user/assistant turns or summary block |
| `MemoryContext.LongTerm` | Summarized into the system prompt                        |
| `ToolContext`            | Tool definitions passed alongside `messages`, not inside |
| `Temporal/Environmental` | System-prompt preamble                                   |
| Tool call/result history | `AgentMessage` entries with `ToolUseBlock` / `ToolResultBlock` |

### 3. Borrow `stopWhen` from Vercel AI SDK (#9)

```csharp
public delegate bool StopCondition(Context ctx, AgentMessage lastResponse);

public static class StopConditions
{
    public static StopCondition StepCountIs(int n) => (ctx, _) => ctx.IterationCount >= n;
    public static StopCondition NoToolCalls = (_, msg) => msg.Content.OfType<ToolUseBlock>().None();
    public static StopCondition BudgetExceeded(decimal usd) => (ctx, _) => ctx.TotalCostUsd >= usd;
}
```

Default to `[NoToolCalls, StepCountIs(20)]`. Matches the Anthropic SDK
defaults (#7) without locking the API into one provider.

### 4. Extend `ILlmClient` (eventually) — but not yet

Today `Agency.Llm.Abstractions/ILlmClient.cs` exposes `SendAsync` and
`StreamAsync` for plain text. The loop needs:

```csharp
Task<AgentMessage> SendAgentAsync(
    IReadOnlyList<AgentMessage> messages,
    IReadOnlyList<ToolDefinition> tools,
    CancellationToken ct);
```

so the assistant response can carry `ToolUseBlock`s through to the
caller. **Not in scope for the doc-only task** — but flag this as the
first plumbing change before writing the loop body.

### 5. Honor the `Class1.cs` header comment

The comment at the top of `Class1.cs` calls out two non-standard goals:
**persistent goals as a structured backlog**, and **stateful handoffs
across sessions**. None of the canonical loops (#1–#9) handle this
natively; the closest references are:

- **#8 Temporal cookbook** — durable workflow state survives crashes
- **#10 smolagents** — `memory.steps` is the right substrate to also
  carry `BacklogStep` / `HandoffNote` types
- **#17 LLMTornado** — explicit handoff serialization between agents

Combine: add `BacklogContext` as a sibling of `MemoryContext`, modeled
on smolagents' `PlanningStep`, and serialize it to disk on session exit
(à la Temporal's workflow state) so the next session can rehydrate.

### 6. What to read in what order

If you only read three of these before writing the loop:

1. **#1** Anthropic computer-use `loop.py` — the canonical shape
2. **#15** MS Foundry .NET executor — the same shape in C#
3. **#10** smolagents — why steps-as-history beats messages-as-history
   for a layered `Context`

Then **#7** Anthropic SDK docs for the message-type taxonomy and
auto-compact pattern, and **#9** Vercel AI SDK for `stopWhen`.
