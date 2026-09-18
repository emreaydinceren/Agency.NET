# Agency.Acp — Persona Identity and Dispatch Robustness (Project Plan)

Decomposition of **[`Agency.Acp.PersonaIdentity-Specifications.md`](Agency.Acp.PersonaIdentity-Specifications.md)** into atomic, context-free tasks. Every task is self-contained: a sub-agent with no project history should be able to execute it from the task text plus the files it names.

## How to use this document

1. Work **one Task at a time, top to bottom**. A Task marked **(Test)** must go **RED** before the **(Implementation)** task that follows it is written. Red for the right reason — a compile error because the type does not exist yet counts; a typo does not.
2. **Vertical slices only.** Do **not** write all tests, then all implementations. Spec **§15** records why: tests written in bulk describe *imagined* behaviour and end up asserting shape rather than conduct.
3. Each task cites the **Spec** section defining its requirement. When Spec and plan disagree, **the Spec wins** — raise the conflict, do not guess (CLAUDE.md §1).
4. **Surgical changes only** (CLAUDE.md §3): touch only the files named in *Deliverable*. Do not improve adjacent code. If you notice unrelated dead code, mention it; do not delete it.
5. **D0 is blocking** — it unblocks the joint milestone. D1–D2 may proceed in any order after. D3 is last. D4 is mandatory, not optional (**Spec §14.6**).

---

## Repo-wide conventions (read once, applies to every task)

- **Solution:** `src/Agency.slnx`. **Adapter:** `src/Acp/Agency.Acp/`. **Adapter tests:** `src/Acp/Agency.Acp.Test/`. **Harness:** `src/Harness/Agency.Harness/`. **Harness tests:** `src/Harness/Agency.Harness.Test/`.
- **Build:** `dotnet build src/Agency.slnx -c Release`. **Unit tests:** `dotnet test src/Agency.slnx -c Release --filter "Category!=Functional" --nologo -- RunConfiguration.MaxCpuCount=1`.
- **Compiler:** `TreatWarningsAsErrors=true`, `Nullable=enable`, `AnalysisLevel=latest-Recommended`. **A warning fails the build.**
- **Line endings:** `.gitattributes` pins `*.cs text eol=crlf`. **Write every new `.cs` file with CRLF** and verify byte-for-byte — verbatim string literals bake newlines into the functional-test HTTP cache key, so LF breaks offline CI.
- **Public API tracking:** `Microsoft.CodeAnalysis.PublicApiAnalyzers` is a `GlobalPackageReference` on every non-test project. Any new/changed **public** member needs a sorted line in that project's `PublicAPI.Unshipped.txt`. **Zero `*REMOVED*` entries are permitted by this work** (**Spec §14.2**).
- **Internals visibility:** `src/Acp/Agency.Acp/AssemblyInfo.cs` declares `[assembly: InternalsVisibleTo("Agency.Acp.Test")]`; `src/Harness/Agency.Harness/AssemblyInfo.cs` declares it for `Agency.Harness.Test`, `Agency.Harness.Console`, `Agency.Harness.Console.Test`. **Prefer `internal` + the existing entry over `public`** — internal types need no PublicAPI entries.
- **Test stack:** xunit.v3 3.2.2, Moq 4.20.72. Pass `TestContext.Current.CancellationToken` to async calls under test.
- ⚠️ **`Mock<IChatClient>` returns `null` from un-stubbed `GetStreamingResponseAsync`**, and the agent loop calls it. Any new mock driving a turn must stub it — use `response.ToChatResponseUpdates()`. See `src/Memory/Agency.Memory.Functional.Test/TestInfrastructure.cs` for the pattern.
- `src/Acp/Agency.Acp.Test/xunit.runner.json` sets `parallelizeTestCollections: false`. **Leave it** — it suppresses a real static-`Meter` race.
- **Style:** file-scoped namespaces, XML doc comments on all public members, 4-space indent.
- **Never run `--filter "Category=Functional"`** unless the task says so — it needs a live LLM endpoint and the GPU crashes under concurrency.
- **Do not commit** unless explicitly asked (CLAUDE.md).

### Terminology

| Term | Meaning |
|---|---|
| **Identity** | The Persona-supplied text replacing the opening line of the system prompt |
| **Append semantics** | Replace the identity *line*; keep ReAct, discovery, skills, grounding (**Spec §14.1**) |
| **Turn** | One `session/prompt` request and its single terminal response |
| **Iteration** | One LLM call inside a Turn; a Turn may contain many |

---

# Deliverable D0 — The identity path

Spec **§1.2**, **§6.1**, **§6.2**, **§6.4**, **§8.1**. **Blocking: this is the only item standing between the two repos and the joint milestone.**

Today `_meta.systemPrompt` is read nowhere, so every Persona runs on the harness's baseline identity and two Personas are indistinguishable.

---

### Task 1 — (Test) Identity prompt parsing

