# AAT-26 — Planner-Worker-Judge Hierarchy

> **Status:** Defined · **Tag:** `llm` · **Target project:** `Agency.Harness`
> **Document type:** High-Level Design (HLD) / Product + Engineering Specification
> **Author role:** Senior Product Manager (spec), grounded in the existing `Agency.Harness` codebase
> **Source task:** [AAT-26 — Implement a "Planner-Worker-Judge" Hierarchy](https://www.notion.so/36b5b7b651d880f4b306ffcac5f5e069)
> **Anchors refreshed:** `file:line` references re-verified against the current codebase (post permissions + skills work). For the code-grounded build walkthrough — including the traps this spec glosses over — see [Planner-Worker-Judge - Implementation Guide](Planner-Worker-Judge%20-%20Implementation%20Guide.md).

---

## Table of Contents

1. [Goal](#1-goal)
2. [Example Queries / Use Cases](#2-example-queries--use-cases)
3. [Non-Goals](#3-non-goals)
4. [Design Principles](#4-design-principles)
5. [Architecture Overview](#5-architecture-overview)
6. [System Components / Pipeline](#6-system-components--pipeline)
7. [Data Model / State Layer](#7-data-model--state-layer)
8. [Core Algorithms / Processing Logic](#8-core-algorithms--processing-logic)
9. [Incremental vs Full Processing](#9-incremental-vs-full-processing)
10. [Background Workers / Async Components](#10-background-workers--async-components)
11. [Performance Expectations](#11-performance-expectations)
12. [Edge Cases and Failure Modes](#12-edge-cases-and-failure-modes)
13. [End-to-End Flow](#13-end-to-end-flow)
14. [Design Notes / Rationale](#14-design-notes--rationale)
15. [Configuration Reference](#15-configuration-reference)
16. [Observability Reference](#16-observability-reference)
17. [Implementation Plan (TDD, Test-First)](#17-implementation-plan-tdd-test-first)
18. [Open Questions](#18-open-questions)

---

## 1. Goal

Introduce a **Planner-Worker-Judge (PWJ) hierarchy** into `Agency.Harness` — a role-specialized
multi-agent orchestration that decomposes a single complex objective into three cooperating roles
running over the *existing* `Agent` loop and `IChatClient` abstraction:

| Role | One-line responsibility | Why it exists |
|------|-------------------------|---------------|
| **Planner** | Decompose the objective into an ordered `Plan` of discrete `PlanStep`s *before* execution begins. | Concentrates long-horizon reasoning in one strong-tier model call instead of smearing it across a flat tool loop. |
| **Worker** | Execute one `PlanStep` as a scoped subagent with its own model tier and `ToolRegistry`. | Cheap, parallelizable, replaceable; built directly on the existing `AgentTool` (`subagent_tool`) delegation primitive. |
| **Judge** | Evaluate plan validity and worker output against acceptance criteria, emitting a `Verdict` that drives accept / revise / escalate. | Catches invalid plans and bad output *before* they propagate, converting "hope it worked" into a verifiable gate. |

The deliverable is a new **orchestration layer** — not a new LLM client and not a fork of the agent
loop. It composes `Agent`, `ChatSession`, `AgentTool`, `AgentHooks`, and `StopConditions` that already
exist in `src/Harness/Agency.Harness`.

**Definition of done (V1):**

- A `PlannerWorkerJudge` orchestrator that, given an objective + `ToolContext`, produces a final
  answer by running plan → execute → verify → (bounded) revise.
- All three roles are independently configurable to a different model tier via the existing
  `LlmClientOptions` / `clientName` / `model` selection already used by `AgentTool`.
- The loop is bounded by a **revision budget** and the existing token/cost `StopConditions`.
- A typed `AgentEvent` stream surfaces every plan, dispatch, and verdict so console/HTTP hosts
  observe progress without polling.
- Full OpenTelemetry parity with `Agent` (ActivitySource + Meter, counters + histograms).
- ≥ 90% line coverage on the orchestration logic via `FakeChatClient`-driven unit tests, plus one
  functional E2E test gated behind `Category=Functional` against LM Studio.

**Why this matters:** Today `Agency.Harness` offers a single flat loop (`Agent`) plus opportunistic
delegation (`subagent_tool`). That works for "do a task with tools" but degrades on multi-step
objectives: the model must hold the whole plan in working memory, there is no verification gate, and
cost is unbounded by structure. PWJ adds *structure* (explicit plan), *specialization* (per-role model
tiers), and *assurance* (a judge gate) — the three things the literature consistently shows improve
multi-step success rate while cutting token spend.

---

## 2. Example Queries / Use Cases

### 2.1 Codebase refactor across multiple files

> "Rename `IEmbeddingGenerator.Generate` to `GenerateAsync` everywhere and update all call sites and tests."

- **Planner** emits steps: (1) locate interface + implementors, (2) rename method + signature,
  (3) update each call site, (4) update tests, (5) build + verify.
- **Workers** execute steps 2–4 in parallel where independent (per-file), each a cheap-tier subagent
  with `read_file` / `write_file` tools.
- **Judge** gates step 5: rejects if `dotnet build` output (returned as a tool result) contains errors,
  sending a revise verdict back to the planner with the compiler output as feedback.

### 2.2 Research-and-synthesize

> "Compare pgvector vs SQLite-vec for our RAG store and recommend one."

- **Planner**: (1) gather pgvector facts, (2) gather sqlite-vec facts, (3) build comparison table,
  (4) write recommendation.
- **Workers**: steps 1–2 run concurrently (read-only, web/doc tools).
- **Judge**: verifies the recommendation cites both sources and answers the actual question
  (anti-hallucination, completeness rubric).

### 2.3 Guarded operational task

> "Clean up stale branches in this repo."

- **Planner** produces a plan whose steps include destructive git operations.
- **Judge** runs as an **inline `OnPreToolUse` gate**: any `delete_branch` / shell `git branch -D`
  is verified against the plan's allow-list before it commits; off-plan deletions are `Deny`-ed.
- This is the *gate-judge* variant (vs. the *critic-judge* in 2.1/2.2).

### 2.4 Single-step objective (degenerate case)

> "What's the capital of France?"

- **Planner** returns a single-step plan (or `IsTrivial = true`); orchestrator short-circuits to a
  single worker turn with no judge loop. PWJ must not tax simple queries with three round-trips.

---

## 3. Non-Goals

| # | Non-goal | Rationale |
|---|----------|-----------|
| NG-1 | A new `ILlmClient` / `IChatClient` implementation. | PWJ orchestrates existing clients; it is provider-agnostic. |
| NG-2 | Replacing or forking the `Agent` loop. | Workers *are* `Agent`/`ChatSession` instances. The orchestrator sits above them. |
| NG-3 | Distributed / cross-process agents. | V1 is in-process `Task`-parallel only. Remote workers are V2+. |
| NG-4 | Learned / fine-tuned planners (RL, SFT on plan traces). | Out of scope; planner is prompt-driven in V1. |
| NG-5 | A general DAG workflow engine. | V1 plans are a **linear list with optional parallel groups**, not arbitrary graphs. |
| NG-6 | Human-in-the-loop UI. | The orchestrator *emits* an escalation event; building an approval UI is a host concern. |
| NG-7 | Persisting plans/verdicts to a store. | V1 keeps all state in-memory in `Context`. Durable plan storage is V2. |
| NG-8 | Multi-judge ensembles / debate. | V1 ships a single judge. Ensemble aggregation is a V2 extension point. |

---

## 4. Design Principles

1. **Compose, don't fork.** Every role is an existing `Agent` driven by an `IChatClient`. The
   orchestrator is glue, not a new engine. If a feature already exists (`StopConditions`,
   `AgentHooks`, `subagent_tool`), reuse it verbatim.
2. **Roles are model-tier boundaries.** Planner, Worker, and Judge each resolve to a (clientName,
   model) pair through the same factory `AgentTool` already uses. This is where cost savings live:
   a strong planner, cheap workers, a mid-tier judge.
3. **Verification is a gate, not an afterthought.** The judge runs *before* output is accepted into
   the final answer. Failed verification produces structured feedback, not a silent retry.
4. **Bounded by construction.** Every loop has an explicit ceiling: revision budget, step count,
   token budget, cost budget. There is no unbounded planner↔judge cycle.
5. **Observable by default.** Plans, dispatches, and verdicts are typed `AgentEvent`s. Telemetry
   mirrors `Agent` exactly (same tag vocabulary: `model`, `client_type`, `role`).
6. **Caller-owned state, loop-mutated counters.** Mirrors the existing `Context` contract — the
   orchestrator mutates only counters/usage; everything else is `init`-only and snapshot-friendly.
7. **Degrade gracefully to a single turn.** A trivial objective must cost ~1 LLM call, not 3+.
8. **Test-first.** Each role and the orchestrator are unit-tested against `FakeChatClient` with
   queued responses before implementation (see §17).

---

## 5. Architecture Overview

The PWJ orchestrator sits **above** the agent loop. Planner and Judge are thin `Agent` wrappers with
specialized system prompts and structured-output parsing. Workers are full `Agent`/`ChatSession`
instances spawned through the same **injected delegate** `AgentTool` uses today —
`Func<string?, string?, (AgentOptions, Agent, IToolRegistry)>`. (`AgentFactory`/`IAgentFactory` are now
**public in `Agency.Harness`** under `Agents/`, so the orchestrator can construct role agents via
`IAgentFactory.CreateAgent` directly; `AgentTool`'s delegate still bundles the worker's `IToolRegistry`.)

```
                          ┌─────────────────────────────────────────────────────────┐
                          │                  PlannerWorkerJudge                        │
                          │                  (orchestrator)                            │
                          │                                                            │
   objective ───────────▶│   ┌──────────┐   Plan    ┌───────────────┐                 │
   + ToolContext         │   │ Planner  │──────────▶│  Plan (steps) │                 │
   + HierarchyOptions    │   │ (Agent,  │           └───────┬───────┘                 │
                          │   │  Strong) │                   │                          │
                          │   └────┬─────┘                   │ dispatch (per step /     │
                          │        ▲                         │  parallel group)         │
                          │        │ revise verdict          ▼                          │
                          │        │              ┌────────────────────────┐            │
                          │        │              │   Worker pool          │            │
                          │        │              │  ┌──────┐  ┌──────┐     │            │
                          │        │              │  │Worker│  │Worker│ ... │            │
                          │        │              │  │(Agent│  │(Agent│     │            │
                          │        │              │  │ Cheap│  │ Cheap│     │            │
                          │        │              │  └──┬───┘  └──┬───┘     │            │
                          │        │              └─────┼─────────┼─────────┘            │
                          │        │                    │ StepResult(s)                  │
                          │        │                    ▼                                │
                          │   ┌────┴─────┐      ┌───────────────┐                        │
                          │   │  Judge   │◀─────│  StepResult   │                        │
                          │   │ (Agent,  │      └───────────────┘                        │
                          │   │  Std)    │── Verdict {Accept|Revise|Escalate} ──┐        │
                          │   └──────────┘                                       │        │
                          │        │ Accept                          Escalate    │        │
                          │        ▼                                    │         │        │
                          │   ┌───────────────┐                        ▼         │        │
                          │   │  Final answer │                 EscalationEvent  │        │
                          │   └───────┬───────┘                 (host decides)   │        │
                          └───────────┼────────────────────────────────────────┼────────┘
                                      │  AgentEvent stream (typed)               │
                                      ▼                                          ▼
                            host (Console / HTTP)                        human-in-the-loop

   Inline-gate judge variant (orthogonal): a Judge can also be wired as an
   AgentHooks.OnPreToolUse delegate on Worker agents, returning Deny / Rewrite
   to block off-plan or unsafe tool calls *before* they commit.
```

### 5.1 Two judge topologies

The literature splits "judge" into two distinct roles; the codebase already supports both seams, and
the spec keeps them **separately configurable**:

| Topology | Codebase seam | When it runs | Failure action | Cost |
|----------|---------------|--------------|----------------|------|
| **Critic judge** (default) | A `Judge` `Agent` invoked by the orchestrator after each step/plan. | After a `StepResult` is produced. | Emits `Verdict.Revise(feedback)` → loop. | One judge LLM call per verified unit. |
| **Gate judge** (opt-in) | `AgentHooks.OnPreToolUse` on Worker agents (reuses `Deny`/`Rewrite`). | Before a worker tool call commits. | `Deny(reason)` blocks the call inline. | Cheap/free if rule-based; an LLM call if model-backed. |

---

## 6. System Components / Pipeline

### 6.1 `IPlanner` / `Planner`

- **Purpose:** Turn a free-text objective + available tool catalogue into a structured `Plan`.
- **Responsibilities:**
  - Build a planning system prompt that lists available tools (`ToolContext.Registry.ListDefinitions()`)
    and instructs the model to emit a JSON plan.
  - Call the strong-tier `IChatClient` once (no tool loop — planning is pure reasoning).
  - Parse the JSON response into `Plan` (see §7). Reject/repair malformed JSON with one bounded retry.
  - Mark trivial objectives (`Plan.IsTrivial`) so the orchestrator can short-circuit.
- **Inputs:** `string objective`, `ToolContext`, `HierarchyOptions`.
- **Outputs:** `Plan` (ordered `PlanStep`s, optional parallel-group ids).
- **Internal flow:**
  1. `SystemPromptBuilder`-style assembly of a planner prompt (new `PlannerPromptBuilder`, pure static fn).
  2. Single `GetResponseAsync` call with `ChatOptions { ModelId = plannerModel, Instructions = prompt }`
     and **no tools** (planner does not act).
  3. Extract assistant text → parse JSON → `Plan`.
  4. On revise, re-invoke with the prior plan + judge feedback appended.
- **Implementation notes:**
  - There is **no** existing structured-output parser to reuse: `AgentJsonContext` only serializes
    `Dictionary<string,object?>` and there is no `AgentLlmResponse` type. Build plan/verdict parsing
    from scratch — add source-gen `PlanJsonContext`/`VerdictJsonContext` to stay AOT/trim-safe and warning-free.
  - `Planner` is `sealed`; expose `IPlanner` for substitution in tests/hosts.
- **Constraints:** No tool execution. Single responsibility = produce/repair a plan. Max plan size is
  capped (`MaxSteps`, default 12) to keep token cost and fan-out bounded.
- **V1 vs V2:** V1 = linear plan + optional parallel groups. V2 = dependency edges (DAG) + replanning
  mid-execution when a step reveals new information.

### 6.2 Worker tier (reuses `AgentTool` / `ChatSession`)

- **Purpose:** Execute exactly one `PlanStep` and return a `StepResult`.
- **Responsibilities:**
  - Spawn a child `Agent`/`ChatSession` via the existing factory signature
    `Func<string?, string?, (AgentOptions, Agent, IToolRegistry)>` (identical to `AgentTool`).
  - Run the step prompt to completion under the worker's own `StopConditions`.
  - Collapse the worker's `AgentEvent` stream into a single `StepResult { StepId, Output, Status, Usage }`.
- **Inputs:** `PlanStep`, worker (clientName, model), scoped `ToolContext`.
- **Outputs:** `StepResult`.
- **Internal flow:** This is *literally* the body of `AgentTool.InvokeAsync` (`Tools/AgentTool.cs:45-120`,
  which now also contains a permission auto-deny/resume loop the extraction must preserve) generalized:
  build `ChatSession`, `await foreach` the events, capture
  `FinalText` + terminal `AgentResultStatus`. V1 extracts that into a reusable `WorkerRunner` so both
  `AgentTool` and the orchestrator share one code path.
- **Implementation notes:**
  - Independent steps within the same parallel group run via `Task.WhenAll` — mirrors how `Agent`
    already executes tool calls in parallel (`Agent.cs:689` per-call lambda … `:876` `Task.WhenAll`). Concurrency must respect the
    **LM Studio 2-slot limit** (see §11) via a `SemaphoreSlim` gate when the worker client is local.
  - Each worker gets a **fresh `Context`** — workers do not share conversation history (isolation =
    smaller prompts + no cross-contamination).
- **Constraints:** A worker sees only its step prompt + injected `KnowledgeContext` facts, never the
  full plan. One step in, one result out.
- **V1 vs V2:** V1 = stateless workers. V2 = worker memory handoff (a step may pass artifacts to a
  dependent step via `MemoryContext`).

### 6.3 `IJudge` / `Judge`

- **Purpose:** Decide whether a plan or step result satisfies acceptance criteria.
- **Responsibilities:**
  - Build a judging prompt embedding the objective, the unit under review (plan or step output), and
    a **rubric** (`HierarchyOptions.Rubric`).
  - Call the judge-tier `IChatClient` once; parse a structured `Verdict`.
  - Defend against known judge biases (length, self-preference, position) via prompt design and a
    **different model than the worker** (see §14).
- **Inputs:** objective, rubric, `Plan` *or* `StepResult`, attempt index.
- **Outputs:** `Verdict` = `Accept` | `Revise(feedback, targetStepId?)` | `Escalate(reason)`.
- **Internal flow:**
  1. `JudgePromptBuilder.Build(...)` (pure static fn).
  2. Single `GetResponseAsync` (no tools by default; tool-using "agent-as-judge" is a V2 opt-in).
  3. Parse JSON verdict (`PlanJsonContext`/`VerdictJsonContext`).
- **Implementation notes:** The **gate-judge** variant is a thin adapter that maps a `Verdict` onto a
  `PreToolUseDecision` (`Accept`→`Allow`, `Revise`/`Escalate`→`Deny(reason)`), letting a judge plug
  into the existing `OnPreToolUse` hook with zero loop changes.
- **Constraints:** The judge never edits output; it only scores and routes. Mutation is the planner's/
  worker's job. This keeps responsibilities clean and the judge stateless.
- **V1 vs V2:** V1 = single static-rubric judge. V2 = multi-judge aggregation + agent-as-judge with
  read-only verification tools.

### 6.4 `PlannerWorkerJudge` (orchestrator)

- **Purpose:** Own the plan → execute → verify → revise lifecycle and emit the event stream.
- **Responsibilities:** sequencing, parallel dispatch, revision-budget enforcement, usage/cost
  aggregation, event emission, terminal status.
- **Inputs:** `string objective`, `Context` (tools/knowledge/memory), `HierarchyOptions`, `CancellationToken`.
- **Outputs:** `IAsyncEnumerable<AgentEvent>` (new event types defined in §7).
- **Internal flow:** see §8.
- **Constraints:** Single in-flight run per instance (mirrors `ChatSession` not being thread-safe).
- **V1 vs V2:** V1 = orchestrator class. V2 = also expose it *as a tool* (`hierarchy_tool`) so a parent
  `Agent` can delegate an entire objective to a PWJ sub-hierarchy, recursively.

### 6.5 Component responsibility matrix

| Concern | Planner | Worker | Judge | Orchestrator |
|---------|:-------:|:------:|:-----:|:------------:|
| Decompose objective | ✅ | | | |
| Execute tools | | ✅ | (V2) | |
| Score / route | | | ✅ | |
| Sequencing & fan-out | | | | ✅ |
| Budget enforcement | | | | ✅ |
| Emit events | | | | ✅ |
| Model tier | Strong | Cheap | Standard | — |

---

## 7. Data Model / State Layer

All state is **in-memory** in V1 (NG-7). Types live in `Agency.Harness` as `sealed record`s, matching
the existing `AgentEvent` / `Context` style (file-scoped namespace, XML docs, nullable-enabled).

### 7.1 Core records

```csharp
namespace Agency.Harness.Hierarchy;

/// <summary>An ordered decomposition of an objective produced by the planner.</summary>
public sealed record Plan
{
    public required IReadOnlyList<PlanStep> Steps { get; init; }
    public bool   IsTrivial { get; init; }           // single-step / direct-answer fast path
    public string? Rationale { get; init; }           // planner's reasoning (for traces)
}

/// <summary>One unit of work dispatched to a single worker.</summary>
public sealed record PlanStep
{
    public required string StepId       { get; init; } // stable id, e.g. "s1"
    public required string Instruction  { get; init; } // the worker prompt
    public string?         ParallelGroup { get; init; } // steps sharing a group run concurrently
    public IReadOnlyList<string> DependsOn { get; init; } = []; // V1: empty or intra-list ordering only
    public string?         ClientName   { get; init; } // optional per-step model override
    public string?         Model        { get; init; }
}

/// <summary>The outcome of executing one PlanStep.</summary>
public sealed record StepResult
{
    public required string             StepId  { get; init; }
    public required string             Output  { get; init; }
    public required AgentResultStatus  Status  { get; init; } // reuses existing enum
    public required LlmTokenUsage      Usage   { get; init; } // reuses existing record
}

/// <summary>The judge's decision over a plan or a step result.</summary>
public abstract record Verdict
{
    public sealed record Accept                                   : Verdict;
    public sealed record Revise(string Feedback, string? TargetStepId = null) : Verdict;
    public sealed record Escalate(string Reason)                  : Verdict;
}
```

### 7.2 New `AgentEvent` subtypes

Extends the existing `AgentEvent` hierarchy (`AgentEvents.cs`) so hosts already consuming the stream
keep working:

```csharp
public sealed record PlanCreatedEvent(Plan Plan, int Attempt)                          : AgentEvent;
public sealed record StepDispatchedEvent(PlanStep Step)                                : AgentEvent;
public sealed record StepCompletedEvent(StepResult Result)                             : AgentEvent;
public sealed record VerdictEvent(string SubjectId, Verdict Verdict, int Attempt)      : AgentEvent;
public sealed record EscalationEvent(string Reason, Plan Plan, IReadOnlyList<StepResult> Partial) : AgentEvent;
// Terminal AgentResultEvent (existing) still closes every run.
```

### 7.3 State-flow table

| State | Owner | Mutated by | Lifetime |
|-------|-------|-----------|----------|
| `Plan` | orchestrator | planner (create / revise) | one run |
| `StepResult[]` | orchestrator | workers | one run |
| `Verdict` | transient | judge | per verification |
| `Context.TotalUsage` / `TotalCostUsd` | `Context` | orchestrator (aggregates worker+role usage) | one run |
| revision counter | orchestrator | orchestrator | one run |

### 7.4 No new persistence backend

V1 introduces **no** tables, no SQLite/Postgres schema, no `KeyValueStore` usage. If V2 adds durable
plan replay, the natural home is the existing `Agency.KeyValueStore.*` abstraction keyed by run id —
explicitly deferred here.

---

## 8. Core Algorithms / Processing Logic

### 8.1 Orchestration loop (pseudocode)

```
RunAsync(objective, ctx, options, ct):
    yield SessionStartedEvent(runId)

    attempt = 0
    plan = Planner.CreatePlanAsync(objective, ctx, ct)
    yield PlanCreatedEvent(plan, attempt)

    # Fast path — trivial objective short-circuits the whole hierarchy
    if plan.IsTrivial or plan.Steps.Count == 1:
        result = Worker.RunStepAsync(plan.Steps[0], ctx, ct)
        yield StepCompletedEvent(result)
        yield AgentResultEvent(result.Status, result.Output, ctx.TotalUsage, ctx.TotalCostUsd)
        return

    while true:
        ct.ThrowIfCancellationRequested()

        # (optional) verify the PLAN before spending worker tokens
        if options.VerifyPlan:
            v = Judge.VerifyPlanAsync(objective, options.Rubric, plan, attempt, ct)
            yield VerdictEvent(planId, v, attempt)
            if v is Revise r and attempt < options.MaxRevisions:
                attempt++; plan = Planner.RevisePlanAsync(plan, r.Feedback, ct)
                yield PlanCreatedEvent(plan, attempt); continue
            if v is Escalate e: yield EscalationEvent(...); yield AgentResultEvent(Error,...); return

        # execute steps, honoring parallel groups
        results = []
        foreach group in GroupBy(plan.Steps, s => s.ParallelGroup):
            groupResults = await Task.WhenAll(group.Select(s => DispatchAsync(s, ctx, ct)))
            results.AddRange(groupResults)            # each Dispatch yields StepDispatched/StepCompleted

        # verify the aggregate result
        verdict = Judge.VerifyResultAsync(objective, options.Rubric, results, attempt, ct)
        yield VerdictEvent(runId, verdict, attempt)

        switch verdict:
            Accept:
                final = Synthesize(results)           # planner or template merge of step outputs
                yield AgentResultEvent(Success, final, ctx.TotalUsage, ctx.TotalCostUsd); return
            Revise r when attempt < options.MaxRevisions:
                attempt++
                plan = Planner.RevisePlanAsync(plan, r.Feedback, ct)
                yield PlanCreatedEvent(plan, attempt); continue
            Revise:                                    # budget exhausted
                yield AgentResultEvent(MaxStepsReached, BestEffort(results), ...); return
            Escalate e:
                yield EscalationEvent(e.Reason, plan, results)
                yield AgentResultEvent(Error, e.Reason, ...); return

        # global guards (reuse StopConditions semantics)
        if options.Budget exceeded or options.TokenBudget exceeded:
            yield AgentResultEvent(BudgetExceeded, BestEffort(results), ...); return
```

### 8.2 Termination guarantees

The loop **provably terminates** because every back-edge increments `attempt`, and the loop exits when
`attempt >= options.MaxRevisions`. Independently, `Budget` / `TokenBudget` guards force exit. There is
no path that loops without incrementing a bounded counter — this is the single most important
correctness property and gets a dedicated test (§17, T-ORCH-3).

### 8.3 Verdict parsing & malformed-output handling

| Failure | Detection | Handling |
|---------|-----------|----------|
| Planner returns non-JSON | `JsonException` on parse | One bounded re-prompt ("return only JSON matching schema"); then `Escalate`. |
| Judge returns unparseable verdict | parse failure | Treat as `Revise("judge output unparseable")` once; second failure → `Escalate`. |
| Plan exceeds `MaxSteps` | count check | Truncate + log warning, or `Revise` planner with "too many steps". |
| Worker truncated (`finish_reason=length`) | already surfaced by `Agent` as `AgentResultStatus.Error` | Step marked failed; judge sees failed step and routes. |
| Empty plan | count == 0 | `Escalate("planner produced no steps")`. |

### 8.4 Synthesis step

When the judge `Accept`s, step outputs are merged into one final answer. V1 default = a **planner
synthesis call** ("given these step results, produce the final answer"). A cheaper alternative
(`SynthesisMode.Concatenate`) joins step outputs with headers for deterministic, zero-cost merging —
configurable via `HierarchyOptions.Synthesis`.

---

## 9. Incremental vs Full Processing

| Mode | Behavior | When |
|------|----------|------|
| **Full** (V1 default) | Planner builds the whole plan up front; all steps execute; judge verifies the aggregate. | Default. |
| **Incremental verify** | Judge verifies *each step result* as it completes, allowing early `Revise` of a single step without re-running the whole plan. | `options.VerifyEachStep = true`. Cheaper revisions, more judge calls. |
| **Replanning (V2)** | A step result can trigger the planner to amend remaining steps (new info discovered mid-run). | Deferred — requires DAG model. |

The **incremental verify** mode reuses the same `Judge.VerifyResultAsync` per step instead of once at
the end, and targets `Revise(feedback, targetStepId)` so only the failing step's worker re-runs. This
is the recommended mode for long plans where re-running everything on one bad step is wasteful.

---

## 10. Background Workers / Async Components

- **Parallel step dispatch:** Steps sharing a `ParallelGroup` execute via `Task.WhenAll`, exactly
  mirroring how `Agent.cs` runs tool calls concurrently (`Agent.cs:689`–`:876`). The orchestrator never blocks
  a thread — it is fully `async`/`IAsyncEnumerable`.
- **Concurrency cap:** A `SemaphoreSlim(maxConcurrency)` gates worker spawning. Default
  `maxConcurrency = 2` when the worker client is the local LM Studio endpoint (hard requirement — the
  AMD 8060S iGPU crashes above 2 concurrent inference slots); higher for hosted APIs.
- **Cancellation:** The orchestrator threads `CancellationToken` into every planner/worker/judge call.
  A per-run timeout reuses the `AgentOptions.TurnTimeoutSeconds` pattern (`CreateLinkedTokenSource` +
  `CancelAfter`) already implemented in `Agent.ChatAsync` (`Agent.cs:172`+).
- **Event streaming:** Events are yielded as they happen, so a host renders plan/dispatch/verdict
  progress live — no buffering of the whole run.
- **No daemon/hosted-service component** in V1. The orchestrator is invoked per request; it is not a
  long-running background service.

---

## 11. Performance Expectations

| Metric | Target / Expectation | Notes |
|--------|----------------------|-------|
| Trivial-objective overhead | ≤ 1 LLM call | Fast path (§8.1) must not pay the 3-role tax. |
| Planner latency | 1 strong-tier call | No tool loop; pure reasoning. |
| Worker fan-out | up to `maxConcurrency` parallel | 2 for local LM Studio; 8–16 for hosted. |
| Judge cost (full mode) | 1 call per attempt | +1 per step in incremental mode. |
| Token savings vs flat loop | ~20–30% on multi-step objectives | Cheap workers replace strong-model tool spam (literature + tier routing). |
| Revision ceiling | `MaxRevisions` (default 2) | Hard bound; prevents runaway cost. |
| Worst-case calls | `1 + MaxRevisions·(1 + ⌈steps/conc⌉ + 1)` | Planner + per-attempt (plan-verify + workers + result-verify). |

**Scaling notes:** Cost scales with `MaxRevisions × stepCount`, not exponentially — the judge prunes
rather than branches. The dominant lever is the **worker model tier**: pushing execution onto a cheap
model is where the savings come from, so the planner/judge being expensive is acceptable because they
fire O(attempts), not O(steps).

---

## 12. Edge Cases and Failure Modes

| # | Scenario | Expected behavior |
|---|----------|-------------------|
| E-1 | Empty / whitespace objective | Reject before planning; `ArgumentException`. |
| E-2 | Planner emits malformed JSON | One bounded re-prompt → else `Escalate`. |
| E-3 | Plan has 0 steps | `Escalate("no steps")`. |
| E-4 | Plan exceeds `MaxSteps` | Truncate + warn, or `Revise`. |
| E-5 | Worker step fails (tool error / truncation) | `StepResult.Status = Error`; judge decides revise vs escalate. |
| E-6 | Judge never accepts (always `Revise`) | Loop exits at `MaxRevisions` with `MaxStepsReached` + best-effort output. |
| E-7 | Judge unparseable verdict | Treat as `Revise` once, then `Escalate`. |
| E-8 | Cost/token budget hit mid-run | Exit with `BudgetExceeded` + partial output (reuses `AgentResultStatus`). |
| E-9 | Cancellation mid-run | `OperationCanceledException` propagates; partial events already emitted. |
| E-10 | Parallel step throws | `Task.WhenAll` surfaces; that step → `Error`, siblings complete; judge sees mixed results. |
| E-11 | Local concurrency > 2 (LM Studio) | Semaphore caps at 2; never crashes the iGPU. |
| E-12 | Circular `DependsOn` (V2 DAG) | Detected at plan-validation; `Escalate`. (N/A in V1 linear plans.) |
| E-13 | Gate-judge denies every worker tool call | Worker returns no progress; surfaces as failed step → escalate. |
| E-14 | Self-preference bias (judge == worker model) | Mitigated by requiring distinct tiers; warn if misconfigured. |

---

## 13. End-to-End Flow

Worked example for §2.1 ("rename `Generate` → `GenerateAsync`"):

```
1.  Host calls PlannerWorkerJudge.RunAsync(objective, ctx{tools: read/write/powershell}, options)
2.  → SessionStartedEvent(run-7f3a)
3.  Planner (Strong, e.g. claude-opus) called once with tool catalogue in prompt
4.  → PlanCreatedEvent(plan = [s1 locate, s2 rename-iface, s3 update-callers(group:g1),
                                s4 update-tests(group:g1), s5 build-verify], attempt 0)
5.  options.VerifyPlan = true → Judge checks plan covers tests + build
6.  → VerdictEvent(plan, Accept, 0)
7.  Dispatch s1 (no group) → worker(Cheap) reads files
        → StepDispatchedEvent(s1) → StepCompletedEvent(s1, Output="found 3 impls")
8.  Dispatch s2 → worker renames interface method
        → StepDispatched/Completed(s2)
9.  Dispatch group g1: s3 + s4 in parallel (Task.WhenAll, semaphore<=2 local)
        → StepDispatched(s3), StepDispatched(s4), StepCompleted(s3), StepCompleted(s4)
10. Dispatch s5 → worker runs `dotnet build` via execute_powershell, returns output
        → StepCompleted(s5, Output="Build succeeded, 0 errors")
11. Judge.VerifyResultAsync(objective, rubric, [s1..s5])
        rubric: "build clean? all call sites updated? tests compile?"
12. → VerdictEvent(run-7f3a, Accept, 0)
13. Synthesis (planner merge) → "Renamed across 3 files + 2 tests; build green."
14. → AgentResultEvent(Success, finalText, TotalUsage{...}, TotalCostUsd)

   Counterfactual: if step 10 build had errors, Judge → Revise("build failed: <compiler output>",
   targetStepId=s2); attempt=1; planner amends s2; loop re-runs from step 7 (or just s2 in
   incremental mode). Bounded by MaxRevisions.
```

---

## 14. Design Notes / Rationale

- **Why an orchestrator and not "just a smarter system prompt"?** A single agent asked to "plan then
  do then check" collapses all three roles into one model at one tier — you lose per-role model
  selection (the cost lever), you lose the *hard* verification gate (the model can skip its own
  check), and you lose structured observability. Separating roles makes each independently testable,
  swappable, and measurable.

- **Why reuse `AgentTool`'s factory instead of a new worker type?** `AgentTool.InvokeAsync`
  (`Tools/AgentTool.cs`) already does exactly what a worker does: spawn a `ChatSession` from a
  `(clientName, model)` factory, stream events, collapse to a final result. That factory seam is now
  `IAgentFactory` (public in `Agency.Harness`, `Agents/`); `AgentTool` additionally bundles the worker's
  `IToolRegistry` via its own delegate. V1 extracts that into a
  shared `WorkerRunner` so there is *one* delegation code path, and `subagent_tool` becomes a thin
  caller of it. This avoids two divergent implementations of "run a child agent."

- **Why `OnPreToolUse` for the gate judge?** The hook already supports `Deny`/`Rewrite` and runs
  *before* a tool commits (`Agent.cs:699`–`:806`). Mapping `Verdict → PreToolUseDecision` means the
  gate judge requires **zero changes to the agent loop** — it is pure composition. This is the same
  pattern `BlockListHooks.Dangerous` uses today.

- **Why a different model for judge vs worker?** The literature's strongest, most reproducible judge
  bias is **self-preference** — a model rates its own outputs higher. Requiring distinct tiers (and
  warning when they match) is a cheap structural mitigation. Length and position bias are handled in
  the rubric prompt.

- **Why bound by revision count rather than "until correct"?** "Until correct" is unbounded and the
  failure mode is silent cost blow-up. A revision budget makes the worst case computable (§11) and
  turns "stuck" into an explicit `MaxStepsReached`/`Escalate` outcome a host can act on.

- **Why keep plans linear in V1?** A DAG is more expressive but needs cycle detection, topological
  scheduling, and partial-failure semantics — a lot of surface area. Linear + parallel-groups covers
  the 80% case (independent fan-out) at a fraction of the complexity. DAG is a clean V2 extension of
  `PlanStep.DependsOn`, which is already in the model as a forward-compatible (V1-unused) field.

- **Trade-off accepted — latency for assurance:** PWJ adds round-trips (plan, verify) that a flat loop
  skips. For trivial objectives this is wasteful, hence the fast path (§8.1). For multi-step
  objectives the assurance + token savings dominate. The orchestrator is opt-in: hosts pick `Agent`
  for simple tasks and `PlannerWorkerJudge` for complex ones.

- **Consistency with house style:** all new types are `sealed record`s with `///` XML docs,
  file-scoped namespaces, nullable enabled, warning-free (`TreatWarningsAsErrors=true` per
  `Directory.Build.props`). Implementation members that need test visibility are marked `internal`
  with `InternalsVisibleTo` (the project's established pattern), never `public`-for-testing.

---

## 15. Configuration Reference

New `HierarchyOptions` (sits alongside `AgentOptions`, bound from configuration):

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `PlannerClientName` | `string?` | `DefaultClientName` | Client for the planner role. |
| `PlannerModel` | `string?` | strong-tier model | Planner model id. |
| `WorkerClientName` | `string?` | `DefaultClientName` | Client for workers. |
| `WorkerModel` | `string?` | cheap-tier model | Worker model id. |
| `JudgeClientName` | `string?` | `DefaultClientName` | Client for the judge. |
| `JudgeModel` | `string?` | standard-tier model | Judge model id (should differ from worker — see §14). |
| `MaxRevisions` | `int` | `2` | Hard ceiling on plan/result revision cycles. |
| `MaxSteps` | `int` | `12` | Max steps a plan may contain. |
| `MaxConcurrency` | `int` | `2` (local) / `8` (hosted) | Parallel worker cap; respects LM Studio 2-slot limit. |
| `VerifyPlan` | `bool` | `true` | Run the judge over the plan before executing. |
| `VerifyEachStep` | `bool` | `false` | Incremental per-step verification (§9). |
| `Rubric` | `string` | sensible default | Acceptance criteria injected into the judge prompt. |
| `Synthesis` | `enum { PlannerMerge, Concatenate }` | `PlannerMerge` | How accepted step outputs become the final answer. |
| `Budget` | `decimal?` | `null` | USD cost ceiling (reuses `StopConditions.BudgetExceeded` semantics). |
| `TokenBudget` | `long?` | `null` | Total-token ceiling. |
| `TurnTimeoutSeconds` | `int?` | `null` | Per-run timeout (reuses `Agent` pattern). |

---

## 16. Observability Reference

Mirrors `Agent` exactly (same ActivitySource+Meter approach, same tag vocabulary), adding a `role` tag
(`planner` | `worker` | `judge`) so a single dashboard slices PWJ cost by role.

| Signal | Name | Tags |
|--------|------|------|
| `ActivitySource` | `Agency.Harness.Hierarchy` | — |
| `Meter` | `Agency.Harness.Hierarchy` | — |
| Counter | `hierarchy.runs` | `status` |
| Counter | `hierarchy.plans` | `attempt` |
| Counter | `hierarchy.steps` | `role=worker`, `step.status` |
| Counter | `hierarchy.verdicts` | `verdict` (`accept`/`revise`/`escalate`) |
| Counter | `hierarchy.revisions` | — |
| Counter | `hierarchy.tokens` | `role`, `model`, `client_type`, `token.type` |
| Counter | `hierarchy.escalations` | `reason.kind` |
| Histogram | `hierarchy.run.duration` (ms) | `status` |
| Histogram | `hierarchy.role.duration` (ms) | `role`, `model`, `client_type` |

Activities: `Hierarchy.RunAsync` (root span) with child spans `hierarchy.plan`, `hierarchy.step`,
`hierarchy.verify` — enabling a trace waterfall that shows the plan→fan-out→verify shape directly.

---

## 17. Implementation Plan (TDD, Test-First)

Per the project's TDD discipline (red → green → refactor), **every implementation task is preceded by
its test task**. All unit tests drive `FakeChatClient` (the class in `Test/Fakes/FakeLlmClient.cs`;
FIFO via `EnqueueResponse(ChatResponse)`) — no live LLM.
Functional E2E tests are tagged `Category=Functional` and target LM Studio (`http://llm-host.example:1234`).

### Phase 0 — Domain model & contracts

- **T-MODEL-1 (test):** Records `Plan`, `PlanStep`, `StepResult`, `Verdict`, and new `AgentEvent`
  subtypes round-trip through `System.Text.Json` (source-gen `PlanJsonContext`). Assert
  serialization is AOT/trim-safe (no reflection fallback).
- **IMPL-MODEL-1:** Add the records (§7) + `PlanJsonContext`. Verify build is warning-free.

### Phase 1 — Planner

- **T-PLAN-1 (test):** Given a `FakeChatClient` returning a valid JSON plan, `Planner.CreatePlanAsync`
  parses N steps in order, preserving `ParallelGroup`.
- **T-PLAN-2 (test):** Malformed JSON triggers exactly one re-prompt, then surfaces a parse failure.
- **T-PLAN-3 (test):** A trivial objective response sets `Plan.IsTrivial = true`.
- **T-PLAN-4 (test):** `PlannerPromptBuilder.Build` (pure fn) includes the tool catalogue from
  `ToolContext.Registry.ListDefinitions()`.
- **IMPL-PLAN-1:** Implement `IPlanner`/`Planner` + `PlannerPromptBuilder`.

### Phase 2 — Worker runner (extract from `AgentTool`)

- **T-WORK-1 (test):** `WorkerRunner.RunStepAsync` collapses a `ChatSession` event stream into a
  `StepResult` with correct `Status` + `Output` (mirror existing `AgentTool` behavior).
- **T-WORK-2 (test):** A failed/truncated worker run yields `StepResult.Status = Error`.
- **T-WORK-3 (test):** `subagent_tool` (refactored `AgentTool`) still passes its existing tests after
  delegating to `WorkerRunner` (no behavior change).
- **IMPL-WORK-1:** Extract `WorkerRunner`; point `AgentTool` at it.

### Phase 3 — Judge

- **T-JUDGE-1 (test):** Valid `Accept` verdict parsed from `FakeChatClient`.
- **T-JUDGE-2 (test):** `Revise` verdict carries feedback + optional `TargetStepId`.
- **T-JUDGE-3 (test):** Unparseable verdict → treated as `Revise` once.
- **T-JUDGE-4 (test):** Gate adapter maps `Accept→Allow`, `Revise/Escalate→Deny` as a
  `PreToolUseDecision`.
- **IMPL-JUDGE-1:** Implement `IJudge`/`Judge` + `JudgePromptBuilder` + gate adapter.

### Phase 4 — Orchestrator

- **T-ORCH-1 (test):** Happy path — plan(2 steps)→workers→Accept→Success; assert event order
  (`SessionStarted, PlanCreated, StepDispatched×2, StepCompleted×2, Verdict, AgentResult`).
- **T-ORCH-2 (test):** Trivial fast path — single LLM call, no judge events emitted.
- **T-ORCH-3 (test):** **Termination** — judge always `Revise`; run exits at `MaxRevisions` with
  `MaxStepsReached`. (Guards the core correctness property, §8.2.)
- **T-ORCH-4 (test):** Parallel group — two steps in one group dispatched concurrently; results
  aggregated.
- **T-ORCH-5 (test):** Budget guard — `TokenBudget` exceeded → `BudgetExceeded` with partial output.
- **T-ORCH-6 (test):** Escalation — empty plan → `EscalationEvent` + `Error`.
- **T-ORCH-7 (test):** Cancellation — token cancelled mid-run propagates `OperationCanceledException`.
- **IMPL-ORCH-1:** Implement `PlannerWorkerJudge` orchestrator + new events.

### Phase 5 — Observability & config

- **T-OBS-1 (test):** Run emits `hierarchy.runs`, `hierarchy.verdicts`, `hierarchy.revisions` with
  expected tags (use a `MeterListener` in-test, as existing telemetry tests do).
- **IMPL-OBS-1:** Wire ActivitySource/Meter (§16); bind `HierarchyOptions` from configuration.

### Phase 6 — Functional E2E

- **T-E2E-1 (`Category=Functional`):** Against LM Studio, a real 3-step objective completes with
  `Success`, `MaxConcurrency=2` honored, total cost recorded.

### Phase 7 — Console integration (optional, V1.1)

- **T-CON-1 (test):** `Agency.Harness.Console` renders `PlanCreatedEvent` / `VerdictEvent` to the
  terminal via `IChatOutput`.
- **IMPL-CON-1:** Add rendering cases for the new event types.

---

## 18. Open Questions

| # | Question | Proposed default (pending sign-off) |
|---|----------|-------------------------------------|
| Q-1 | Should the orchestrator also be exposed as a `hierarchy_tool` so a parent agent can recurse? | Defer to V2; design the orchestrator so it *can* implement `ITool` later. |
| Q-2 | Synthesis: planner-merge (costs a call) vs concatenate (free)? | Default `PlannerMerge`; expose `Concatenate` for cost-sensitive hosts. |
| Q-3 | Do model "tiers" map to named `LlmClientOptions` entries, or to model-id strings? | Use existing `(clientName, model)` selection — no new tier concept needed. |
| Q-4 | Plan-verification on by default? | Yes (`VerifyPlan = true`) — cheap insurance; one judge call. |
| Q-5 | Should gate-judge and critic-judge be combinable in one run? | Yes — orthogonal seams; allow both, document the cost. |

---

*End of specification.*
