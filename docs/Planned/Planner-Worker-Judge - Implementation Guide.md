# Planner-Worker-Judge — Implementation Guide (Functional Spec, Code-Grounded)

> **What this is.** An implementation-ready extraction of the AAT-26 *Planner-Worker-Judge* (PWJ)
> spec (`docs/spec.md`), re-grounded against the **current** `Agency.Harness` codebase. The original
> spec is a sound product/engineering design but has drifted: its `file:line` references predate the
> permissions and skills work, and a few of its "reuse what exists" claims are thinner or differently
> located than stated. This document is what you actually build from — it keeps the functional
> requirements, fixes the anchors, and adds the pointers the spec omits. Where the two disagree,
> **this document wins**; consult `Planner-Worker-Judge - Implementation Guide.md` only for the long-form rationale (§14 there is still
> worth reading).
>
> **Status of the feature in code today: not started.** No `Agency.Harness.Hierarchy` namespace, no
> Planner/Worker/Judge/orchestrator types, no Plan/Verdict records, no `hierarchy.*` telemetry. The
> *foundation* it composes on, however, is ~90% present and in places richer than the spec assumed.

---

## Why Planner-Worker-Judge? (Business case & essence)

**The essence, in one sentence:** instead of asking a single model to "plan, do, and check" inside one
flat tool loop, PWJ splits those into three cooperating roles — a **Planner** that decomposes the
objective, **Workers** that execute the pieces, and a **Judge** that verifies the result before it is
accepted — so a complex objective is handled with *structure*, *specialization*, and *assurance*
instead of hope.

**The problem it solves.** Today the harness offers one flat loop (`Agent`) plus opportunistic
delegation (`subagent_tool`). That is excellent for "do a task with tools," but on multi-step
objectives it degrades in three predictable ways:

- the model must hold the *entire* plan in working memory while also executing it;
- there is no verification gate — a wrong result flows straight to the user; and
- cost is unbounded by structure: one strong, expensive model does everything.

**How the three roles fix that:**

| Role | What it does | Why it pays off |
|---|---|---|
| **Planner** (strong tier) | Decomposes the objective into an ordered plan *before* any execution | Concentrates long-horizon reasoning in one good model call instead of smearing it across a flat loop |
| **Worker** (cheap tier) | Executes one step as a scoped, isolated subagent | Cheap, parallelizable, replaceable — this is where the cost savings live |
| **Judge** (mid tier) | Scores the plan/output against acceptance criteria → accept / revise / escalate | Turns "hope it worked" into a verifiable gate that catches bad output *before* it propagates |

**The benefits, distilled:**

- **Higher multi-step success rate.** An explicit plan plus a verification gate is the combination the
  literature consistently shows lifts completion on complex objectives.
- **Lower cost.** Pushing execution onto a *cheap* worker tier — while only the planner and judge use
  stronger models, and only O(attempts), not O(steps) — targets the ~20–30% token savings versus a
  flat strong-model tool loop. (Real only when the tiers actually differ; in the default local config
  they may not — see §3.3.)
- **Assurance, not hope.** Failed verification produces *structured feedback* and a bounded revision,
  or an explicit `Escalate` a host can act on — never a silent retry and never an unbounded loop.
- **Bounded by construction.** Every loop has a hard ceiling (revision budget + token/cost stop
  conditions), so worst-case cost is computable rather than open-ended.
- **Observable by default.** Plan, dispatch, and verdict are typed events with per-role telemetry, so a
  host renders progress live and a dashboard slices cost by role.