- **Goal:** Pin the two accepted wire shapes and the tolerated-unknown rule from **Spec §6.1** before anything depends on them.
- **Read first:** **Spec §6.1 (IdentityPromptParser)**, **Spec §14.1 (why bare string is an append)**, **Spec §12 (E-3, E-4, E-5)**. For test style, `src/Acp/Agency.Acp.Test/Dispatch/MethodDispatcherTests.cs`.
- **Deliverable:** Create `src/Acp/Agency.Acp.Test/Dispatch/IdentityPromptParserTests.cs`, namespace `Agency.Acp.Test.Dispatch`. The type under test does not exist yet — this will not compile, which is the required RED.
  Using `Newtonsoft.Json.Linq.JToken` inputs, assert `Agency.Acp.Dispatch.IdentityPromptParser.Parse(JToken?)` returns:
  | Input | Expected |
  |---|---|
  | `JObject` `{"append":"You are Ana"}` | `"You are Ana"` |
  | `JValue` `"You are Ana"` (bare string) | `"You are Ana"` |
  | `null` | `null` |
  | `JValue.CreateNull()` | `null` |
  | `JObject` `{}` | `null` |
  | `JObject` `{"replace":"X"}` (unknown key) | `null` |
  | `JValue` `""` | `null` |
  | `JValue` `"   "` (whitespace) | `null` |
  | `JValue` `42` (number) | `null` |
  | `JArray` `["a"]` | `null` |
  | `JObject` `{"append":""}` | `null` |
  Add one test asserting **no exception escapes** for any of the above.
- **Acceptance:** Compiles **fail** with `CS0234`/`CS0103` — `IdentityPromptParser` does not exist. RED.

### Task 2 — (Implementation) `IdentityPromptParser`

- **Goal:** Implement the total parse function specified in **Spec §6.1**.
- **Read first:** The test from Task 1. **Spec §6.1**, **Spec §8.1 step 2 ("Step 2 is total")**.
- **Deliverable:** Create `src/Acp/Agency.Acp/Dispatch/IdentityPromptParser.cs`:
  ```csharp
  internal static class IdentityPromptParser
  {
      internal static string? Parse(JToken? token);
  }
  ```
  Logic exactly per **Spec §6.1**: `JValue` of type String → normalise; `JObject` with an `append` key → normalise that value; everything else → `null`. `Normalise(s) = string.IsNullOrWhiteSpace(s) ? null : s`.
  **It must not throw for any input** — every branch is a type test, never a cast that can fail.
  XML doc must state that **both shapes carry append semantics** and cite **Spec §14.1** for why a bare string is not a replace.
  Keep it `internal` — reachable via the existing `InternalsVisibleTo("Agency.Acp.Test")`; no `PublicAPI.Unshipped.txt` entry required.
- **Acceptance:** Task 1 green. `dotnet build src/Agency.slnx -c Release` warning-free.

---

### Task 3 — (Test) `Agent.CreateContext` carries identity

- **Goal:** Pin the new overload from **Spec §6.4** and prove the existing signature is untouched (**Spec §14.2**, **O-6**).
- **Read first:** `src/Harness/Agency.Harness/Agent.cs` — `public static Context CreateContext(...)` at **line 161**, which builds `Query = new QueryContext { Prompt = initialPrompt, InstructionsBlock = instructionsBlock }`. `src/Harness/Agency.Harness/Contexts/QueryContext.cs` — `IdentityPrompt` already exists at line 18. **Spec §6.4**, **Spec §14.2**.
- **Deliverable:** Create `src/Harness/Agency.Harness.Test/CreateContextIdentityTests.cs`. Assert:
  (a) `Agent.CreateContext("p", identityPrompt: "You are Ana")` yields `ctx.Query.IdentityPrompt == "You are Ana"`;
  (b) **the existing call shape `Agent.CreateContext("p")` still compiles verbatim** and yields `ctx.Query.IdentityPrompt == null`;
  (c) `ctx.Query.Prompt` and `ctx.Query.InstructionsBlock` are unaffected by the new parameter.
  (b) is the back-compat gate — if it needs editing later, an optional parameter was added instead of an overload.
- **Acceptance:** Compiles and fails — no overload accepting `identityPrompt`. RED.

### Task 4 — (Implementation) `Agent.CreateContext` overload

- **Goal:** Implement the additive overload per **Spec §6.4** and **§14.2**.
- **Read first:** The test from Task 3. `src/Harness/Agency.Harness/Agent.cs` lines 155–180. `src/Harness/Agency.Harness/PublicAPI.Unshipped.txt`.
- **Deliverable:** In `Agent.cs`, add an overload:
  ```csharp
  public static Context CreateContext(
      string initialPrompt,
      ToolContext? tools, EnvironmentalContext? environment, UserSpecificContext? user,
      TimeProvider? timeProvider, SkillContext? skills, SessionContext? session,
      string? instructionsBlock,
      string? identityPrompt)
  ```
  which sets `Query = new QueryContext { Prompt = initialPrompt, InstructionsBlock = instructionsBlock, IdentityPrompt = identityPrompt }`.
  **The existing 8-parameter member must remain, with its signature byte-identical**, and must delegate to the new one passing `identityPrompt: null`. One implementation, two entry points — do not duplicate the initialiser.
  Add the new member to `PublicAPI.Unshipped.txt`, sorted.
  ⚠️ **If `PublicAPI.Unshipped.txt` gains a `*REMOVED*` line, you have added an optional parameter instead of an overload.** Rework it — that is binary-breaking for the published `AgencyDotNet.Harness` package (**Spec §14.2**).
