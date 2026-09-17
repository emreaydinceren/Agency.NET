# Agency.Acp — Project Plan (Atomic Task Decomposition)

Decomposition of **`docs/specs/Agency.Acp-Specifications.md`** into atomic, context-free tasks.
Every task is self-contained: a sub-agent with no project history should be able to execute it from
the task text plus the files it names.

## How to use this document

1. Work **one Task at a time, top to bottom**. A Task numbered *N.t* (Test) must go **red** before
   its paired *N.i* (Implementation) is written. Red for the right reason — a compile error because
   the type does not exist yet counts; a typo does not.
2. Each Task cites the **Spec** section that defines its requirement. When Spec and plan disagree,
   **the Spec wins** — raise the conflict, do not guess (CLAUDE.md §1).
3. **Surgical changes only** (CLAUDE.md §3): touch only the files named in *Deliverable*. Do not
   improve adjacent code. If you notice unrelated dead code, mention it; do not delete it.
4. **D0–D5 are harness changes** and may proceed in any order. **D6–D10 depend on D1–D4.** D11 is a
   separate repository. D12 is the milestone that proves the product.

### Repo-wide conventions (read once, applies to every task)

- **Solution:** `src/Agency.slnx`. **Harness:** `src/Harness/Agency.Harness/`. **Harness unit
  tests:** `src/Harness/Agency.Harness.Test/`. **Console host:**
  `src/Harness/Agency.Harness.Console/`. **LLM providers:** `src/Llm/Agency.Llm.OpenAI/`,
  `src/Llm/Agency.Llm.Claude/`; shared contracts in `src/Llm/Agency.Llm.Common/`.
- **Build:** `dotnet build src/Agency.slnx -c Release`. **Unit tests:**
  `dotnet test src/Agency.slnx -c Release --filter "Category!=Functional"`. **Functional:**
  `--filter "Category=Functional"` — needs a local LLM endpoint; append
  `-- RunConfiguration.MaxCpuCount=1`, because concurrent test assemblies crash the GPU.
- **Compiler:** `TreatWarningsAsErrors=true`, `Nullable=enable`,
  `AnalysisLevel=latest-Recommended` (`src/Directory.Build.props`). A warning fails the build.
- **Packages:** centrally pinned in `src/Directory.Packages.props`. Add the version there; reference
  by name with **no version attribute** in the `.csproj`.
- **Test stack:** `xunit.v3` 3.2.2, `Moq` 4.20.72, `Microsoft.NET.Test.Sdk` 18.6.0. Pass
  `TestContext.Current.CancellationToken` to async calls under test — the established pattern here.
- **Traits:** functional tests carry `[Trait("Category", "Functional")]`; those needing a live model
  add `[Trait("Category", "RequiresLlm")]`. See `src/Harness/Agency.Harness.Test/Functional/`.
- **Internals visibility:** `src/Harness/Agency.Harness/AssemblyInfo.cs` already declares
  `InternalsVisibleTo` for `Agency.Harness.Test`, `Agency.Harness.Console`,
  `Agency.Harness.Console.Test`. **An internal type a unit test must reach uses `internal` plus that
  existing entry.** A new consumer assembly needs a **new** line added there.
- **Public API tracking:** `Microsoft.CodeAnalysis.PublicApiAnalyzers` is a `GlobalPackageReference`
  on every non-test project. **Any new or changed public member must be added to that project's
  `PublicAPI.Unshipped.txt` or the build fails.** One entry per line, sorted.
- **Style:** file-scoped namespaces; XML doc comments on all public members; `crlf`; 4-space indent
  (`.editorconfig`). See `Agents/CSharpPrinciples.md`.
- **Do not commit** unless explicitly asked (CLAUDE.md).

### Terminology

| Term | Meaning |
|---|---|
| **Turn** | One `session/prompt` request and its single terminal response |
| **Iteration** | One LLM call inside a Turn; a Turn may contain many |
| **Park** | A Turn ending in `AwaitingPermission`, awaiting `ResumeWithPermissionsAsync` |
| **Lock** | One of the five independently-asserted safety properties (Spec §6.5) |
| **Delta** | A change inside `Agency.Harness` required by the adapter (Spec §6.9) |

---

# Deliverable D0 — MCP transport: authentication and diagnosis

Spec **§6.5**, **§6.9 (D-1, D-2)**, **§12 (E-5)**.

Without D0 the adapter cannot authenticate to the client's MCP tool server — the single item that
blocked the whole integration.

### Task 0.1.t — Test: `Headers` reach the MCP transport (red)

- **Goal:** Pin the header-passthrough requirement from **Spec §6.9 (D-1)** before relying on it.
- **Read first:** `src/Harness/Agency.Harness/Tools/McpClientOptions.cs`,
  `src/Harness/Agency.Harness/Tools/McpClientPool.cs` (`CreateTransport`), **Spec §6.5**, **§6.9**.
- **Deliverable:** Add `src/Harness/Agency.Harness.Test/McpServerConfigHeadersTests.cs` (test-project
  **root**, matching the existing `McpClientPoolResilienceTests.cs`; there is no `Tools/` folder).

  ⚠️ **`HttpClientTransport` exposes only `Name`** — the options it was constructed with cannot be
  read back, so asserting on a constructed transport is impossible. Extract the options construction
  into pure functions first and assert on those:
  `internal static HttpClientTransportOptions BuildHttpTransportOptions(McpServerConfig)` and
  `internal static StdioClientTransportOptions BuildStdioTransportOptions(McpServerConfig)`, both
  reached via the existing `InternalsVisibleTo("Agency.Harness.Test")`. `CreateTransport` then just
  selects between them.

  Assert: (a) `Headers = new() { ["Authorization"] = "Bearer abc" }` is forwarded verbatim to
  `AdditionalHeaders`, alongside correct `Name` and `Endpoint`; (b) `Headers = null` leaves
  `AdditionalHeaders` **null** — absence must not become an empty dictionary; (c) a missing `Url`
  throws `InvalidOperationException` naming the server; (d) the `Stdio` builder ignores `Headers`
  entirely and carries `EnvironmentVariables` instead.
- **Acceptance:** `dotnet test --filter "FullyQualifiedName~McpServerConfigTests"` is red.

### Task 0.1.i — Wire `Headers` through to `AdditionalHeaders`

- **Goal:** Deliver **Spec §6.9 (D-1)**.
- **Read first:** the test from 0.1.t, `McpClientPool.cs`,
  `src/Harness/Agency.Harness/PublicAPI.Unshipped.txt`.
- **Deliverable:** **Largely landed — verify rather than rewrite.** Confirm
  `public Dictionary<string, string>? Headers { get; set; }` exists on `McpServerConfig` with an XML
  doc comment; confirm the `McpTransportKind.Http` branch sets `AdditionalHeaders = server.Headers`;
  confirm both `Headers.get`/`Headers.set` lines are in `PublicAPI.Unshipped.txt`. Change
  `CreateTransport` to `internal static`.
- **Acceptance:** 0.1.t green. `dotnet build src/Agency.slnx -c Release` warning-free.

### Task 0.2.t — Test: an auth failure is diagnosable (red)

- **Goal:** Capture **Spec §12 (E-5)** — a `401` surfaces as `404` because the SDK's `AutoDetect`
  falls back to SSE and reports the `GET` failure instead.
- **Read first:** `McpClientPool.cs` (the per-server `catch` filling `FailedServers`),
  **Spec §6.9 (D-2)**, **§12 (E-5)**.
- **Deliverable:** Add `src/Harness/Agency.Harness.Test/Tools/McpAutoDetectDiagnosticsTests.cs`.
  Stand up an in-process `HttpListener` returning `401` to `POST /mcp` and `404` to `GET /mcp`. Call
  `McpClientPool.CreateAsync` against it with no `Headers`. Assert `FailedServers["x"]`
  **contains `401`**. If the status is surfaced by logging rather than the message, capture it with a
  test `ILoggerProvider`.
- **Acceptance:** Compiles and fails — today the message names `404`. Red.

### Task 0.2.i — Log the first-attempt status on `AutoDetect` fallback