**What it deliberately is *not*.** It is not a new engine — every role is the *existing* `Agent` loop,
composed (compose, don't fork). It is opt-in and pay-for-what-you-use: trivial objectives short-circuit
to a single worker turn with no three-role tax, so hosts keep plain `Agent` for simple tasks and reach
for `PlannerWorkerJudge` only when an objective is genuinely multi-step. The trade-off accepted is
**latency for assurance** — the extra round-trips (plan, verify) buy verification and cost control on
exactly the objectives that warrant them.

---

## 0. Read this first — pointers the spec gets wrong or omits

These are the traps. Internalize them before writing code.

1. **`AgentFactory` is now public in the harness library.**
   `src/Harness/Agency.Harness/Agents/AgentFactory.cs` + `IAgentFactory.cs`, namespace `Agency.Harness`,
   `public sealed`. (It was `internal` in the Console host when AAT-26 was drafted, and earlier drafts
   of this guide warned about that — it has since moved into the library, which *removes* the trap.)
   This is your construction seam:
   - **Planner & Judge** need only an `IChatClient` for their tier — get it from
     `Models.CreateChatClient(clientName)` (`Models.cs:122`, returns `(IChatClient, clientType)`) and
     make a single `GetResponseAsync` with no tools.
   - **Workers** need a full `Agent` — call `IAgentFactory.CreateAgent(clientName, model)`
     (`Agents/IAgentFactory.cs`) and supply the scoped `ToolContext` yourself when building the
     `ChatSession`. Note `CreateAgent` returns *only* an `Agent`; it does **not** bundle a tool
     registry. `AgentTool` keeps its own delegate
     `Func<string?, string?, (AgentOptions, Agent, IToolRegistry)>` (`Tools/AgentTool.cs:13`) precisely
     because it needs the registry bundled — reuse that delegate for workers if you want one call.

   The orchestrator stays in `Agency.Harness` and depends on `IAgentFactory` / `Models` directly — no
   host dependency.

2. **`FakeChatClient` exists — but the file is misleadingly named.** The class the spec's §17 test
   plan calls `FakeChatClient` is real and correct: `src/Harness/Agency.Harness.Test/Fakes/FakeLlmClient.cs`
   (the *file* is `FakeLlmClient.cs`, the *class* is `FakeChatClient`). API: `EnqueueResponse(ChatResponse)`
   FIFO, plus `GetResponseCallCount`, `ReceivedSystemPrompts`, `ReceivedMessages`. Reuse it verbatim;
   do not write a new fake.

3. **There is no structured-output parser to reuse.** The spec (§6.1, §7.1, §17 IMPL-MODEL-1)
   implies an `AgentLlmResponse` / `AgentJsonContext` source-gen parser exists. `AgentJsonContext.cs`
   is trivial — it only serializes `Dictionary<string,object?>` (`AgentJsonContext.cs:5`). There is
   **no** `AgentLlmResponse` type anywhere. You are building plan/verdict JSON parsing **from
   scratch**, including new `JsonSerializerContext`s (`PlanJsonContext`, `VerdictJsonContext`). Budget
   for it.

4. **Stale line numbers throughout the spec.** Permissions + skills landed after it was written, so
   the loop grew. Corrected anchors (verified):
   - Parallel tool dispatch / `Task.WhenAll`: spec says `Agent.cs:354-424` → now **`Agent.cs:689` (per-call lambda) … `:876` (`Task.WhenAll`)**.
   - `OnPreToolUse` seam (for the gate-judge): spec says `Agent.cs:362-378` → now **`Agent.cs:699-806`**.
   - Turn-timeout pattern (`CreateLinkedTokenSource`+`CancelAfter`): spec says `Agent.cs:137-143` → now in `ChatAsync` (`Agent.cs:172` onward).
   - Worker body to extract: spec says `AgentTool.cs:41-87` → now **`AgentTool.cs:45-120`** (it grew a permission auto-deny loop, §1 below).

5. **The permission model changed the worker contract.** `AgentTool.InvokeAsync` now contains a
   park/auto-deny loop (`AgentTool.cs:69-117`): a subagent that hits an unresolved permission **auto-
   denies every pending request and resumes**, because sub-agents can't prompt a human. Your
   `WorkerRunner` extraction must preserve this loop — workers inherit "rules-only, auto-deny"
   behavior. (See `docs/Consent at the Tool Boundary - The Permission Model.md` §9.)

6. **Gate-judge is now even cleaner than the spec thought.** `PreToolUseDecision` gained an `Ask`
   variant (`Hooks/PreToolUseDecision.cs`). Your `Verdict → PreToolUseDecision` adapter maps
   `Accept→Allow`, `Revise/Escalate→Deny(reason)` and can optionally use `Ask` to escalate to the
   user. Deny-wins precedence already holds across hooks (`Deny > Ask > Rewrite > Allow`).

`★ Architectural note:` the orchestrator is a **harness-library** type. Its construction seam is now
fully in-library — `IAgentFactory.CreateAgent` for worker `Agent`s and `Models.CreateChatClient` for
planner/judge `IChatClient`s — alongside `ChatSession`, `AgentEvent`, `StopConditions`, `AgentHooks`,
`PreToolUseDecision`, `IToolRegistry`, and `Context`. It must still compile without referencing
`Agency.Harness.Console` — now trivially satisfied. Keep that boundary intact.

---

## 1. Where the code goes

New folder `src/Harness/Agency.Harness/Hierarchy/`, namespace `Agency.Harness.Hierarchy`:

| File | Contents |
|---|---|
| `Plan.cs`, `PlanStep.cs`, `StepResult.cs`, `Verdict.cs` | Domain records (§4) |
| `HierarchyEvents.cs` | New `AgentEvent` subtypes (§4.2) |
| `PlanJsonContext.cs` | Source-gen JSON contexts for plan + verdict |
| `IPlanner.cs` / `Planner.cs` / `PlannerPromptBuilder.cs` | Planner role (§3.1) |
| `WorkerRunner.cs` | Shared worker body extracted from `AgentTool` (§3.2) |
| `IJudge.cs` / `Judge.cs` / `JudgePromptBuilder.cs` / `JudgeGateAdapter.cs` | Judge role + gate adapter (§3.3) |
| `HierarchyOptions.cs` | Config record (§7) |
| `PlannerWorkerJudge.cs` | Orchestrator (§3.4, §5) |

Tests mirror under `src/Harness/Agency.Harness.Test/Hierarchy/`. Test visibility via the existing
`InternalsVisibleTo("Agency.Harness.Test")` (already configured — see other `internal` types like
`ToolHelpTool`). **Public** surface = what a host consumes (`PlannerWorkerJudge`, `HierarchyOptions`,
the records, the events, `IPlanner`/`IJudge`); everything else `internal`.

Host wiring (a separate, smaller task) goes in `Agency.Harness.Console` — register `HierarchyOptions`
from config and resolve the orchestrator with the DI-registered `IAgentFactory` (now a library service).

---

## 2. What you can reuse, verbatim (the foundation map)

| Need | Reuse | Anchor |
|---|---|---|
| Construct a role `Agent` per (clientName, model) | **`IAgentFactory.CreateAgent`** (public, in-library) | `Agents/IAgentFactory.cs`, `Agents/AgentFactory.cs:31` |
| Spawn a child *worker* (agent + tools bundled) | `AgentTool`'s delegate `Func<…,(AgentOptions,Agent,IToolRegistry)>` + `ChatSession` | `Tools/AgentTool.cs:13`, `:60-62` |
| Run a child to completion, collapse events → result | `AgentTool.InvokeAsync` body (extract it) | `Tools/AgentTool.cs:45-120` |
| Planner/Judge `IChatClient` per tier | `Models.CreateChatClient(clientName)` → `(IChatClient, clientType)` | `Models.cs:122` |
| Bounded loops | `StopConditions` (`StepCountIs`, `BudgetExceeded`, `TokensExceeded`, `Any`) | `StopConditions.cs:20-37` |
| Token/cost accounting | `LlmTokenUsage`, `Context.TotalUsage/TotalCostUsd` | `AgentEvents.cs`, `Contexts/Context.cs` |
| Gate-judge seam | `AgentHooks.OnPreToolUse` → `PreToolUseDecision.{Allow,Deny,Rewrite,Ask}` | `Hooks/PreToolUseDecision.cs`, `Agent.cs:699-806` |
| Event stream contract | `public abstract record AgentEvent`; terminal `AgentResultEvent` + `AgentResultStatus` | `AgentEvents.cs` |
| Telemetry pattern | static `ActivitySource`/`Meter` + counters/histograms + `TagList` | `Agent.cs:22-47`, `:811`, `:843` |
| Test double | `FakeChatClient.EnqueueResponse(...)` | `Fakes/FakeLlmClient.cs` |
| Per-run timeout | linked CTS + `CancelAfter` | `Agent.ChatAsync` (`Agent.cs:172+`) |

`AgentResultStatus` (reuse, do not redefine) = `{ Success, MaxStepsReached, BudgetExceeded, Error, AwaitingPermission }`.

---

## 3. Functional requirements per component

### 3.1 Planner (`IPlanner` / `Planner`)

**Does:** objective + tool catalogue → structured `Plan`. Pure reasoning — **no tool loop**.

- Build the planner system prompt with a new pure-static `PlannerPromptBuilder.Build(objective, ToolContext, HierarchyOptions)`. It must list available tools from `ctx.Tools.Registry.ListDefinitions()` (so the planner knows what workers can do) and instruct the model to emit **only** JSON matching the `Plan` schema.
- One `IChatClient.GetResponseAsync` call with `ChatOptions { ModelId = PlannerModel, Instructions = prompt }` and **`Tools = null`** (planner does not act). Use the planner-tier client.
- Parse assistant text → `Plan` via `PlanJsonContext`. On `JsonException`: **one** bounded re-prompt ("return only JSON matching this schema"); a second failure surfaces a parse failure the orchestrator turns into `Escalate` (§6, E-2).
- Set `Plan.IsTrivial = true` for single-step/direct-answer objectives so the orchestrator can fast-path (§2.4).
- `RevisePlanAsync(plan, feedback, ct)`: re-invoke with the prior plan + judge feedback appended.
- Cap plan size at `HierarchyOptions.MaxSteps` (default 12).

**Out of scope:** tool execution, DAG dependencies (`PlanStep.DependsOn` is a forward-compat field, V1-unused).

### 3.2 Worker (`WorkerRunner`)

**Does:** execute exactly one `PlanStep` → one `StepResult`.

- **Extract** the body of `AgentTool.InvokeAsync` (`AgentTool.cs:45-120`) into a reusable
  `WorkerRunner.RunStepAsync(PlanStep, ToolContext, ct)`. Then make `AgentTool` a thin caller of it
  so there is **one** "run a child agent" code path (spec §6.2, T-WORK-3 guards no behavior change).
- Must preserve the permission **auto-deny/resume loop** (`AgentTool.cs:69-117`): workers are
  rules-only and cannot prompt a human.
- Build a `ChatSession` from the injected factory delegate, `await foreach` its events, capture the
  terminal `AgentResultEvent.FinalText` + `Status`, and collapse to
  `StepResult { StepId, Output, Status, Usage }`.
- A failed/truncated worker (`AgentResultStatus.Error`) yields `StepResult.Status = Error` — do not
  throw; let the judge route it (§6, E-5).
- Each worker gets a **fresh `Context`** (no shared history; smaller prompts, no cross-contamination).
  A worker sees only its step instruction (+ injected `KnowledgeContext` if you choose to seed it),
  never the whole plan.

`★ Concurrency trap:` parallel workers against the local LM Studio endpoint must be capped at **2**
(the AMD 8060S iGPU hard-crashes above 2 concurrent inference slots — this is a known, reproduced
failure, not a guess). Gate worker spawning with `SemaphoreSlim(MaxConcurrency)`; default 2 for local,
higher for hosted. See `HierarchyOptions.MaxConcurrency` (§7).

### 3.3 Judge (`IJudge` / `Judge`)

**Does:** score a plan or an aggregate `StepResult[]` against a rubric → `Verdict`. Never edits output.

- `JudgePromptBuilder.Build(objective, rubric, subject, attempt)` (pure static). Embed the objective,
  the unit under review, and `HierarchyOptions.Rubric`.
- One `GetResponseAsync` (no tools in V1 — "agent-as-judge" with read-only verification tools is V2),
  judge-tier client. Parse JSON → `Verdict` via `VerdictJsonContext`.
- Unparseable verdict → treat as `Revise("judge output unparseable")` **once**; second failure →
  `Escalate` (§6, E-7).
- **Gate-judge adapter** (`JudgeGateAdapter`): map `Verdict → PreToolUseDecision`
  (`Accept→Allow`, `Revise/Escalate→Deny(reason)`), exposing an `AgentHooks.OnPreToolUse` delegate so
  a judge can block off-plan/unsafe worker tool calls inline with **zero loop changes** (same shape as
  `BlockListHooks.Dangerous`).

`★ Bias mitigation is a hard requirement, cheaply met:` the judge model **should differ** from the
worker model (self-preference is the strongest, most reproducible judge bias). Enforce distinct tiers
via `HierarchyOptions` and **log a warning when `JudgeModel == WorkerModel`**. Note for this repo: per
the model-tier reality, all configured tiers may currently point at the *same* local model — so the
warning will fire in the default local config. That's expected; document it, don't suppress it.

### 3.4 Orchestrator (`PlannerWorkerJudge`)

**Does:** own plan → execute → verify → (bounded) revise; emit the typed event stream; aggregate
usage/cost; produce the terminal `AgentResultEvent`.

- Signature: `IAsyncEnumerable<AgentEvent> RunAsync(string objective, Context ctx, HierarchyOptions options, CancellationToken ct)`.
- Single in-flight run per instance (mirror `ChatSession`'s non-thread-safe contract).
- Reject empty/whitespace objective with `ArgumentException` **before** planning (§6, E-1).
- Implements the loop in §5. Mutates only counters/usage on `Context`; everything else `init`-only
  (mirror the existing `Context` contract).

---

## 4. Data model

All `sealed record`, file-scoped namespace `Agency.Harness.Hierarchy`, nullable enabled, `///` docs,
warning-free (`TreatWarningsAsErrors=true`).

```csharp
public sealed record Plan
{
    public required IReadOnlyList<PlanStep> Steps { get; init; }
    public bool    IsTrivial { get; init; }
    public string? Rationale { get; init; }
}

public sealed record PlanStep
{
    public required string StepId        { get; init; }   // "s1"
    public required string Instruction   { get; init; }   // worker prompt
    public string?         ParallelGroup { get; init; }   // same group → concurrent
    public IReadOnlyList<string> DependsOn { get; init; } = [];  // V1-unused (DAG = V2)
    public string?         ClientName    { get; init; }   // optional per-step override
    public string?         Model         { get; init; }
}

public sealed record StepResult
{
    public required string            StepId { get; init; }
    public required string            Output { get; init; }
    public required AgentResultStatus Status { get; init; }   // reuse existing enum
    public required LlmTokenUsage     Usage  { get; init; }   // reuse existing record
}

public abstract record Verdict
{
    public sealed record Accept                                                   : Verdict;
    public sealed record Revise(string Feedback, string? TargetStepId = null)      : Verdict;
    public sealed record Escalate(string Reason)                                  : Verdict;
    private Verdict() { }   // closed union
}
```

### 4.2 New events (extend `AgentEvent`)

```csharp
public sealed record PlanCreatedEvent(Plan Plan, int Attempt)                                       : AgentEvent;
public sealed record StepDispatchedEvent(PlanStep Step)                                             : AgentEvent;
public sealed record StepCompletedEvent(StepResult Result)                                          : AgentEvent;
public sealed record VerdictEvent(string SubjectId, Verdict Verdict, int Attempt)                   : AgentEvent;
public sealed record EscalationEvent(string Reason, Plan Plan, IReadOnlyList<StepResult> Partial)   : AgentEvent;
// Existing AgentResultEvent still closes every run.
```

Hosts already `switch` over `AgentEvent` (e.g. `AgentTool.cs:80-103`, the Console renderer), so adding
subtypes is non-breaking — unknown cases fall through.

---

## 5. Orchestration algorithm (authoritative)

```text
RunAsync(objective, ctx, options, ct):
  guard: objective non-empty else throw ArgumentException
  yield SessionStartedEvent(runId)

  attempt = 0
  plan = Planner.CreatePlanAsync(objective, ctx, ct)
  yield PlanCreatedEvent(plan, attempt)

  # Fast path — trivial objective never pays the 3-role tax
  if plan.IsTrivial or plan.Steps.Count == 1:
      result = WorkerRunner.RunStepAsync(plan.Steps[0], ctx, ct)
      yield StepCompletedEvent(result)
      yield AgentResultEvent(result.Status, result.Output, ctx.TotalUsage, ctx.TotalCostUsd)
      return

  # Empty plan → escalate
  if plan.Steps.Count == 0:
      yield EscalationEvent("planner produced no steps", plan, [])
      yield AgentResultEvent(Error, "no steps", ctx.TotalUsage, ctx.TotalCostUsd); return

  while true:
      ct.ThrowIfCancellationRequested()

      if options.VerifyPlan:
          v = Judge.VerifyPlanAsync(objective, options.Rubric, plan, attempt, ct)
          yield VerdictEvent(planId, v, attempt)
          match v:
            Revise r when attempt < options.MaxRevisions:
                attempt++; plan = Planner.RevisePlanAsync(plan, r.Feedback, ct)
                yield PlanCreatedEvent(plan, attempt); continue
            Escalate e:
                yield EscalationEvent(e.Reason, plan, []); yield AgentResultEvent(Error, e.Reason, ...); return
            _: pass  # Accept, or Revise with budget exhausted → proceed/handle below

      # execute, honoring parallel groups; SemaphoreSlim(MaxConcurrency) gates spawning
      results = []
      foreach group in GroupBy(plan.Steps, s => s.ParallelGroup):     # null group = singleton
          groupResults = await Task.WhenAll(group.Select(s => DispatchAsync(s, ctx, ct)))
          results.AddRange(groupResults)        # DispatchAsync yields StepDispatched + StepCompleted

      verdict = Judge.VerifyResultAsync(objective, options.Rubric, results, attempt, ct)
      yield VerdictEvent(runId, verdict, attempt)

      match verdict:
        Accept:
            final = Synthesize(results, options.Synthesis)   # PlannerMerge (LLM) or Concatenate (free)
            yield AgentResultEvent(Success, final, ctx.TotalUsage, ctx.TotalCostUsd); return
        Revise r when attempt < options.MaxRevisions:
            attempt++; plan = Planner.RevisePlanAsync(plan, r.Feedback, ct)
            yield PlanCreatedEvent(plan, attempt); continue
        Revise:                                  # budget exhausted
            yield AgentResultEvent(MaxStepsReached, BestEffort(results), ...); return
        Escalate e:
            yield EscalationEvent(e.Reason, plan, results); yield AgentResultEvent(Error, e.Reason, ...); return

      # global guards (reuse StopConditions semantics)
      if options.Budget exceeded or options.TokenBudget exceeded:
          yield AgentResultEvent(BudgetExceeded, BestEffort(results), ...); return
```

**Termination guarantee (the core correctness property, gets its own test):** every back-edge
increments `attempt`; the loop exits when `attempt >= options.MaxRevisions`. Independently,
`Budget`/`TokenBudget` force exit. There is **no** path that loops without incrementing a bounded
counter.

**Incremental verify (`options.VerifyEachStep`)**: verify each `StepResult` as it completes and emit
`Revise(feedback, targetStepId)` so only the failing step re-runs (cheaper revisions, more judge
calls). Same `Judge.VerifyResultAsync`, called per-step.

**Synthesis**: `PlannerMerge` (default — one planner call: "given these results, produce the final
answer") or `Concatenate` (free — join step outputs with headers). Config: `HierarchyOptions.Synthesis`.

---

## 6. Edge cases (must each have a test)

| # | Scenario | Expected |
|---|---|---|
| E-1 | Empty/whitespace objective | `ArgumentException` before planning |
| E-2 | Planner malformed JSON | one re-prompt → else `Escalate` |
| E-3 | Plan has 0 steps | `EscalationEvent` + `AgentResultEvent(Error)` |
| E-4 | Plan exceeds `MaxSteps` | truncate + warn, or `Revise` |
| E-5 | Worker step fails/truncates | `StepResult.Status=Error`; judge routes |
| E-6 | Judge always `Revise` | exits at `MaxRevisions` → `MaxStepsReached` + best-effort |
| E-7 | Judge unparseable | `Revise` once, then `Escalate` |
| E-8 | Cost/token budget hit mid-run | `BudgetExceeded` + partial output |
| E-9 | Cancellation mid-run | `OperationCanceledException` propagates; prior events already emitted |
| E-10 | Parallel step throws | `Task.WhenAll` surfaces; that step→`Error`, siblings complete |
| E-11 | Local concurrency > 2 | `SemaphoreSlim` caps at 2; never crashes iGPU |
| E-13 | Gate-judge denies every tool call | worker makes no progress → failed step → escalate |
| E-14 | Judge model == worker model | warn (fires in default local config — expected) |

---

## 7. `HierarchyOptions`

Bound from a new `"Hierarchy"` config section (mirror how `PermissionsOptions`/`HooksOptions` bind).

| Property | Type | Default | Notes |
|---|---|---|---|
| `PlannerClientName` / `PlannerModel` | `string?` | default client / strong | |
| `WorkerClientName` / `WorkerModel` | `string?` | default client / cheap | |
| `JudgeClientName` / `JudgeModel` | `string?` | default client / standard | **should differ from worker** |
| `MaxRevisions` | `int` | `2` | hard ceiling |
| `MaxSteps` | `int` | `12` | plan size cap |
| `MaxConcurrency` | `int` | `2` local / `8` hosted | LM Studio 2-slot limit |
| `VerifyPlan` | `bool` | `true` | judge the plan before spending worker tokens |
| `VerifyEachStep` | `bool` | `false` | incremental verify (§5) |
| `Rubric` | `string` | sensible default | injected into judge prompt |
| `Synthesis` | `enum { PlannerMerge, Concatenate }` | `PlannerMerge` | |
| `Budget` | `decimal?` | `null` | USD ceiling (reuses `BudgetExceeded` semantics) |
| `TokenBudget` | `long?` | `null` | total-token ceiling |
| `TurnTimeoutSeconds` | `int?` | `null` | per-run timeout (linked CTS) |

Add a `HierarchyOptionsValidator` that fails fast at startup on contradictory config (e.g.
`MaxRevisions < 0`, `MaxSteps < 1`), consistent with `PermissionsOptionsValidator` /
`HooksOptionsValidator`.

---

## 8. Observability

Mirror `Agent.cs:22-47` exactly: static `ActivitySource`/`Meter` named `Agency.Harness.Hierarchy`,
counters/histograms created once, `TagList` for tags. Add a `role` tag (`planner|worker|judge`).

| Signal | Name | Tags |
|---|---|---|
| Counter | `hierarchy.runs` | `status` |
| Counter | `hierarchy.plans` | `attempt` |
| Counter | `hierarchy.steps` | `role=worker`, `step.status` |
| Counter | `hierarchy.verdicts` | `verdict` (`accept`/`revise`/`escalate`) |
| Counter | `hierarchy.revisions` | — |
| Counter | `hierarchy.tokens` | `role`, `model`, `client_type`, `token.type` |
| Counter | `hierarchy.escalations` | `reason.kind` |
| Histogram | `hierarchy.run.duration` (ms) | `status` |
| Histogram | `hierarchy.role.duration` (ms) | `role`, `model`, `client_type` |

Activities: root `Hierarchy.RunAsync` with child spans `hierarchy.plan`, `hierarchy.step`,
`hierarchy.verify` (yields a plan→fan-out→verify trace waterfall).

---

## 9. Test plan (TDD, test-first) — mapped to existing infra

All unit tests drive **`FakeChatClient`** (`Fakes/FakeLlmClient.cs`) with `EnqueueResponse(...)` in
FIFO order — no live LLM. Functional E2E tagged `Category=Functional` against LM Studio
(`http://llm-host.example:1234`). Telemetry tests use a `MeterListener` (see existing telemetry tests
for the pattern). Keep functional prompts deterministic (pinned clock, fixed ids) so HTTP-cache replay
in CI stays stable — see `docs`/the CI notes on cassette key stability.

| Phase | Tests | Impl |
|---|---|---|
| 0 Domain | records + new events round-trip through source-gen `PlanJsonContext` (assert no reflection fallback) | records + contexts; warning-free |
| 1 Planner | valid JSON→N steps in order (preserve `ParallelGroup`); malformed→1 re-prompt then fail; trivial→`IsTrivial`; prompt includes `ListDefinitions()` catalogue | `Planner` + `PlannerPromptBuilder` |
| 2 Worker | `RunStepAsync` collapses stream→`StepResult` (status+output); failed/truncated→`Error`; **`subagent_tool` still passes its existing tests** after delegating to `WorkerRunner` | extract `WorkerRunner`; repoint `AgentTool` |
| 3 Judge | `Accept` parsed; `Revise` carries feedback+`TargetStepId`; unparseable→`Revise` once; gate adapter maps `Accept→Allow`, `Revise/Escalate→Deny` | `Judge` + `JudgePromptBuilder` + `JudgeGateAdapter` |
| 4 Orchestrator | happy path event order (`SessionStarted, PlanCreated, StepDispatched×2, StepCompleted×2, Verdict, AgentResult`); trivial fast path (1 call, no judge events); **termination** (always-Revise → `MaxStepsReached`); parallel group concurrency; budget guard→`BudgetExceeded`+partial; empty plan→`Escalation`; cancellation propagates | `PlannerWorkerJudge` + events |
| 5 Obs/Config | run emits `hierarchy.runs/verdicts/revisions` with expected tags (`MeterListener`) | wire telemetry; bind+validate `HierarchyOptions` |
| 6 E2E | `Category=Functional`: real 3-step objective → `Success`, `MaxConcurrency=2` honored, cost recorded | — |
| 7 Console (V1.1, optional) | renders `PlanCreatedEvent`/`VerdictEvent` | add render cases |

**Coverage target:** ≥90% line on the orchestration logic (spec §1 DoD).

---

## 10. Acceptance criteria (Definition of Done, V1)

- `PlannerWorkerJudge.RunAsync(objective, ctx, options, ct)` produces a final answer via
  plan → execute → verify → (bounded) revise, and short-circuits trivial objectives to ~1 LLM call.
- All three roles independently configurable to a (clientName, model) pair via `HierarchyOptions`.
- Loop bounded by `MaxRevisions` **and** token/cost `StopConditions`; provable termination test green.
- Typed `AgentEvent` stream surfaces every plan, dispatch, verdict, escalation; existing hosts keep working.
- OpenTelemetry parity with `Agent` (ActivitySource + Meter, counters + histograms, `role` tag).
- `subagent_tool` behavior unchanged after the `WorkerRunner` extraction (regression test green).
- Orchestrator compiles in `Agency.Harness` with **no** dependency on `Agency.Harness.Console`.
- ≥90% line coverage on orchestration logic + one `Category=Functional` E2E.

---

## 11. Explicitly out of scope (V1) — keep these as `// V2` seams

New `IChatClient`; forking the `Agent` loop; cross-process/remote workers; learned planners; a general
DAG engine (`PlanStep.DependsOn` stays a forward-compat field); human-in-the-loop UI (emit
`EscalationEvent`, host decides); persisting plans/verdicts (natural home later:
`Agency.KeyValueStore.*`, which exists, keyed by run id); multi-judge ensembles; exposing the
orchestrator **as** an `ITool` (`hierarchy_tool`) for recursive delegation — design `PlannerWorkerJudge`
so it *can* implement `ITool` later, but don't.

---

*Derived from `docs/spec.md` (AAT-26), re-anchored to the codebase as of this writing. If a `file:line`
no longer matches, trust the symbol name and search — the loop continues to evolve.*