- **Acceptance:** Task 3 green, including assertion (b) **unedited**. `PublicAPI.Unshipped.txt` contains no `*REMOVED*`. Full unit suite green.

---

### Task 5 — (Test) A `ChatSession` identity reaches the submitted prompt

- **Goal:** Prove the end-to-end harness behaviour of **Spec §6.4** and **O-2** — the identity replaces line 1 and the harness scaffolding survives.
- **Read first:** `src/Harness/Agency.Harness/ChatSession.cs` — constructor at **line 45**, `Agent.CreateContext` call sites at **line 93** (`PreviewContext`) and **line 182** (lazy `_ctx ??=`). `src/Harness/Agency.Harness/SystemPromptBuilder.cs` lines 18–40 — note the default line *"You are an autonomous agent operating inside the Agency runtime."*, the ReAct block, and the progressive-discovery block. `src/Harness/Agency.Harness.Test/SystemPromptIdentityTests.cs` for the established pattern. **Spec §6.4**, **§14.1**.
- **Deliverable:** Create `src/Harness/Agency.Harness.Test/ChatSessionIdentityTests.cs`. Using `FakeChatClient` (in `src/Harness/Agency.Harness.Test/Fakes/FakeLlmClient.cs` — note the **class** is `FakeChatClient`), drive one turn through a `ChatSession` constructed with `identityPrompt: "You are Ana, who routes."` and capture the submitted system prompt via `ReceivedSystemPrompts`. Assert:
  (a) the first line is `You are Ana, who routes.`;
  (b) the default identity line does **not** appear;
  (c) the ReAct text *"When solving a task, always explain your reasoning"* **does** appear;
  (d) with `identityPrompt: null`, the default line appears verbatim.
  Add one test asserting `PreviewContext()` (internal, reachable via `InternalsVisibleTo`) reports the **same** identity — **Spec §6.4** warns that missing this second call site makes preview hosts disagree with live turns.
- **Acceptance:** Compiles and fails — the constructor has no `identityPrompt` parameter. RED.

### Task 6 — (Implementation) `ChatSession` identity threading

- **Goal:** Hold identity on the session and inject it at **both** Context-creation sites per **Spec §6.4**.
- **Read first:** The test from Task 5. `src/Harness/Agency.Harness/ChatSession.cs` — field `_instructionsBlock` at line 26, assignment at line 53, call sites at lines 93 and 182. `PublicAPI.Unshipped.txt`.
- **Deliverable:** In `ChatSession.cs`:
  1. Add `private readonly string? _identityPrompt;`
  2. Add a constructor **overload** taking `string? identityPrompt` as a trailing parameter after `instructionsBlock`. **The existing constructor must remain byte-identical** and delegate with `identityPrompt: null`.
  3. Pass `identityPrompt: this._identityPrompt` to `Agent.CreateContext` at **both** line 93 and line 182. Missing either is a silent divergence.
  4. Add the new constructor to `PublicAPI.Unshipped.txt`, sorted.
  Do **not** add identity to `AgentOptions` — **Spec §6.4** records why: identity is a property of the query, not of the agent's operating limits.
- **Acceptance:** Task 5 green (all five assertions). `PublicAPI.Unshipped.txt` contains no `*REMOVED*`. Existing `ChatSession` tests pass **unchanged**.

---

### Task 7 — (Test) `session/new` wires identity end to end

- **Goal:** Prove the hop that was missing — **Spec §1.2** — through the dispatcher, not the parser.
- **Read first:** `src/Acp/Agency.Acp/Dispatch/MethodDispatcher.cs` — `HandleSessionNewAsync` at **lines 150–165**, currently reading `@params?["_meta"]?["model"]`. `src/Acp/Agency.Acp/Sessions/SessionFactory.cs` — `CreateAsync(NewSessionRequest, string?, CancellationToken)` at **line 63**. `src/Acp/Agency.Acp.Test/Sessions/SessionCreationTests.cs` for the fake-LLM seam. **Spec §6.2**, **§8.1**.
- **Deliverable:** Create `src/Acp/Agency.Acp.Test/Sessions/SessionIdentityTests.cs`. Drive a real `session/new` whose params carry `_meta.systemPrompt = { "append": "You are Ana, who routes." }`, then drive one turn, and assert the **submitted system prompt** carries that identity.
  **Assert on the submitted prompt, not on `SessionState`** — spec **§15** requires observable behaviour, and a `SessionState` assertion would pass even if the value never reached the model, which is precisely the defect being fixed.
  Add a second test with `_meta` absent asserting the default line and a **successful** session (**Spec §12 E-1** — `session/new` stays fail-soft).