- **Goal:** Implement **Spec §6.9 (D-2)**.
- **Read first:** the test from 0.2.t, `McpClientPool.cs`.
- **Deliverable:** Add `internal sealed class McpResponseLoggingHandler : DelegatingHandler` in
  `src/Harness/Agency.Harness/Tools/`, recording the **first** response status per instance and
  exposing `internal HttpStatusCode? FirstStatus { get; }`. Use the
  `HttpClientTransport(HttpClientTransportOptions, HttpClient, ILoggerFactory, bool)` overload so the
  handler can be installed; give `CreateTransport` an optional `ILoggerFactory?`. On a per-server
  failure, append the first status to the `FailedServers` entry, e.g.
  `"{ex.Message} (first attempt: 401 Unauthorized)"`. Do **not** change `FailedServers`' type.
- **Acceptance:** 0.2.t green; existing `McpClientPool` tests green; build warning-free.

---

# Deliverable D1 — Turn correctness: cancellation, timeout, truncation

Spec **§6.9 (D-4, D-5, D-6)**, **§8.3**, **§12 (E-1, E-2)**.

**Spec §12 rates E-1 high severity**: a cancel landing mid-batch corrupts the *next* Turn, so the
damage surfaces away from its cause. D1 is a prerequisite for D8.

### Task 1.1.t — Test: a cancelled Turn leaves a valid transcript (red)

- **Goal:** Pin **Spec §12 (E-1)** and **§6.9 (D-4)**.
- **Read first:** `src/Harness/Agency.Harness/Agent.cs` — the assistant-message append (search
  `ctx.Conversation.Append(lastAssistant)`) and the tool-result appends after `Task.WhenAll`;
  `src/Harness/Agency.Harness.Test/Fakes/FakeLlmClient.cs`; **Spec §6.9**, **§12 (E-1)**.
- **Deliverable:** Add `src/Harness/Agency.Harness.Test/CancellationRepairTests.cs`. Script
  `FakeLlmClient` to return an assistant message with one `FunctionCallContent`; register a tool
  whose `InvokeAsync` blocks until the test cancels. Drive `ChatSession.SendAsync`, cancel mid-tool,
  catch `OperationCanceledException`, then read the conversation via the internal `PreviewContext()`.
  Assert **every `FunctionCallContent` has a matching `FunctionResultContent` with the same
  `CallId`**. Add a second test asserting the next `SendAsync` after a cancel completes normally.
- **Acceptance:** Both compile and fail — the transcript holds an unanswered `tool_use`. Red.

### Task 1.1.i — Repair the transcript on cancellation

- **Goal:** Implement **Spec §6.9 (D-4)**.
- **Read first:** the tests from 1.1.t, `Agent.cs` (the `try/catch/finally` in `ChatIteratorAsync`,
  currently telemetry-only).
- **Deliverable:** In the cancellation path, before the exception is rethrown, append a synthetic
  `FunctionResultContent` for every dispatched `FunctionCallContent` lacking a result — content
  `"[Cancelled] The user cancelled this turn before the tool returned."`, matching `CallId`. Do it in
  a `finally` so it covers failure as well as cancellation. Do **not** emit an `AgentResultEvent`;
  per **Spec §8.3** the exception remains the signal. Preserve
  `ExceptionDispatchInfo.Capture(...).Throw()`.
- **Acceptance:** 1.1.t green; full unit suite green; build warning-free.

### Task 1.2.t — Test: a turn timeout is distinguishable from a user cancel (red)

- **Goal:** Pin **Spec §12 (E-2)** and **§6.9 (D-5)**.
- **Read first:** `Agent.cs` — the linked CTS and `turnCts.CancelAfter(...)` driven by
  `AgentOptions.TurnTimeoutSeconds`; **Spec §6.9**, **§12 (E-2)**.