- **Acceptance:** Compiles and fails — identity is not read. RED **for the right reason**: confirm the failure is a missing identity in the prompt, not a null-reference or a session-creation error.

### Task 8 — (Implementation) Read `_meta.systemPrompt` in `session/new`

- **Goal:** Close the hop. **Spec §6.2**, **§8.1 step 1a**.
- **Read first:** The test from Task 7. `MethodDispatcher.cs` lines 146–166; `SessionFactory.cs` lines 63–70.
- **Deliverable:**
  1. In `MethodDispatcher.HandleSessionNewAsync`, replace
     `string? requestedModelId = @params?["_meta"]?["model"]?.Value<string>();`
     with
     `string? identityPrompt = IdentityPromptParser.Parse(@params?["_meta"]?["systemPrompt"]);`
  2. Rename `SessionFactory.CreateAsync`'s second parameter from `requestedModelId` to `identityPrompt` and thread it to the `ChatSession` constructor's new `identityPrompt` argument. `SessionFactory` is `internal`; no PublicAPI impact.
  3. Record it on `SessionState.IdentityPrompt` (add the field — `internal`, `string?`) per **Spec §7.2**.
  4. **Rewrite** the XML doc on `HandleSessionNewAsync` — it currently documents the `_meta.model` behaviour and would otherwise be actively wrong.
  5. Add one `Debug`-level log line recording the parse outcome and identity length (**Spec §6.1** — this is the observability the original defect lacked).
  ⚠️ **Both parameters are `string?`, so the compiler cannot catch a mix-up.** Change the parameter name and every call site in the same edit.
- **Acceptance:** Task 7 green. Existing `SessionCreationTests` and `SessionRegistryTests` pass unchanged. Build warning-free.

---

### Task 9 — (Test) Identity is present on every iteration

- **Goal:** Assert **O-1** — the property that shipped structurally true and silently broken.
- **Read first:** **Spec §9 (per-iteration rebuild)**, **Spec §1.3 (O-1)**. `src/Harness/Agency.Harness/SystemPromptBuilder.cs`. `FakeChatClient.ReceivedSystemPrompts` collects one entry per call.
- **Deliverable:** In `src/Harness/Agency.Harness.Test/ChatSessionIdentityTests.cs`, add a test scripting a **two-iteration** turn (first response carries a `FunctionCallContent`, second is plain text). Assert `ReceivedSystemPrompts` has **two** entries and **both** contain the identity text.
  **This task has no paired implementation** — it asserts a property that falls out of §9's per-iteration rebuild. That is deliberate: a property believed to hold for structural reasons and never asserted is exactly how the original defect shipped.
- **Acceptance:** Green immediately. **Then prove it load-bearing:** temporarily change `SystemPromptBuilder` to emit the identity only when `ctx.IterationCount <= 1`, confirm **this** test goes red, restore. Report both runs.

### Task 10 — (Test) Two Personas do not bleed

- **Goal:** Assert **O-3** — the milestone property.
- **Read first:** **Spec §7.3 (single assignment, single reader)**, **Spec §1.3 (O-3)**, **Spec §12 (E-7)**.
- **Deliverable:** In `src/Acp/Agency.Acp.Test/Sessions/SessionIdentityTests.cs`, add a test creating **two** sessions in one dispatcher with different `_meta.systemPrompt` values, driving one turn each. Assert each session's submitted prompt contains its **own** identity and **not** the other's.
  No paired implementation — asserts the immutability described in **Spec §7.3**.
- **Acceptance:** Green. Load-bearing proof: temporarily hoist the identity to a `static` field, confirm this test reddens, restore.

### Task 11 — (Test) Identity survives a model change

- **Goal:** Assert **Spec §12 (E-8)** — `SetAgent` preserves the `Context`, so identity survives `session/set_config_option`.
- **Read first:** `src/Harness/Agency.Harness/ChatSession.cs` — `SetAgent` at line 127. `src/Acp/Agency.Acp.Test/Dispatch/SetSessionConfigOptionTests.cs`. **Spec §12 (E-8)**, **§7.2**.
- **Deliverable:** In `SessionIdentityTests.cs`, add a test: create a session with an identity, drive a turn, issue `session/set_config_option` changing the `model` config id, drive a second turn, assert the identity is still in the submitted prompt.
  No paired implementation — asserts existing `SetAgent` behaviour.
- **Acceptance:** Green. If it fails, **stop and report** — that would mean `SetAgent` does not preserve the `Context` and the spec's §7.2 claim is wrong.

---

### Task 12 — (Test) `_meta.model` is no longer read

- **Goal:** Pin the removal from **Spec §6.2** and the additive-surface rule from **Spec §14.2** / **O-6**.
- **Read first:** **Spec §6.2**, **Spec §12 (E-14)**, **Spec §14.5 (G-2)**. Huddle confirmed they never send `_meta.model` and select a model via `session/set_config_option`.
- **Deliverable:** In `src/Acp/Agency.Acp.Test/Sessions/SessionIdentityTests.cs`, add a test sending `_meta.model = "some/other-model"` and asserting the session starts on `AgentOptions.DefaultModel` — i.e. the field is ignored.
  Add `src/Acp/Agency.Acp.Test/PublicApiSurfaceTests.cs` asserting that **no** `PublicAPI.Unshipped.txt` under `src/` contains the literal `*REMOVED*`. Locate the repo root by walking up from `AppContext.BaseDirectory` until a directory containing `Agency.slnx` under `src/` is found; if not found, skip rather than fail (the file layout differs in a packed run).
- **Acceptance:** The `_meta.model` test fails while the reader still exists (it would honour the requested model). The PublicAPI test passes now and guards every later task. RED on the first.

### Task 13 — (Implementation) Delete the `_meta.model` reader

- **Goal:** Remove dead code per **Spec §6.2** and CLAUDE.md §2.
- **Read first:** The test from Task 12. `MethodDispatcher.cs` line 158 (already replaced in Task 8 — confirm no residue).
- **Deliverable:** Confirm no `_meta` read other than `systemPrompt` remains anywhere under `src/Acp/`: `grep -rn '_meta' src/Acp/ --include=*.cs`. Remove any leftover `requestedModelId` parameter, local, or doc reference. Do **not** leave a deprecated overload.
- **Acceptance:** Task 12's first test green. `grep` shows `_meta` only for `systemPrompt`. Build warning-free.

---

# Deliverable D1 — The dispatch error contract

Spec **§6.3**, **§8.2**, **§4 (P3)**, **§12 (E-9, E-10, E-11)**.

**Spec §5.2** records the current hole: a handler throwing anything other than `AcpJsonRpcException` or `JsonException` escapes `DispatchAsync`, is swallowed by `StdioTransport.InvokeHandlerAsync`, and the client receives **no reply, no error and no crash** — waiting forever. Base spec **P6** promises this cannot happen.

---

### Task 14 — (Test) An unmapped handler fault becomes `-32603`

- **Goal:** Pin **Spec §6.3** and **O-4**.
- **Read first:** `src/Acp/Agency.Acp/Dispatch/MethodDispatcher.cs` — `DispatchAsync` catch block at **lines 102–114**, catching only `AcpJsonRpcException` (107) and `JsonException` (111). `src/Acp/Agency.Acp/Transport/StdioTransport.cs` — `InvokeHandlerAsync` at **lines 77–88** with its bare `catch (Exception) { }`. **Spec §6.3**, **§8.2**, **§12 (E-9)**.
- **Deliverable:** Create `src/Acp/Agency.Acp.Test/Dispatch/DispatchErrorContractTests.cs`. Register a handler that throws `InvalidOperationException("No LLM client named 'foo'")`, dispatch a request **with an id**, and assert the response is a JSON-RPC error with `code == -32603` whose `message` contains **both** the method name and `InvalidOperationException`.
  The realistic trigger is `IAgentFactory.CreateAgent` failing when `Agent:DefaultClientName` matches no configured client — state that in the test's XML doc.
- **Acceptance:** Compiles and fails — the exception escapes and no response is produced. RED.

### Task 15 — (Implementation) Terminal catch in `DispatchAsync`

- **Goal:** Make **P6** true on every path. **Spec §6.3**, **§8.2 step 6**.
- **Read first:** The test from Task 14. `MethodDispatcher.cs` lines 100–116. **Spec §14.3 (why here, not the transport)**.
- **Deliverable:** Add a terminal `catch (Exception ex)` to `DispatchAsync`, **after** the existing two arms, returning
  `BuildError(idToken, ErrorCode.InternalError, $"Internal error handling '{method}': {ex.GetType().Name}: {ex.Message}")`.
  Include the exception **message**; do **not** put the stack trace on the wire — log it to stderr instead.
  **Ordering is mandatory:** `AcpJsonRpcException` and `JsonException` keep their specific mappings and must remain first.
  Do **not** move this logic into `StdioTransport` — **Spec §14.3** records that the transport has no access to the request id and cannot distinguish a notification from a request.
- **Acceptance:** Task 14 green. Existing `MethodDispatcherTests` pass unchanged. Build warning-free.

### Task 16 — (Test) Existing error mappings are unchanged

- **Goal:** Regression-guard the catch ordering introduced in Task 15. **Spec §6.3**, **§8.3**.
- **Read first:** `src/Acp/Agency.Acp.Test/Dispatch/MethodDispatcherTests.cs` — existing assertions for `-32601`, `-32700`, `-32602`.
- **Deliverable:** In `DispatchErrorContractTests.cs`, add tests asserting a handler throwing `AcpJsonRpcException(ErrorCode.InvalidParams, "…")` still yields **`-32602`** with its own message, and one throwing `JsonException` still yields **`-32602`**, i.e. neither is captured by the new terminal arm.
- **Acceptance:** Green. Load-bearing proof: temporarily move the terminal `catch (Exception)` **above** the specific arms, confirm these tests redden, restore. Report both runs.

### Task 17 — (Test) Shutdown cancellation produces no error response