- **Deliverable:** Add `src/Harness/Agency.Harness.Test/TurnTimeoutTests.cs`. Use
  `Microsoft.Extensions.TimeProvider.Testing` (already pinned) with a `FakeTimeProvider` to advance
  past `TurnTimeoutSeconds` while the fake client hangs. Assert the exception is **`TimeoutException`**
  (or an `OperationCanceledException` carrying a distinguishing marker — choose one and state it in
  the test's XML comment). A second test cancels the caller's token and asserts the ordinary
  `OperationCanceledException`, proving separability.
- **Acceptance:** Compiles and fails — today both paths throw the same type. Red.

### Task 1.2.i — Separate the timeout CTS from the cancel CTS

- **Goal:** Implement **Spec §6.9 (D-5)**.
- **Read first:** the tests from 1.2.t, `Agent.cs`.
- **Deliverable:** In `ChatIteratorAsync`, hold the caller-linked token and a separate timeout CTS.
  On unwind, inspect which fired: timeout fired and caller's did not ⇒ wrap in `TimeoutException`;
  otherwise rethrow unchanged. Keep the `agent.turn.timeout_seconds` telemetry tag.
- **Acceptance:** 1.2.t green; 1.1.t still green; full unit suite green.

### Task 1.3.t — Test: truncation is not an error (red)

- **Goal:** Pin **Spec §8.3** — `FinishReason == Length` maps to ACP `max_tokens` and cannot share a
  status with genuine failures.
- **Read first:** `src/Harness/Agency.Harness/AgentEvents.cs` (`AgentResultStatus`), `Agent.cs` (the
  length-finish branch), **Spec §8.3**, **§6.9 (D-6)**.
- **Deliverable:** Add `src/Harness/Agency.Harness.Test/AgentResultStatusTests.cs`. Script
  `FakeLlmClient` to return `ChatFinishReason.Length`; assert the terminal `AgentResultEvent.Status`
  is a **new** `AgentResultStatus.Truncated`, not `Error`. Second test: a genuinely degenerate
  response still yields `Error`.
- **Acceptance:** Compiles and fails — `Truncated` does not exist. Red.

### Task 1.3.i — Add `AgentResultStatus.Truncated`

- **Goal:** Implement **Spec §6.9 (D-6)**.
- **Read first:** the tests from 1.3.t, `AgentEvents.cs`, `PublicAPI.Unshipped.txt`.
- **Deliverable:** Append `Truncated` to the **end** of `AgentResultStatus` — appending preserves
  existing numeric values that reordering would silently change. Add an XML doc comment. Emit it from
  the length-finish branch. Add
  `Agency.Harness.AgentResultStatus.Truncated = 5 -> Agency.Harness.AgentResultStatus` to
  `PublicAPI.Unshipped.txt`. Find exhaustive `switch`es over the enum
  (`Agency.Harness.Console/ConsoleChatSession.cs`) and handle the new member.
- **Acceptance:** 1.3.t green; Console renders the status; full unit suite green.

---

# Deliverable D2 — Streaming

Spec **§6.9 (D-7, D-8)**, **§6.8**, **§11.1**. **The long pole.** Objective **O-1** depends on it.

### Task 2.1.t — Test: streaming yields deltas and an equivalent aggregate (red)

- **Goal:** Pin **Spec §6.9 (D-7)** — deltas precede the terminal event and the aggregate equals the
  non-streamed response.
- **Read first:** `Agent.cs` (the `GetResponseAsync` call site),
  `src/Harness/Agency.Harness.Test/Fakes/FakeLlmClient.cs` — **its `GetStreamingResponseAsync`
  currently throws `NotSupportedException`; this task implements it** — **Spec §6.9**, **§6.8**.
- **Deliverable:** Extend `FakeLlmClient` with a scripted `GetStreamingResponseAsync` returning
  `ChatResponseUpdate`s. Add `src/Harness/Agency.Harness.Test/StreamingTests.cs` asserting:
  (a) at least one `AssistantTextDeltaEvent` precedes the terminal `AgentResultEvent`;
  (b) concatenated delta text equals the `AssistantTurnEvent` message text;
  (c) `ctx.TotalUsage.TotalTokens > 0` after a streamed turn, since usage arrives only on the final
  update; (d) a streamed response containing a `FunctionCallContent` still drives the tool loop —
  same tool, same arguments — proving §6.9's "reassembled by `ToChatResponseAsync()`" claim.
- **Acceptance:** Compiles and fails — `AssistantTextDeltaEvent` does not exist. Red.

### Task 2.1.i — Switch the agent loop to streaming

- **Goal:** Implement **Spec §6.9 (D-7)**.
- **Read first:** the tests from 2.1.t, `Agent.cs`, `AgentEvents.cs`, `PublicAPI.Unshipped.txt`.
- **Deliverable:** Add `public sealed record AssistantTextDeltaEvent(string Text) : AgentEvent` with
  an XML doc comment and its `PublicAPI.Unshipped.txt` entry. In `Agent.cs`, replace
  `await this._llm.GetResponseAsync(requestMessages, options, ct)` with an `await foreach` over
  `GetStreamingResponseAsync`, collecting into `List<ChatResponseUpdate>` and yielding an
  `AssistantTextDeltaEvent` per non-empty `TextContent`. After the loop call
  `updates.ToChatResponseAsync()` to produce the `ChatResponse` the rest of the method already
  consumes — **do not restructure anything downstream**. Preserve both retry loops (empty-choices,
  degenerate-response) around the streaming call.
- **Acceptance:** 2.1.t green. **The entire existing harness suite must stay green** — this is the
  regression gate for the highest-risk change in the plan.

### Task 2.2.t — Test: reasoning content is typed separately (red)

- **Goal:** Pin **Spec §6.8** — reasoning maps to `agent_thought_chunk`, never mixed into text.
- **Read first:** the implementation from 2.1.i, **Spec §6.8**, **§6.9**.
- **Deliverable:** In `StreamingTests.cs`, script updates interleaving `TextReasoningContent` with
  `TextContent`. Assert reasoning yields `AssistantThoughtDeltaEvent` and **never** appears in
  `AssistantTextDeltaEvent.Text`.
- **Acceptance:** Compiles and fails. Red.

### Task 2.2.i — Emit `AssistantThoughtDeltaEvent`

- **Goal:** Implement the reasoning half of **Spec §6.8**.
- **Read first:** the test from 2.2.t, `Agent.cs`, `AgentEvents.cs`.
- **Deliverable:** Add `public sealed record AssistantThoughtDeltaEvent(string Text) : AgentEvent`
  plus its `PublicAPI.Unshipped.txt` entry. Branch on content type in the streaming loop:
  `TextContent` → text delta; `TextReasoningContent` → thought delta.
- **Acceptance:** 2.2.t green; 2.1.t still green.

### Task 2.3.t — Test: tool calls correlate by id start to finish (red)

- **Goal:** Pin **Spec §6.9 (D-8)** — ACP correlates `tool_call` with `tool_call_update` by id, and
  no id is exposed today.
- **Read first:** `AgentEvents.cs` (`ToolInvokedEvent`), `Agent.cs` (parallel dispatch and
  `Task.WhenAll`), **Spec §6.9 (D-8)**.
- **Deliverable:** Add `src/Harness/Agency.Harness.Test/ToolEventCorrelationTests.cs`. Script two
  tool calls in one assistant message. Assert: (a) a `ToolStartedEvent` for each call precedes
  **either** `ToolInvokedEvent`; (b) each `ToolStartedEvent.CallId` matches exactly one
  `ToolInvokedEvent.CallId`; (c) the ids equal the provider's `FunctionCallContent.CallId`, not
  freshly minted GUIDs.
- **Acceptance:** Compiles and fails. Red.

### Task 2.3.i — Add `ToolStartedEvent` and `CallId`

- **Goal:** Implement **Spec §6.9 (D-8)** without breaking the public API.
- **Read first:** the test from 2.3.t, `AgentEvents.cs`, `Agent.cs`, `PublicAPI.Unshipped.txt`.
- **Deliverable:** Add
  `public sealed record ToolStartedEvent(string CallId, string ToolName, JsonElement Input) : AgentEvent`.
  Add `CallId` to `ToolInvokedEvent` **as an appended member with a default** — it is public and
  shipped, so a new positional parameter is breaking; append `string CallId = ""` last, or add an
  `init`-only property. State the choice in the XML doc. Yield a `ToolStartedEvent` per call
  immediately before the parallel dispatch begins. Update `PublicAPI.Unshipped.txt`.
- **Acceptance:** 2.3.t green; existing tests constructing `ToolInvokedEvent` still compile
  **unchanged** — if any break, the member was not appended correctly.

---

# Deliverable D3 — Persona identity seam

Spec **§6.9 (D-3)**, **§4 (P1)**. `QueryContext.IdentityPrompt` does **not** exist on `main`; it
must be re-landed.

### Task 3.1.t — Test: the identity line is overridable and present every iteration (red)

- **Goal:** Pin **Spec §6.9 (D-3)** — the Persona's identity replaces the default opening line on
  **every** iteration, not only the first.
- **Read first:** `src/Harness/Agency.Harness/Contexts/QueryContext.cs` (today: `Prompt`,
  `InstructionsBlock`), `src/Harness/Agency.Harness/SystemPromptBuilder.cs` (the hardcoded
  `"You are an autonomous agent operating inside the Agency runtime."`), `Agent.cs` (the per-iteration
  `SystemPromptBuilder.Build(ctx)` call), **Spec §6.9 (D-3)**, **§9**.
- **Deliverable:** Add `src/Harness/Agency.Harness.Test/SystemPromptIdentityTests.cs`. Assert:
  (a) with `IdentityPrompt = null` the default line is emitted verbatim; (b) with it set, the custom
  text replaces that line and the default does not appear; (c) the ReAct instruction and grounding
  sections remain in both cases — **Spec §I1 forbids a Replace mode**; (d) a two-iteration run has
  the identity present in both submitted prompts (capture via `Context.LastLlmRequest`).
- **Acceptance:** Compiles and fails — `IdentityPrompt` does not exist. Red.

### Task 3.1.i — Add `QueryContext.IdentityPrompt`

- **Goal:** Implement **Spec §6.9 (D-3)**.
- **Read first:** the tests from 3.1.t, `QueryContext.cs`, `SystemPromptBuilder.cs`,
  `PublicAPI.Unshipped.txt`.
- **Deliverable:** Add `public string? IdentityPrompt { get; init; }` to `QueryContext` with an XML
  doc comment stating it replaces **only** the opening identity line. In `SystemPromptBuilder.Build`,
  replace the hardcoded literal with
  `ctx.Query.IdentityPrompt ?? "You are an autonomous agent operating inside the Agency runtime."`.
  Change nothing else in the builder. Update `PublicAPI.Unshipped.txt`.
- **Acceptance:** 3.1.t green; existing `SystemPromptBuilder` tests green.

---

# Deliverable D4 — Provider metadata and per-session effort

Spec **§6.7**, **§4 (P2, P3)**, **§16 (G-2, G-4)**.

**P2 is the governing constraint:** no feature may be load-bearing on a vendor-specific endpoint.

### Task 4.1.t — Test: `Model` carries optional metadata and tolerates absence (red)

- **Goal:** Pin **Spec §6.7** — optional fields, null where the server cannot answer.
- **Read first:** `src/Llm/Agency.Llm.Common/Model.cs` (today
  `public sealed record Model(string Id, string Name)`), `src/Llm/Agency.Llm.Common/IModelProvider.cs`,
  **Spec §6.7**, **§4 (P2, P3)**.
- **Deliverable:** Add `src/Llm/Agency.Llm.Common.Test/ModelTests.cs` (create the test project if
  absent, mirroring `src/Llm/Agency.Llm.Test/`). Assert: (a) `new Model("id", "name")` still compiles
  — the two-arg shape must stay source-compatible; (b) `Kind`, `ContextLength`, `IsLoaded` default to
  `null`; (c) `ModelKind` has at least `Chat`, `Embedding`, `Unknown`.
- **Acceptance:** Compiles and fails — the members do not exist. Red.

### Task 4.1.i — Extend `Model` with optional metadata

- **Goal:** Implement **Spec §6.7**'s metadata table.
- **Read first:** the test from 4.1.t, `Model.cs`,
  `src/Llm/Agency.Llm.Common/PublicAPI.Unshipped.txt`.
- **Deliverable:** Add `public enum ModelKind { Unknown, Chat, Embedding, Vision }` and extend
  `Model` with `init`-only `ModelKind? Kind`, `int? ContextLength`, `bool? IsLoaded` — **as optional
  members, not positional parameters**, so `new Model(id, name)` keeps compiling. XML docs must state
  that `null` means *unknown*, never *false* (**P3**). Update `PublicAPI.Unshipped.txt`.
- **Acceptance:** 4.1.t green; every existing `Model` construction compiles unchanged.

### Task 4.2.t — Test: a richer catalogue enriches, a plain one degrades (red)

- **Goal:** Pin **Spec §6.7** — enrichment where the server answers, plain everywhere else.
- **Read first:** `src/Llm/Agency.Llm.OpenAI/OpenAIClient.cs` (`GetModelsAsync`), **Spec §6.7**,
  **§4 (P2)**.
- **Deliverable:** Add `src/Llm/Agency.Llm.Test/OpenAIModelCatalogueTests.cs`. Against an in-process
  `HttpListener`: (a) a server answering only `/v1/models` yields models with `Kind`,
  `ContextLength`, `IsLoaded` **all null**; (b) a server also answering a richer catalogue endpoint
  yields them populated; (c) if the richer endpoint returns `404` or malformed JSON, the result is
  case (a) and **no exception escapes** — enrichment must never fail a catalogue fetch.
- **Acceptance:** Compiles and fails. Red.

### Task 4.2.i — Populate metadata where the server supports it

- **Goal:** Implement **Spec §6.7** enrichment at the provider edge.
- **Read first:** the tests from 4.2.t, `OpenAIClient.cs`.
- **Deliverable:** In `OpenAIClient.GetModelsAsync`, after the standard `/v1/models` call, attempt a
  richer catalogue fetch **inside a `try/catch` that swallows everything** and merges by `Id`. Keep
  the vendor path name in one `private const string` with a comment naming the server family it
  targets — **P2 requires it to be an optional enrichment, never on the required path**. Do not
  change `IModelProvider`.
- **Acceptance:** 4.2.t green; existing OpenAI tests green.

### Task 4.3.t — Test: two sessions differ in effort; a change preserves history (red)

- **Goal:** Pin **Spec §6.7 (Effort)** and **Spec §16 (G-4)** — per-client effort swapped via
  `SetAgent`.
- **Read first:** `src/Llm/Agency.Llm.Common/LlmClientOptions.cs`,
  `src/Llm/Agency.Llm.OpenAI/SuppressThinkingPipelinePolicy.cs` (the existing injection point),
  `src/Harness/Agency.Harness/ChatSession.cs` (`SetAgent`), **Spec §6.7**, **§16 (G-4)**.
- **Deliverable:** Add `src/Llm/Agency.Llm.Test/ThinkingOptionsTests.cs`: two `LlmClientOptions`
  built with `with { }` produce request bodies carrying **different** thinking settings. Add
  `src/Harness/Agency.Harness.Test/SetAgentPreservesHistoryTests.cs`: send one turn, `SetAgent` a
  differently-configured agent, send a second turn, assert the second request still carries the first
  turn's messages.
- **Acceptance:** Both compile and fail. Red.

### Task 4.3.i — Add effort fields to `LlmClientOptions`

- **Goal:** Implement **Spec §6.7 (Effort)**.
- **Read first:** the tests from 4.3.t, `LlmClientOptions.cs`, `SuppressThinkingPipelinePolicy.cs`,
  `src/Llm/Agency.Llm.Claude/ClaudeClient.cs`.
- **Deliverable:** Add `bool? EnableThinking` and `int? ThinkingBudgetTokens` to `LlmClientOptions`
  with XML docs. Generalise `SuppressThinkingPipelinePolicy` into a policy that writes
  `enable_thinking` from `EnableThinking` — keeping `SuppressThinking = true` behaving **exactly** as
  today for back-compat. In `ClaudeClient`, map `ThinkingBudgetTokens` onto the Anthropic
  `thinking: { type, budget_tokens }` shape. **Spec §6.7 records that `reasoning_effort` is rejected
  as the mechanism** — do not emit it. Update `PublicAPI.Unshipped.txt`.
- **Acceptance:** 4.3.t green; existing `SuppressThinking` tests green **unchanged** — back-compat is
  the gate here.

---

# Deliverable D5 — Memory configuration defects

Spec **§14**. Independent of ACP; these are latent bugs for any user running memory.

### Task 5.1.t — Test: memory options bind from configuration (red)

- **Goal:** Close the defect recorded in the agreement as **O9a**.
- **Read first:**
  `src/Memory/Agency.Memory.Distiller/DependencyInjection/MemoryServiceCollectionExtensions.cs`
  (lines registering `AddOptions<MemoryOptions>()` and `AddOptions<DistillerOptions>()` with **no**
  `.Bind(...)`), `src/Memory/Agency.Memory.Common.Test/OptionsTests.cs` (a passing test proving the
  options *can* bind), `src/Memory/Agency.Memory.Hygiene/DependencyInjection/HygieneServiceCollectionExtensions.cs`.
- **Deliverable:** Add a test asserting that after `AddAgencyMemory(...)` with an `IConfiguration`
  containing `Memory:RetrievalTopK` and `Distiller:InactivityTimeout`, the resolved
  `IOptions<MemoryOptions>.Value.RetrievalTopK` and `IOptions<DistillerOptions>.Value` reflect the
  configured values.
- **Acceptance:** Compiles and fails — nothing binds today. Red.

### Task 5.1.i — Bind `MemoryOptions` and `DistillerOptions`

- **Goal:** Fix O9a.
- **Read first:** the test from 5.1.t, both DI extension files.
- **Deliverable:** Add `.Bind(config.GetSection("Memory"))` / `.Bind(config.GetSection("Distiller"))`
  to the three `AddOptions<T>()` call sites, threading `IConfiguration` through the extension
  signatures if it is not already available. Preserve the existing `Action<T>?` overloads so callers
  configuring in code keep working; configuration binds first, the action applies after.
- **Acceptance:** 5.1.t green; existing memory DI tests green.

### Task 5.2.t — Test: a Claude-typed client constructs a Claude client (red)

- **Goal:** Close the defect recorded as **O9b**.
- **Read first:** `src/Harness/Agency.Harness.Console/Program.cs` — the two
  `new OpenAIClient(...)` constructions for the consolidator and distiller, which ignore
  `ClientType`; `src/Harness/Agency.Harness/Models.cs` (the correct dispatch:
  `"CLAUDE" => new ClaudeClient(options)`, `"OPENAI" => new OpenAIClient(options)`).
- **Deliverable:** Add a test in `src/Harness/Agency.Harness.Console.Test/` asserting that with
  `Agent:LLmClients[0].ClientType = "Claude"`, the chat client built for the distiller is **not** an
  OpenAI client. Assert on the concrete type or on the outbound request shape.
- **Acceptance:** Compiles and fails. Red.

### Task 5.2.i — Route memory clients through `Models.CreateChatClient`

- **Goal:** Fix O9b using machinery that already exists.
- **Read first:** the test from 5.2.t, `Program.cs`, `Models.cs`.
- **Deliverable:** Replace both `new OpenAIClient(...)` constructions with
  `Models.CreateChatClient(clientName)`. Keep the `defaultClientOpts with { SuppressThinking = true }`
  behaviour for the distiller by resolving through a named client configured that way, or by passing
  the modified options into `Models`. Do not duplicate the provider `switch`.
- **Acceptance:** 5.2.t green; Console starts with memory enabled against both provider types.

---

# Deliverable D6 — `Agency.Acp` scaffold, transport and dispatcher

Spec **§6.1**, **§6.2**, **§16 (G-3)**. **Depends on nothing; may start in parallel with D0–D5.**

### Task 6.0 — Scaffold the project

- **Goal:** Create the single executable decided in **Spec §16 (G-3)**.
- **Read first:** `src/Harness/Agency.Harness.Console/Agency.Harness.Console.csproj` (the precedent —
  an `Exe` that packs normally), `src/Directory.Packages.props`, `src/Agency.slnx`,
  `src/Directory.Build.targets` (the `PackageId = AgencyDotNet$(…)` rule), **Spec §16 (G-3)**.
- **Deliverable:** Create `src/Acp/Agency.Acp/Agency.Acp.csproj` (`OutputType=Exe`) and
  `src/Acp/Agency.Acp.Test/Agency.Acp.Test.csproj`. Add both to `src/Agency.slnx` under a new
  `/Acp/` folder. Pin `dotacp.protocol` **2026.7.19** in `Directory.Packages.props` and reference it
  by name. Add `[assembly: InternalsVisibleTo("Agency.Acp.Test")]` in
  `src/Acp/Agency.Acp/AssemblyInfo.cs`. Reference `Agency.Harness`, `Agency.Llm.Common`,
  `Agency.Configuration`. **Verify the package smoke gate:** `dotnet pack` must emit
  `lib/net10.0/Agency.Acp.dll`; if it does not, set `IsPackable=false` and record why.
- **Acceptance:** `dotnet build src/Agency.slnx -c Release` warning-free; `dotnet test` discovers the
  empty test project.

### Task 6.1.t — Test: framing round-trips and writes never interleave (red)

- **Goal:** Pin **Spec §6.1** — one reader loop, one serialized writer.
- **Read first:** **Spec §6.1**, `dotacp.protocol`'s `AgentMethods`/`ClientMethods` constants.
- **Deliverable:** Add `src/Acp/Agency.Acp.Test/Transport/StdioTransportTests.cs`. Against in-memory
  streams: (a) a written notification is a **single line** terminated by `\n`, parseable as JSON;
  (b) 100 notifications emitted **concurrently** from 10 tasks produce 100 well-formed lines with no
  interleaving — parse every line and assert none fails; (c) a request read from stdin is surfaced
  with its `id` and `method` intact; (d) **the reader does not await the handler** — dispatch a
  handler that blocks, then assert a second message is still read.
- **Acceptance:** Compiles and fails — `StdioTransport` does not exist. Red.

### Task 6.1.i — Implement `StdioTransport`

- **Goal:** Implement **Spec §6.1**.
- **Read first:** the tests from 6.1.t, **Spec §6.1 (Implementation notes)**.
- **Deliverable:** Add `internal sealed class StdioTransport` in `src/Acp/Agency.Acp/Transport/`.
  Constructor takes `Stream input, Stream output`. A single reader loop deserializes
  newline-delimited JSON-RPC and invokes a `Func<JsonRpcMessage, Task>` **without awaiting it**.
  All writes go through one `Channel<string>` drained by a single writer task. Use
  `dotacp.protocol`'s own `JsonSerializerOptions` — **Spec §6.1 warns that defaults produce the wrong
  wire form** for union discrimination and enum spellings.
- **Acceptance:** 6.1.t green.

### Task 6.2.t — Test: stdout carries protocol bytes only (red)

- **Goal:** Pin **Spec §12 (E-4)**, rated high severity — a stray write corrupts the stream.
- **Read first:** **Spec §12 (E-4)**, **Spec §6.1**.
- **Deliverable:** Add `src/Acp/Agency.Acp.Test/Transport/StdoutPurityTests.cs`. Boot the host with
  logging configured at `Trace` and a redirected stdout; drive `initialize` plus one failing method.
  Assert **every** line on stdout parses as JSON-RPC. Add a second assertion that the configured
  `ILoggerProvider` set writes to stderr or a file, never `Console.Out`.
- **Acceptance:** Compiles and fails. Red.

### Task 6.2.i — Guarantee stdout purity

- **Goal:** Implement **Spec §12 (E-4)**'s mitigation.
- **Read first:** the test from 6.2.t, `src/Harness/Agency.Harness.Console/Program.cs` (how Serilog is
  configured there).
- **Deliverable:** In the host's composition root, configure every log sink to stderr or a file. Call
  `Console.SetOut(TextWriter.Null)` after capturing the real stdout handle for the transport, so a
  stray `Console.WriteLine` anywhere in the process — including inside a dependency — cannot reach
  the protocol stream.
- **Acceptance:** 6.2.t green.

### Task 6.3.t — Test: `initialize` reports the agreed capabilities; unknown methods are `-32601` (red)

- **Goal:** Pin **Spec §6.2**'s method table and **Spec §3 (Non-Goals)**.
- **Read first:** **Spec §6.2**, **Spec §3**, `dotacp.protocol`'s `AgentMethods`.
- **Deliverable:** Add `src/Acp/Agency.Acp.Test/Dispatch/MethodDispatcherTests.cs`. Assert:
  (a) `initialize` returns `ProtocolVersion` **1**, `AuthMethods` **empty**, `SupportsLoadSession`
  **false**; (b) `session/load`, `session/list`, `session/resume`, `session/set_mode`,
  `authenticate`, `logout` each return `-32601`; (c) an unparseable request yields `-32700`;
  (d) malformed params yield `-32602`.
- **Acceptance:** Compiles and fails. Red.

### Task 6.3.i — Implement `MethodDispatcher`

- **Goal:** Implement **Spec §6.2**.
- **Read first:** the tests from 6.3.t, **Spec §6.2**.
- **Deliverable:** Add `internal sealed class MethodDispatcher` in `src/Acp/Agency.Acp/Dispatch/`,
  routing the `AgentMethods` constants to handler delegates and returning `-32601` for the rest.
  `AgentVersion` comes from the assembly's informational version (NBGV-stamped).
- **Acceptance:** 6.3.t green.

---

# Deliverable D7 — Session lifecycle

Spec **§6.3**, **§7.2**, **§8.1**, **§16 (G-1)**. **Depends on D6 and D4.**

### Task 7.1.t — Test: sessions are independent and dispose cleanly (red)

- **Goal:** Pin **Spec §16 (G-1)** — protocol multi-session with honest identity.
- **Read first:** **Spec §6.3**, **§7.2**, `src/Harness/Agency.Harness/ChatSession.cs` (note the
  constructor takes `AgentOptions` **as a parameter**, which is what makes per-session options
  possible without a harness change).
- **Deliverable:** Add `src/Acp/Agency.Acp.Test/Sessions/SessionRegistryTests.cs`. Assert: (a) two
  `session/new` calls return distinct ids; (b) a prompt in session A does not appear in session B's
  history; (c) each session's `AgentOptions` instance is distinct, so differing `ContextWindowSize`
  values do not bleed; (d) `session/close` disposes the `McpClientPool` and the DI scope — assert via
  a disposal-tracking fake; (e) an unknown session id yields `-32602` naming the id;
  **(f) disposal happens on transport disconnect with no `session/close` ever sent** — close the
  input stream and assert the pool and scope are disposed and `OnSessionEnd` fired exactly once;
  **(g) `session/close` followed by disconnect disposes exactly once**, not twice.
  **(f) is the important one** — **Spec §6.3 (Disposal triggers)** and **§12 (E-15)** record that
  the first client never sends `session/close`, so a suite that only tests (d) would pass while the
  process leaked every session it ever created.
- **Acceptance:** Compiles and fails. Red.

### Task 7.1.i — Implement `SessionRegistry` and `SessionState`

- **Goal:** Implement **Spec §6.3** and **§7.2**.
- **Read first:** the tests from 7.1.t, **Spec §6.3**, **§7.2** (the `SessionState` field table).
- **Deliverable:** Add `internal sealed class SessionState` with exactly the fields in **Spec §7.2**
  and `internal sealed class SessionRegistry` backed by a `ConcurrentDictionary<string, SessionState>`.
  Each session takes an `IServiceScope` from `IServiceScopeFactory`, builds a **per-session**
  `AgentOptions` instance, and owns its `ChatSession` and `McpClientPool`. Disposal is
  `await using` in reverse construction order and must be **idempotent** — it can be reached from
  three independent triggers. Wire all three per **Spec §6.3 (Disposal triggers)**:
  `session/close`, **transport disconnect** (the reader loop reaching EOF disposes every live
  session before the process exits), and process shutdown. Treat `session/close` as an optimisation
  that releases sooner, **never as the mechanism**. Record `cwd` on `SessionState` for diagnostics —
  **Spec §6.3 requires it be documented as unobservable, not silently dropped**.
- **Acceptance:** 7.1.t green.

### Task 7.2.t — Test: `session/new` degrades rather than fails (red)

- **Goal:** Pin **Spec §8.1** steps 4 and 7 — an unknown model falls back; an unreachable MCP server
  costs tools, not the session.
- **Read first:** **Spec §8.1**, **Spec §6.7 (Filtering)**,
  `src/Harness/Agency.Harness/Tools/McpClientPool.cs` (`FailedServers` is populated, never thrown).
- **Deliverable:** Add `src/Acp/Agency.Acp.Test/Sessions/SessionCreationTests.cs`. Assert: (a) a
  requested model absent from the catalogue starts the session on `AgentOptions.DefaultModel` and
  returns success — **never an error**; (b) an unreachable MCP server yields a session with fewer
  tools and a recorded failure; (c) a catalogue fetch that throws yields an **empty** `models[]` and
  a live session; (d) models with `Kind == Embedding` are excluded, while `Kind == null` models are
  retained — **P3 forbids treating unknown as false**.
- **Acceptance:** Compiles and fails. Red.

### Task 7.2.i — Implement the `session/new` algorithm

- **Goal:** Implement **Spec §8.1** exactly, in order.
- **Read first:** the tests from 7.2.t, **Spec §8.1**, **§6.7**.
- **Deliverable:** Implement steps 1–8 of **Spec §8.1** in a `SessionFactory`. Map
  `Model` → `AgentModelOption(Id, Name, Description)`, where `Description` carries residency as a
  **point-in-time statement** ("loaded now") and is `null` when `IsLoaded` is null. Populate the
  per-session `AgentOptions.ContextWindowSize` from `Model.ContextLength`. Wrap the catalogue fetch so
  failure yields an empty list.
- **Acceptance:** 7.2.t green.

---

# Deliverable D8 — Turn driver, stop reasons and the permission bridge

Spec **§6.4**, **§6.6**, **§8.2**, **§8.3**. **Depends on D1, D2, D7.**

### Task 8.1.t — Test: a multi-park Turn yields exactly one terminal response (red)

- **Goal:** Pin **Spec §6.4** and **P4** — park/resume is invisible, and a Turn may park **more than
  once**.
- **Read first:** **Spec §6.4**, **§6.6**, `src/Harness/Agency.Harness/ChatSession.cs`
  (`ResumeWithPermissionsAsync`), `AgentEvents.cs` (`PermissionRequestedEvent`).
- **Deliverable:** Add `src/Acp/Agency.Acp.Test/Turns/TurnDriverTests.cs`. Use a permission evaluator
  that returns `Ask` for the first **two** distinct tools and `Allow` thereafter, so the Turn parks
  twice. Assert: (a) exactly **one** terminal response reaches the client; (b) two
  `session/request_permission` calls were made; (c) events emitted before the first park still
  reached the client. Add a test that a **second** `session/prompt` while one is in flight returns a
  JSON-RPC error rather than queueing (**Spec §6.4 Constraints**).
- **Acceptance:** Compiles and fails. Red.

### Task 8.1.i — Implement `TurnDriver`

- **Goal:** Implement **Spec §6.4** and **§8.2**.
- **Read first:** the tests from 8.1.t, **Spec §8.2** (the numbered flow), **§6.4**.
- **Deliverable:** Add `internal sealed class TurnDriver` in `src/Acp/Agency.Acp/Turns/`. Implement
  **Spec §8.2** steps 1–5 verbatim, including the `Interlocked` one-in-flight guard and the
  **loop** back to step 3 after resume. Store the per-turn `CancellationTokenSource` on
  `SessionState` so `session/cancel` can reach it from any thread.
- **Acceptance:** 8.1.t green.

### Task 8.2.t — Test: every status maps per §8.3 (red)

- **Goal:** Pin **Spec §8.3**'s mapping table.
- **Read first:** **Spec §8.3**, `AgentEvents.cs` (`AgentResultStatus`, including `Truncated` from
  Task 1.3.i).
- **Deliverable:** Add `src/Acp/Agency.Acp.Test/Turns/StopReasonTests.cs`, one case per row:
  `Success` → `end_turn`; `MaxStepsReached` → `max_turn_requests`; `Truncated` → `max_tokens`;
  `Error` → **a JSON-RPC error response, not a stop reason**; `OperationCanceledException` →
  `cancelled` **with partial text retained**; `TimeoutException` → distinguishable from cancel.
  Assert `AwaitingPermission` **never** reaches the client.
- **Acceptance:** Compiles and fails. Red.

### Task 8.2.i — Implement stop-reason mapping

- **Goal:** Implement **Spec §8.3** and **P6**.
- **Read first:** the tests from 8.2.t, **Spec §8.3**, **§4 (P6)**.
- **Deliverable:** Add `internal static class StopReasonMapper`. `Error` produces a JSON-RPC error
  carrying the message — **P6 forbids a silent `end_turn`**. Cancellation is **synthesised**, because
  **Spec §8.3 records that no terminal event is emitted on cancel**.
- **Acceptance:** 8.2.t green.

### Task 8.3.t — Test: a denied tool is recoverable, not fatal (red)

- **Goal:** Pin **Spec §6.6** — a denial reaches the model as a tool result and the Turn continues.
- **Read first:** **Spec §6.6**, `src/Harness/Agency.Harness/Agent.cs` (the four `[Blocked]` paths).
- **Deliverable:** Add `src/Acp/Agency.Acp.Test/Permissions/PersonaPermissionEvaluatorTests.cs`.
  Assert: (a) a granted tool is allowed; (b) any other tool yields a `[Blocked]` tool result with
  `IsError: true` **and the Turn reaches `end_turn`**; (c) `Evaluate` **never** returns `Ask`, over an
  exhaustive sweep of tool names; (d) no file is written to `%LocalAppData%` during the test.
- **Acceptance:** Compiles and fails. Red.

### Task 8.3.i — Implement `PersonaPermissionEvaluator`

- **Goal:** Implement **Spec §6.6**'s v1 configuration.
- **Read first:** the tests from 8.3.t,
  `src/Harness/Agency.Harness/Permissions/IPermissionEvaluator.cs`, **Spec §6.6**.
- **Deliverable:** Add `internal sealed class PersonaPermissionEvaluator : IPermissionEvaluator` in
  `src/Acp/Agency.Acp/Permissions/`. `Evaluate` returns `Allow` for the granted set and
  `Deny` otherwise; it **never** returns `Ask`. `RecordAlwaysAsync` is a no-op returning
  `Task.CompletedTask` — **Spec §6.6 requires no grant file be written**. Do **not** reuse the
  Console's `PermissionEvaluator`, which would leak `permissions.local.json` grants between sessions.
- **Acceptance:** 8.3.t green.

---

# Deliverable D9 — Event translation

Spec **§6.8**. **Depends on D2 and D8.**

### Task 9.1.t — Test: each `AgentEvent` maps to its `session/update` (red)

- **Goal:** Pin **Spec §6.8**'s mapping table.
- **Read first:** **Spec §6.8**, `AgentEvents.cs`.
- **Deliverable:** Add `src/Acp/Agency.Acp.Test/Events/EventTranslatorTests.cs`, one case per row:
  `AssistantTextDeltaEvent` → `agent_message_chunk`; `AssistantThoughtDeltaEvent` →
  `agent_thought_chunk`; `ToolStartedEvent` → `tool_call` (pending); `ToolInvokedEvent` →
  `tool_call_update` correlated by `CallId`, with `IsError` mapping to a failed status;
  `SessionStartedEvent` → **nothing emitted**. Assert the tool name travels **unmodified** — **Spec
  §6.5 forbids prefixing and §6.8 forbids classification**.
- **Acceptance:** Compiles and fails. Red.

### Task 9.2.t — Test: usage is occupancy, not cumulative (red)

- **Goal:** Pin **Spec §6.8 (Usage semantics)**.
- **Read first:** **Spec §6.8**, `AgentEvents.cs` (`IterationCompletedEvent`, `LlmTokenUsage`).
- **Deliverable:** In the same test file: (a) `Used` equals the **latest** iteration's
  `InputTokenCount`, not a running total — drive three iterations with decreasing input counts and
  assert `Used` **decreases**; (b) `Size` equals the session's `ContextWindowSize`; (c) when that is
  null, `Size` is **0** and the update is **still emitted** — **Spec §6.8 requires `Used`
  unconditionally**.
- **Acceptance:** Compiles and fails. Red.

### Task 9.1.i / 9.2.i — Implement `EventTranslator`

- **Goal:** Implement **Spec §6.8** in full.
- **Read first:** both test files, **Spec §6.8**.
- **Deliverable:** Add `internal sealed class EventTranslator` in `src/Acp/Agency.Acp/Events/`,
  translating each `AgentEvent` to zero or one `session/update` notification. Emit usage from
  `IterationCompletedEvent`. Pass tool names through verbatim and set no `ToolKind`.
- **Acceptance:** 9.1.t and 9.2.t green.

---

# Deliverable D10 — The v1 safety guarantee

Spec **§6.5 (the five locks)**, **§4 (P5)**. **Depends on D6–D9.**

**Spec P5:** a property that holds by omission is a property that will be removed by accident.

### Task 10.1.t — Test: the five locks, each separately named (red)

- **Goal:** Implement **Spec §6.5**'s guarantee as one test method with **five independently-named
  assertions**, so a regression names itself.
- **Read first:** **Spec §6.5 (Constraints — the five locks)**, **§4 (P5)**,
  `src/Harness/Agency.Harness/Tools/ToolRegistry.cs`,
  `src/Harness/Agency.Harness/Agent.cs` (the precedence comment:
  `hook Deny > rule Deny > active-skill > hook Ask > rule Allow > unresolved`).
- **Deliverable:** Add `src/Acp/Agency.Acp.Test/V1GuaranteeTests.cs` — **one** test method calling
  five named private assertions:
  1. `NoBuiltInToolsRegistered()` — the registry contains no `read_file`, `write_file`,
     `execute_powershell`, `subagent_tool`.
  2. `SkillToolNotRegistered()` — no `skill` tool.
  3. `NoShellRunnerWired()` — no `ISkillShellRunner` resolves from the container.
  4. `SkillShellExecutionDisabled()` — `Skills:DisableShellExecution` is `true`.
  5. `NoHookCanReturnAsk()` — **behavioural, not structural.** Build the v1 profile, drive a Turn
     that calls **every tool enumerated from the App Tool registry** (do **not** hardcode a list —
     **Spec §6.5 requires enumeration so a tool added later is covered**), and assert **zero**
     `PermissionRequestedEvent` and **zero** `AwaitingPermission`.
  The XML doc must record *why* 5 is behavioural: an evaluator `Allow` does not clear a hook `Ask`,
  and a hook `Ask` parks the Turn even with no evaluator supplied, so no structural assertion can
  close the path.
- **Acceptance:** Compiles and fails until D6–D9 land. Red.

### Task 10.1.i — Compose the v1 profile

- **Goal:** Make **Spec §6.5** true by construction.
- **Read first:** the test from 10.1.t, `src/Harness/Agency.Harness/Agents/AgentServiceCollectionExtensions.cs`
  (`AddAgencyAgent` registers **no tools** — the empty registry is the default, not a switch).
- **Deliverable:** In the host's composition root, register only the MCP pool's tools. Do **not**
  call `AddAgencyConfiguredHooks`. Set `Skills:DisableShellExecution`. Register no
  `ISkillShellRunner`. Add a code comment at each of the five points naming the lock it holds and
  pointing at `V1GuaranteeTests`.
- **Acceptance:** 10.1.t green — all five assertions.

---

# Deliverable D11 — `Agency.Utils.InferenceGate` (separate repository)

Spec **§6.10**, **§4 (P7)**. Lands in the `Agency.HttpCacheProxy` repository. **Independent of
D0–D10.**

### Task 11.1.t — Test: no cache, ever (red)

- **Goal:** Pin **Spec §6.10** — "caching provably off" is proven by the cache code being absent.
- **Read first:** **Spec §6.10**, `Agency.HttpCacheProxy`'s `src/Agency.Utils.HttpCacheProxy/Proxy/`
  (`ProxyMiddleware.cs`, `ResponseCache.cs` — the code that must **not** be referenced).
- **Deliverable:** Add `Agency.Utils.InferenceGate.Tests`. Assert: (a) two identical upstream
  requests produce **two** upstream hits; (b) the `Agency.Utils.InferenceGate` assembly has **no**
  reference to the cache assembly — reflect over `GetReferencedAssemblies()`.
- **Acceptance:** Compiles and fails. Red.

### Task 11.1.i — Scaffold the gate

- **Goal:** Implement **Spec §6.10**'s vehicle decision.
- **Read first:** the test from 11.1.t, **Spec §6.10**.
- **Deliverable:** Create `src/Agency.Utils.InferenceGate/` as a minimal ASP.NET forwarder —
  **no reference to the cache project**. Configuration: `Upstream`, `Capacity`, `ListenPort`.
- **Acceptance:** 11.1.t green.

### Task 11.2.t — Test: the ceiling holds and a dead client releases its slot (red)

- **Goal:** Pin **Spec §6.10**'s mechanism and **P7**.
- **Read first:** **Spec §6.10**, **Spec §4 (P7)**.
- **Deliverable:** Assert: (a) with `Capacity = 2`, four concurrent requests never exceed two
  in flight upstream — instrument a fake upstream with a counter; (b) a client that **aborts**
  mid-request releases its slot, so a fifth request proceeds; (c) `/healthz` reports capacity,
  in-flight, queue depth and upstream reachability, and distinguishes gate-up/upstream-down from
  gate-up/upstream-up.
- **Acceptance:** Compiles and fails. Red.

### Task 11.2.i — Implement the semaphore and health endpoint

- **Goal:** Implement **Spec §6.10**.
- **Read first:** the tests from 11.2.t.
- **Deliverable:** `SemaphoreSlim(Capacity)` acquired around the forward, released in a `finally` so
  an aborted connection cannot leak a slot. Add `/healthz`. Absorb upstream rejections and retry
  behind the semaphore — **Spec §6.10 requires this regardless of measurement**, because the harness
  retries a degenerate response with **no delay at all**.
- **Acceptance:** 11.2.t green.

---

# Deliverable D12 — The milestone

Spec **§13 (End-to-End Flow)**, **§1.2 (Objectives O-1…O-6)**.

### Task 12.1 — End-to-end: an ACP client drives a full turn

- **Goal:** Prove **Spec §13** at the protocol boundary — the **Agency half** of the milestone.
  **This is not the milestone itself.** The agreed milestone is *"two Personas in one Room, live
  chunk rendering in the Room view, and a Stop click leaving both resumable"*, and three of those
  properties live in the client's repository and cannot be tested here. If 12.1 is green, the ACP
  surface is proven and the product is not. See *The joint run* below.
- **Read first:** **Spec §13**, **Spec §1.2**, `src/Harness/Agency.Harness.Test/Functional/`
  (the trait and fixture pattern).
- **Deliverable:** Add `src/Acp/Agency.Acp.Test/Functional/EndToEndAcpTests.cs` with
  `[Trait("Category", "Functional")]` and `[Trait("Category", "RequiresLlm")]`. Drive the real
  `agency-acp` process over stdio with a scripted client: `initialize` → `session/new` with a
  bearer-authenticated MCP server → `session/set_config_option` → `session/prompt`. Assert:
  (a) `agent_message_chunk` arrives **before** the terminal response (**O-1**);
  (b) an MCP tool call succeeds against the authenticated server (**O-2**);
  (c) `session/cancel` mid-turn returns `stopReason: cancelled`, retains partial text, and the **next**
  prompt succeeds (**O-3**);
  (d) no filesystem or shell tool appears in the advertised list (**O-5**).
- **Acceptance:** Green against a live endpoint with
  `dotnet test --filter "Category=Functional" -- RunConfiguration.MaxCpuCount=1`.

### The joint run — the actual milestone

Neither side's plan contains it. Task 12.1 proves the four properties that live here; the remaining
three live in the client and are proven there.

| Property | Proven by | Repo |
|---|---|---|
| Deltas precede the terminal response (**O-1**) | Task 12.1 (a) | **Agency** |
| Authenticated MCP tool call succeeds (**O-2**) | Task 12.1 (b) | **Agency** |
| Cancel returns `cancelled`; next prompt works (**O-3**) | Task 12.1 (c) | **Agency** |
| No filesystem or shell tool advertised (**O-5**) | Task 12.1 (d) | **Agency** |
| Chunks render live in the Room view | — | **client** |
| Two Personas in one Room; reply gating and catch-up | — | **client** |
| A human's Stop click leaves both resumable | — | **client** |

**Client-side prerequisites**, which gate scheduling the joint run and are not on this plan:

1. Host profiles, so a Persona can be pointed at `agency-acp` at all.
2. **The per-backend tool-name prefix.** Without it the system prompt names `mcp__team__get_help`
   while the model is advertised `get_help` — **Spec §6.5** records that the harness mints no prefix.
   This is the one that makes a Persona look *broken* rather than *misconfigured*.
3. A per-host model catalogue, or the client offers the wrong backend's models for a local Persona.
4. `session/close` on session dispose — see **Spec §12 (E-15)**; the adapter does not depend on it,
   but sending it releases resources sooner.

**Do not schedule the joint run off this plan alone.**

---

## Sequencing summary

| Deliverable | Depends on | May start |
|---|---|---|
| D0 MCP transport | — | now |
| D1 Turn correctness | — | now |
| D2 Streaming | — | now |
| D3 Identity seam | — | now |
| D4 Provider metadata | — | now |
| D5 Memory defects | — | now |
| D6 Scaffold + transport | — | now |
| D7 Session lifecycle | D4, D6 | after |
| D8 Turn driver | D1, D2, D7 | after |
| D9 Event translation | D2, D8 | after |
| D10 The guarantee | D6–D9 | after |
| D11 Inference gate | — | now (separate repo) |
| D12 Milestone | all | last |

**Critical path:** D2 → D7 → D8 → D9 → D10 → D12. **D2 is the long pole**; start it early and treat
its "entire suite stays green" acceptance as the project's main regression gate.

---

# Execution record — completed 2026-09-16

**All 13 deliverables (D0–D12) are implemented.** **Entire suite — unit *and* functional —
24 assemblies, 1,896 tests, 0 failures, 2 pre-existing skips, 0 build warnings**, run with
`-- RunConfiguration.MaxCpuCount=1` against the live endpoint (`google/gemma-4-e2b` via the caching
proxy) and a running `postgres_vector_db`.

> ### The functional suite caught two regressions the unit suite could not
>
> The unit suite (`--filter "Category!=Functional"`) was green after **every** deliverable and still
> hid a real null-dereference in the agent loop. Both defects were in test doubles and test design,
> but the first was a genuine crash on a code path a real caller reaches.
>
> 1. **`Mock<IChatClient>` returns `null` from un-stubbed `GetStreamingResponseAsync`.** D2's blast
>    radius was scoped by *directory* (`Agency.Harness.Test/`), which found the three hand-written
>    fakes but missed every Moq stub and the entire `Memory` tree. The memory consolidator's
>    sub-agent therefore threw `NullReferenceException` at `Agent.cs:748` the moment the loop went
>    streaming. **Search by symbol, solution-wide, for both `: IChatClient` and `Mock<IChatClient>`.**
>    Fixed in `EndToEndConsolidatorTests.cs` and `TestInfrastructure.cs` via
>    `ToChatResponseUpdates()`, preserving the stateful per-turn counter by extracting a single
>    `NextResponse()` shared by both setups.
> 2. **O-1 was asserted on a tool-calling prompt.** "A streamed chunk arrives before the terminal
>    response" is a property of *streaming order*, but it was checked on a turn that asked the model
>    to call a tool — coupling it to tool-calling behaviour, which **§12 (E-14)** records as
>    unreliable with no dependable signal. When `google/gemma-4-e2b` emitted tool-call-only assistant
>    messages, no `agent_message_chunk` was produced and a sound assertion failed for an unrelated
>    reason (~1 run in 5). Diagnosis: it **never** failed with an ordering violation, only with "no
>    chunk at all" — a real streaming defect would fail deterministically or show chunk-after-terminal.
>    Fixed by asserting O-1 on a dedicated **prose-only** turn; the assertion itself is unchanged, and
>    the tool prompt still proves O-2.
>
> **Also:** never pipe `dotnet test` through `head`/`tail` — the pipeline's exit code masks the
> runner's. Redirect to a file and grep it.

## Corrections to this plan

The plan was written from the spec, not from compiling against the SDK. These tasks were
**unimplementable or wrong as written** and have been executed differently. The corrections are
recorded here so the next reader does not rediscover them.

| Task | What the plan said | Reality |
|---|---|---|
| **2.1.i** | Yield a delta per `TextContent` inside the `await foreach`, *"preserving both retry loops"* | **Uncompilable — CS1626.** C# forbids `yield return` inside a `try` with a `catch`, and the empty-choices retry is exactly that. Fixed with a **manual enumerator**: `MoveNextAsync()` inside the `try`, `yield return` outside — the shape `ChatIteratorAsync` (lines ~257-280) already used. |
| **2.1.t** | Extend `FakeLlmClient` | The class is **`FakeChatClient`**; `FakeLlmClient` is only the filename. Three fakes threw `NotSupportedException` on streaming (`FakeChatClient`, `GoalkeeperTests.CapturingFakeChatClient`, `TurnTimeoutTests.HangingChatClient`) and **all three** had to be fixed or the whole suite exploded. Solution: `ChatResponse.ToChatResponseUpdates()` decomposes the already-enqueued response, so **no existing test needed rewriting**. |
| **1.3.i** | Find *"exhaustive `switch`es over the enum"* in `ConsoleChatSession.cs` | **There are none.** The Console uses `==` equality. The real seam is `ConsoleChatSession.cs:543` — following the plan literally would have silently stopped rendering truncation messages. |
| **1.3.i** | *(unlisted)* | `LoopRunner.cs:201` also consumed `AgentResultStatus.Error` to abort the loop. Splitting `Truncated` out silently converted a hard abort into an unbounded retry against a fixed token ceiling. Now `is Error or Truncated`. |
| **4.1.t** | Create `src/Llm/Agency.Llm.Common.Test/` | Unnecessary — `src/Llm/Agency.Llm.Test/` already references both providers. Tests went there instead. |
| **6.1.i** | Use *"`dotacp.protocol`'s own `JsonSerializerOptions`"* | **The package is Newtonsoft.Json, not System.Text.Json.** There are no options to find: converters are attached as **attributes on each DTO**, so plain `JsonConvert`/`JObject` with defaults is correct. The package also ships **no JSON-RPC envelope type** — `MethodDispatcher` builds `{jsonrpc,id,result|error}` by hand. |
| **7.2.i** | Map `Model` → `AgentModelOption(Id, Name, Description)`; return `models[]` and `effortLevels[]` | **None of those exist.** `NewSessionResponse` is `{ Meta, ConfigOptions, Modes, SessionId }`. Catalogue and effort ladder are both `SessionConfigOption` → `SessionConfigSelect` → `SessionConfigSelectOption { Name, Value, Description }`. `InitializeResponse` likewise nests under `AgentInfo`/`AgentCapabilities` rather than flat fields. |
| **7.2.i** | Step order: build Agent/ChatSession, *then* connect MCP | `ChatSession`'s `ToolContext` is immutable after construction, so the pool must be built **first**. |
| **7.2.i** | *(unlisted)* | `NewSessionRequest` carries **no model field** — the model is chosen via `session/set_config_option` after creation. A requested-model-at-`new` path was added speculatively via `_meta.model`; **unverified against any real client.** |
| **9.1.t** | Assert no `ToolKind` classification | `ToolCall.Kind` is a **non-nullable enum defaulting to `Read`**, so "unclassified" had to be expressed as an explicit `ToolKind.Other` sentinel rather than an absent field. |
| **11.2.i** | *"never pass a 4xx through"* | Scoped to **429/503**. Blanket-retrying a permanent `400`/`404` would loop forever on a request that can never succeed. |

## Defects found that no spec reading would surface

- **`PersonaPermissionEvaluator` was fully implemented, fully tested, and never registered in DI.**
  The deny-by-default gate was simply not running. It was harmless only for as long as no tool
  existed to deny — precisely the failure mode **P5** exists to prevent. `V1GuaranteeTests` could not
  catch it because lock #5 built its own profile instead of resolving the production one. Closed by a
  **sixth lock**, `PermissionEvaluatorIsWired`, which asserts the real composition root resolves it.
  The evaluator is registered **scoped**, not singleton — a singleton would leak one session's
  granted tools into every other session.
- **Effort was inert.** `LlmClientOptions.EnableThinking`/`ThinkingBudgetTokens` (D4) and the ACP
  `effort` config option (D7/D8) were both built but never connected, because
  `IAgentFactory.CreateAgent` had no effort parameter. Closed with a default-interface-method
  overload taking a `Func<LlmClientOptions, LlmClientOptions>`.

## Additions beyond the plan

- `McpClientPool.DisposeAsync` made **idempotent** (`IsDisposed`) — required because §6.3 reaches
  disposal from three independent triggers.
- `src/Acp/Agency.Acp.Test/xunit.runner.json` sets `parallelizeTestCollections: false`, suppressing a
  real static-`Meter`/`ActivitySource` race and enforcing one-inference-at-a-time.

## Known unresolved

- **Effort tier values** (Claude `1024`/`4096`/`16384` budget tokens) are chosen defaults. The spec
  specifies the *mechanism* but no numbers.
- **`ConsolidatorOptions`** has the same unbound `AddOptions<T>()` bug D5 fixed, but sits outside
  D5's three named call sites. Deliberately untouched.
- **`LoopObservabilityTests.HappyPath_EmitsAllExpectedMetrics`** is intermittently flaky from static
  `Meter` state bleeding across tests. Passes in isolation and in a single-assembly run.
- **`CacheKeyTests.Method_IsNormalisedToUpperCase`** fails on `main` in the `Agency.HttpCacheProxy`
  repo — pre-existing, unrelated to D11.

## The milestone is still joint

**Task 12.1 proves the ACP surface, not the product.** It proves the four properties that live in
this repo — deltas before the terminal response (O-1), an authenticated MCP tool call (O-2), clean
cancel leaving the session resumable (O-3), and no filesystem or shell tool advertised (O-5). The
remaining three — live chunk rendering in the Room view, two Personas in one Room, and a Stop click
leaving both resumable — live in Agency.Huddle and are proven there. **Do not schedule the joint run
off this plan alone**; see *The joint run* above for the four client-side prerequisites.