- **Goal:** Pin **Spec §12 (E-10)** and **§8.2 step 5**.
- **Read first:** **Spec §6.3 (Implementation notes — cancellation)**, **§8.2**. Note the ordering constraint: *"Reversed, a clean shutdown emits a spurious `InternalError` for every in-flight handler."*
- **Deliverable:** In `DispatchErrorContractTests.cs`, add a test registering a handler that throws `OperationCanceledException` **while the supplied `CancellationToken` is already cancelled**. Assert `DispatchAsync` produces **no error response** — it rethrows or returns null per the implementation, and the test asserts no `-32603` is emitted.
- **Acceptance:** Compiles and fails — the terminal catch from Task 15 currently maps it to `-32603`. RED.

### Task 18 — (Implementation) Cancellation guard

- **Goal:** Implement **Spec §8.2 step 5**.
- **Read first:** The test from Task 17; `MethodDispatcher.DispatchAsync`.
- **Deliverable:** Add, **before** the terminal arm:
  ```csharp
  catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
  ```
  so a clean shutdown unwinds without emitting spurious errors, while a cancellation that is *not* the supplied token's still falls through to `InternalError`.
- **Acceptance:** Task 17 green; Tasks 14 and 16 still green.

### Task 19 — (Test) A faulting notification emits nothing but logs

- **Goal:** Pin **Spec §12 (E-11)** — JSON-RPC forbids responding to a notification.
- **Read first:** `MethodDispatcher.DispatchAsync` — the `hasId` checks; `BuildError` returns `null` when `hasId` is false. **Spec §6.3 (Implementation notes — notifications)**.
- **Deliverable:** In `DispatchErrorContractTests.cs`, add a test dispatching a **notification** (no `id`) whose handler throws, asserting the returned value is `null` (nothing written to the wire). Capture the configured `ILoggerProvider` and assert a log entry **was** emitted — silence on the wire is correct, silence everywhere is the bug class this deliverable exists to close.
- **Acceptance:** Compiles; the wire assertion passes, the **log assertion fails** (no logging yet). RED on the log.

### Task 20 — (Implementation) Fault logging

- **Goal:** Make swallowed faults observable. **Spec §6.3**, **§10**.
- **Read first:** The test from Task 19. `StdioTransport.InvokeHandlerAsync` lines 77–88. `src/Acp/Agency.Acp/Program.cs` for how logging is configured (**all sinks go to stderr or a file — never `Console.Out`**).
- **Deliverable:**
  1. In `DispatchAsync`'s terminal arm, log the full exception (including stack trace) at `Error` before returning the wire error.
  2. In `StdioTransport.InvokeHandlerAsync`, **keep** the `catch (Exception)` but log at `Error` and replace the misleading comment — it currently claims *"the handler is responsible for translating its own failures"*, which **Spec §14.3** records as an intended contract no code enforced. The new comment must say this is a last resort and that a fault reaching it means the dispatcher itself faulted.
  3. Requires threading an `ILogger`/`ILoggerFactory` into `StdioTransport` — add it as an **optional** constructor parameter so existing test construction still compiles.
  ⚠️ **Nothing may be written to `Console.Out`** — that corrupts the protocol stream, and `StdoutPurityTests` asserts it.
- **Acceptance:** Task 19 fully green. `StdoutPurityTests` still green. Build warning-free.

---

# Deliverable D2 — Configuration bootstrap

Spec **§6.5**, **§1.3 (O-5)**, **§12 (E-12, E-13)**.

A vanilla `agency-acp` hard-fails `session/new` with *"Agent:DefaultModel is not configured"* because the build output contains no `appsettings.json`. Huddle worked around it with per-profile environment overrides; nobody else can start the process.

---

### Task 21 — (Test) A vanilla host resolves a default model

- **Goal:** Pin **O-5** from **Spec §6.5**.
- **Read first:** `src/Acp/Agency.Acp/Agency.Acp.csproj` — **no `Content` items**. `src/Acp/Agency.Acp/Program.cs` — `BuildHost` (made `internal` for testability) and its hardcoded `Skills:DisableShellExecution` source. `src/Acp/Agency.Acp.Test/V1GuaranteeTests.cs` for how `BuildHost` is driven from tests. **Spec §6.5**.
- **Deliverable:** Create `src/Acp/Agency.Acp.Test/ConfigurationBootstrapTests.cs`. With **no** `Agent__*` environment variables set (clear any within the test), resolve `IOptions<AgentOptions>` from `Program.BuildHost()` and assert `DefaultClientName` and `DefaultModel` are both non-empty, and that at least one `LLmClients` entry exists.
  ⚠️ **Do not leak environment mutations across tests** — save and restore any variable you clear, in a `finally`.
- **Acceptance:** Compiles and fails — no configuration is loaded. RED.

### Task 22 — (Implementation) Ship default configuration

- **Goal:** Implement **Spec §6.5**'s defaults table.
- **Read first:** The test from Task 21. `src/Harness/Agency.Harness.Console/Agency.Harness.Console.csproj` for the established `Content`/`CopyToOutputDirectory` pattern. **Spec §6.5**, **§12 (E-13)**.
- **Deliverable:**
  1. Create `src/Acp/Agency.Acp/appsettings.json` with exactly the keys in **Spec §6.5**'s table: `Agent:DefaultClientName = "local"`, a documented `Agent:DefaultModel` placeholder, one `Agent:LLmClients[0]` entry named `local` of OpenAI-style at `http://localhost:1234/v1`, and `Skills:DisableShellExecution = true`.
     **No vendor-specific endpoint may be on the required path** (base spec **P2**) — `localhost` is the generic local-server default, not a named product.
  2. Add to `Agency.Acp.csproj`:
     ```xml
     <ItemGroup>
       <Content Include="appsettings.json" CopyToOutputDirectory="PreserveNewest" />
     </ItemGroup>
     ```
  3. Do **not** change the configuration source order in `Program.BuildHost` — environment variables must continue to win (Task 23 asserts this).
- **Acceptance:** Task 21 green. Build warning-free.

### Task 23 — (Test) Environment overrides the shipped file

- **Goal:** Protect Huddle's existing integration — **Spec §6.5 (Implementation notes — layering)**.
- **Read first:** **Spec §6.5**. Huddle supplies `Agent__DefaultModel` via per-profile `EnvironmentOverrides`; inverting precedence would break them silently.
- **Deliverable:** In `ConfigurationBootstrapTests.cs`, set `Agent__DefaultModel` to a distinctive value, build the host, and assert the resolved `AgentOptions.DefaultModel` equals the environment value, **not** the file value. Restore the variable in a `finally`.
- **Acceptance:** Green. Load-bearing proof: temporarily move the environment-variables source **before** the JSON source in `BuildHost`, confirm this test reddens, restore.

### Task 24 — (Test) The shell-execution lock cannot be defeated by the config file

- **Goal:** Pin **Spec §12 (E-13)** — shipping a config file must not create a route to unlocking a v1 safety lock.
- **Read first:** `src/Acp/Agency.Acp/Program.cs` — the hardcoded, non-overridable in-memory source setting `Skills:DisableShellExecution = true`. `src/Acp/Agency.Acp.Test/V1GuaranteeTests.cs` — assertion #4. **Spec §12 (E-13)**, base spec **§6.5 (the six locks)**.
- **Deliverable:** In `ConfigurationBootstrapTests.cs`, add a test that supplies `Skills__DisableShellExecution=false` **via environment** and asserts the resolved value is still `true` — the hardcoded source must win over both file and environment.
- **Acceptance:** Green. If it fails, **stop and report** — that is a v1 guarantee regression and takes priority over this deliverable.

### Task 25 — Verify the package smoke gate still holds

- **Goal:** Confirm adding content did not break packaging (**Spec §6.5 — Packaging**).
- **Read first:** `src/Directory.Build.targets` — the `PackageId = AgencyDotNet$(…)` rule. The D6 smoke gate in the base plan verified `dotnet pack` emits `lib/net10.0/Agency.Acp.dll`.
- **Deliverable:** Run `dotnet pack src/Acp/Agency.Acp/Agency.Acp.csproj -c Release` and confirm the produced `.nupkg` still contains `lib/net10.0/Agency.Acp.dll`. Report whether `appsettings.json` appears in the package and whether that is desirable — **do not change packaging behaviour without reporting first**.
- **Acceptance:** `dotnet pack` succeeds; `lib/net10.0/Agency.Acp.dll` present. Findings reported.

---

# Deliverable D3 — End-to-end

Spec **§13**, **§1.3 (O-1, O-3)**, **§15 (T-17)**.

### Task 26 — (Functional) A Persona identity reaches a live model

- **Goal:** Prove **Spec §13** at the protocol boundary — the Agency half of the joint milestone.
- **Read first:** `src/Acp/Agency.Acp.Test/Functional/EndToEndAcpTests.cs` — the existing fixture, trait pattern and scripted `dotacp.client` usage. **Spec §13**, **Appendix B**.
- **Deliverable:** Add a test to `EndToEndAcpTests.cs` with `[Trait("Category", "Functional")]` and `[Trait("Category", "RequiresLlm")]`. Drive the real `agency-acp` process: `session/new` carrying `_meta.systemPrompt` with a **distinctive nonce token** in the identity (e.g. *"You always begin your reply with the token ABC123."*), then `session/prompt`. Assert:
  (a) the reply reflects the identity (the nonce appears), proving it reached the model;
  (b) a **second** session with a different nonce does not see the first's (**O-3**);
  (c) no filesystem or shell tool appears in any `tool_call` title.
  ⚠️ **Model-dependent assertion.** A small model may not follow an instruction reliably. If (a) proves flaky, **do not weaken it into something vacuous** — prefer a stronger instruction, and if it still fails, report honestly that identity delivery is proven at the prompt boundary by Task 9 while model *adherence* is not assertable. Spec **§12 (E-14)** records that there is no reliable signal for model instruction-following.
- 🔴 **Inference rules:** model **`google/gemma-4-e2b` only**; **at most 2 concurrent inferences**; always `-- RunConfiguration.MaxCpuCount=1`; endpoint is the active caching proxy configured in `src/shared-test-appsettings.json`. Never leave an `agency-acp` process running — kill it in a `finally`.
- **Acceptance:** Green with `dotnet test --filter "Category=Functional" -- RunConfiguration.MaxCpuCount=1`. No orphaned process.

---

# Deliverable D4 — Specification and documentation

Spec **§14.6**. **Not optional.**

The defect existed because a hop between two tasks was written down nowhere: D-3 specified the harness end, §8.1 specified `session/new`, and the line joining them belonged to neither. Fixing only the code leaves both ends still unnamed.

### Task 27 — Spec §8.1 gains the parse step

- **Goal:** Make identity parsing part of `session/new`'s documented contract (**Spec §14.6**).
- **Read first:** `docs/specs/Agency.Acp-Specifications.md` §8.1 (the numbered 1–8 algorithm). **Spec §14.6**.
- **Deliverable:** Insert a step **1a** — *parse `_meta.systemPrompt` → `identityPrompt` (append semantics; see PersonaIdentity spec §6.1)* — before step 4, and thread `identityPrompt` through steps 6 and 8.
- **Acceptance:** §8.1 names the producer of `QueryContext.IdentityPrompt`.

### Task 28 — Spec §6.9 (D-3) names its consumer

- **Goal:** Close the other end of the omission (**Spec §14.6**).
- **Read first:** `docs/specs/Agency.Acp-Specifications.md` §6.9, delta **D-3**.
- **Deliverable:** Add to D-3's row/notes: *"Supplied by the ACP adapter from `_meta.systemPrompt`; see §8.1 step 1a."*
- **Acceptance:** D-3 no longer describes a harness feature with no named consumer.

### Task 29 — Spec §6.7 loses `_meta.model`

- **Goal:** Remove the documented path for a field no client sends (**Spec §6.2**, **§14.6**).
- **Read first:** `docs/specs/Agency.Acp-Specifications.md` §6.7 and §2.1; search for `_meta` and for any claim that a model may be requested at `session/new`.
- **Deliverable:** Document `session/set_config_option` as the **sole** model-selection path. Note that `NewSessionRequest` carries no model field.
- **Acceptance:** No `_meta.model` reference survives in the base spec.

### Task 30 — `Agency.Acp.md` gains an Identity section

- **Goal:** Close the docs gap mirroring the code gap (**Spec §14.6**).
- **Read first:** `docs/Projects/Agency.Acp.md` — it currently does **not** mention `IdentityPrompt` at all.
- **Deliverable:** Add an **Identity** section covering: the `_meta.systemPrompt` channel; both accepted shapes; **append semantics and why a replace is refused** (**Spec §14.1** — a replace deletes the progressive-discovery instruction); identity present on every iteration; and the per-session, not per-turn, scope (**Spec §14.4**).
- **Acceptance:** A reader can integrate a Persona from this page without reading the spec.

### Task 31 — `Agency.Acp.md` documents configuration

- **Goal:** Document the shipped defaults and precedence (**Spec §6.5**).
- **Read first:** `docs/Projects/Agency.Acp.md` (Prerequisites section); the new `appsettings.json`.
- **Deliverable:** Document the shipped defaults, that environment variables override them (`Agent__DefaultModel` etc.), and that `Skills:DisableShellExecution` is **hardcoded on** and cannot be relaxed by configuration (**Spec §12 E-13**).
- **Acceptance:** An operator can run `agency-acp` and know how to point it at their own endpoint.

---

## Sequencing summary

| Deliverable | Tasks | Depends on | Blocking? |
|---|---|---|---|
| **D0** Identity path | 1–13 | — | **yes — the joint milestone** |
| **D1** Error contract | 14–20 | — | no (mitigated client-side) |
| **D2** Config bootstrap | 21–25 | — | no (worked around client-side) |
| **D3** End-to-end | 26 | D0 | no |
| **D4** Docs & spec | 27–31 | D0 | **yes — §14.6** |

**Critical path:** D0 → D3. D1 and D2 are independent and may run in parallel with D0 **only if** a different agent owns them — they touch `MethodDispatcher.cs` and `Program.cs`, which D0 also touches, so concurrent edits will collide.

## Regression gate

The entire suite — unit **and** functional — must stay green: **24 assemblies, 1,896 tests, 0 failures, 0 build warnings**, run with `-- RunConfiguration.MaxCpuCount=1`.

**`PublicAPI.Unshipped.txt` must contain zero `*REMOVED*` entries** when this work lands (Task 12 guards it automatically). Its presence means an optional parameter was used instead of an overload (**Spec §14.2**) and the change must be reworked even though it compiles and passes.

**Known flaky, do not chase:** `Agency.Harness.Test.Looping.LoopObservabilityTests` intermittently fails from static `Meter`/`MeterListener` state bleeding across tests; it passes in isolation and in a single-assembly run.
