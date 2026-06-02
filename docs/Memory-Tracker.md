# Long-Term Memory & Asynchronous Distillation — Status Tracker

**Source plan:** [`Memory-ProjectPlan.md`](Memory-ProjectPlan.md)
**Source spec:** [`Memory-Specifications.md`](Memory-Specifications.md)
**Last updated:** 2026-05-28 (SA-G completed all G.0–G.5 tasks)

## Legend

- **Active** — prioritized and ready for a sub-agent to pick up; work has not started or is waiting on a dependency.
- **In Progress** — a sub-agent is currently executing this task.
- **Done** — all Acceptance criteria in the plan are met (tests green, build green, XML docs present, no scope drift).

TDD invariant: every `*.T` test task must be **Done** (Red committed) before its paired implementation task can move to **In Progress**.

---

## Status

| **Deliverable / Task** | **Active** | **In Progress** | **Done** |
|---|:---:|:---:|:---:|
| **D1. `Agency.Memory.Common` (Workstream A)** | | | |
| A.0 Scaffold `Agency.Memory.Common` + `.Test` projects | | | ✔ |
| A.1.T Write failing tests for `Record` + `ContentType` | | | ✔ |
| A.1 Implement `Record` + `ContentType` | | | ✔ |
| A.2.T Write failing tests for ranking formula | | | ✔ |
| A.2 Implement `RankingFormula` + `RankingWeights` | | | ✔ |
| A.3.T Write failing tests for options records | | | ✔ |
| A.3 Implement `MemoryOptions` / `DistillerOptions` / `ConsolidatorOptions` | | | ✔ |
| A.4.T Write failing `IMemoryStore` contract tests | | | ✔ |
| A.4 Implement `IMemoryStore` + `SearchQuery` / `SearchHit` DTOs | | | ✔ |
| A.5.T Write failing tests for `MemoryHookFactory` baseline composition | | | ✔ |
| A.5 Implement `MemoryHookFactory` + `ComposeBefore` + `AddAgencyMemory` | | | ✔ |
| A.6.T Write failing tests for `DistillationJob` / `ConsolidationJob` | | | ✔ |
| A.6 Implement `DistillationJob` / `ConsolidationJob` / triggers | | | ✔ |
| A.7.T Write failing tests for memory `AgentEvent` payloads | | | ✔ |
| A.7 Implement memory events | | | ✔ |
| **D2. `Agency.Memory.Sql.Postgres` (Workstream B)** | | | |
| B.0 Scaffold `Agency.Memory.Sql.Postgres` + `.Test` projects | | | ✔ |
| B.1.T Write failing tests for `MemorySchemaInitializer` | | | ✔ |
| B.1 Implement `MemorySchemaInitializer` | | | ✔ |
| B.2.T Write failing tests for `PostgresMemoryStore.UpsertAsync` | | | ✔ |
| B.2 Implement `PostgresMemoryStore.UpsertAsync` | | | ✔ |
| B.3.T Write failing tests for `PostgresMemoryStore.SearchAsync` | | | ✔ |
| B.3 Implement `PostgresMemoryStore.SearchAsync` | | | ✔ |
| B.4.T Write failing tests for `ForgetAsync` / `ForgetMeAsync` | | | ✔ |
| B.4 Implement forget operations | | | ✔ |
| B.5.T Write failing tests for `LastWrittenAtAsync` + cache | | | ✔ |
| B.5 Implement `LastWrittenAtAsync` + cache hydration | | | ✔ |
| B.6.T Write failing tests for `WatermarkRepository` | | | ✔ |
| B.6 Implement `WatermarkRepository` | | | ✔ |
| B.7.T Write failing tests for `DeadLetterRepository` | | | ✔ |
| B.7 Implement `DeadLetterRepository` | | | ✔ |
| B.8 Re-run `A.4.T` contract tests against `PostgresMemoryStore` | | | ✔ |
| **D3. Distiller — `Agency.Memory.Distiller` (Workstream C)** | | | |
| C.0 Scaffold `Agency.Memory.Distiller` + `.Test` projects | | | ✔ |
| C.1.T Write failing tests for `EpisodeExtractionPrompt` + parser | | | ✔ |
| C.1 Implement `EpisodeExtractionPrompt` + parser | | | ✔ |
| C.2.T Write failing tests for `InactivityTimerService` | | | ✔ |
| C.2 Implement `InactivityTimerService` | | | ✔ |
| C.3.T Write failing tests for `DistillerBackgroundService` happy path | | | ✔ |
| C.3 Implement `DistillerBackgroundService` happy path | | | ✔ |
| C.4.T Write failing tests for idempotency by watermark | | | ✔ |
| C.4 Implement watermark guard | | | ✔ |
| C.5.T Write failing tests for retry + dead-letter | | | ✔ |
| C.5 Implement retry + dead-letter | | | ✔ |
| C.6.T Write failing tests for `MarkGoalCompleteTool` + `SetFocusTool` | | | ✔ |
| C.6 Implement `MarkGoalCompleteTool` + `SetFocusTool` | | | ✔ |
| C.7.T Write failing test for timer-only `OnAssistantTurn` (negative regression) | | | ✔ |
| C.7 Wire timer-only `OnAssistantTurn` in `MemoryHookFactory` | | | ✔ |
| C.8.T Write failing test for bounded-channel backpressure | | | ✔ |
| C.8 Configure bounded channel (`DropOldest`, per-session) | | | ✔ |
| **D4. Retrieval Engine + Hooks — `Agency.Memory.Retrieval` (Workstream D)** | | | |
| D.0 Scaffold `Agency.Memory.Retrieval` + `.Test` projects | | | ✔ |
| D.1.T Write failing tests for the retrieval gate | | | ✔ |
| D.1 Implement `RetrievalGate` | | | ✔ |
| D.2.T Write failing tests for `RetrievalEngine` | | | ✔ |
| D.2 Implement `RetrievalEngine` | | | ✔ |
| D.3.T Write failing tests for `SystemPromptBuilder` memory rendering | | | ✔ |
| D.3 Extend `SystemPromptBuilder` with Facts/Memories sections | | | ✔ |
| D.4.T Write failing tests for `OnUserPromptSubmit` / `OnPostToolBatch` wiring | | | ✔ |
| D.4 Wire new hook invocations in `Agent.RunAsync` | | | ✔ |
| **D5. Consolidator — `Agency.Memory.Consolidator` (Workstream E)** | | | |
| E.0 Scaffold `Agency.Memory.Consolidator` + `.Test` projects | | | ✔ |
| E.1.T Write failing tests for consolidator tools (Merge/Update/Delete/Done) | | | ✔ |
| E.1 Implement consolidator tools | | | ✔ |
| E.2.T Write failing tests for `ConsolidatorReconciliationPrompt` | | | ✔ |
| E.2 Implement reconciliation prompt | | | ✔ |
| E.3.T Write failing tests for `ConsolidatorSubAgentFactory` + service | | | ✔ |
| E.3 Implement `ConsolidatorSubAgentFactory` + `ConsolidatorBackgroundService` | | | ✔ |
| **D6. Hygiene Sweeper — `Agency.Memory.Hygiene` (Workstream F)** | | | |
| F.0 Scaffold `Agency.Memory.Hygiene` + `.Test` projects | | | ✔ |
| F.1.T Write failing tests for TTL pass | | | ✔ |
| F.1 Implement TTL pass | | | ✔ |
| F.2.T Write failing tests for importance-pruning pass | | | ✔ |
| F.2 Implement importance pass | | | ✔ |
| F.3.T Write failing tests for sweep schedule + jitter | | | ✔ |
| F.3 Implement scheduler | | | ✔ |
| **D7. End-to-end Functional — `Agency.Memory.Functional.Test` (Workstream G)** | | | |
| G.0 Scaffold `Agency.Memory.Functional.Test` project | | | ✔ |
| G.1 `EndToEnd_FactWrittenInSessionN_RecalledInSessionNPlus1` → E2E **E1.1** | | | ✔ |
| G.2 `EndToEnd_ForgetMeWipesAllUserData` → E2E **E2.2** | | | ✔ |
| G.3 `EndToEnd_HighFrequencyTurns_HotPathLatencyUnaffected` → E2E **E6.1** | | | ✔ |
| G.4 `EndToEnd_DistillerCrash_RecoversFromWatermark` → E2E **E5.1** | | | ✔ |
| G.5 `EndToEnd_ConsolidatorMergesContradiction_LatestStateRetained` → E2E **E3.1** | | | ✔ |

---

## E2E Suite Status (Memory-TestPlan.md)

The black-box end-to-end suite (`Memory-TestPlan.md`) is tracked in detail in
[`Memory-TestPlan-Tracker.md`](Memory-TestPlan-Tracker.md); open questions in
[`Memory-TestPlan-Issues.md`](Memory-TestPlan-Issues.md).

**Status: COMPLETE — 41/41 deliverables Done** (5 `Infrastructure/` primitives + 36 E*.* tests, 8 groups). Full-solution build 0/0; capstone full-suite run 40 pass / 0 fail / 1 skip (the lone skip is this table's legacy G.1 `EndToEndRecallTests`, superseded by E2E **E1.1**). The suite is `[Trait("Category","Functional")]` and excluded from the default CI run (nightly per §5). E2E **E3.4** and **E6.1** are advisory (LLM-variance / latency). Owner-decision items applied & implemented 2026-06-02 (all resolved): **TI-4** (sweep honors injected `TimeProvider`), **TI-5** (service-unreachable retried), **TI-8.1** (distiller terminal-event symmetry via `DistillationSettledEvent` + polymorphic `InMemoryEventBus`), **TI-8.2** (distiller suppresses thinking: SDK `SuppressThinking` + prompt `/no_think`), **TI-8.3** (consolidator mutations forwarded as `MemoryMutatedEvent` and surfaced to the user), **TI-8.4** (structural DELETE rule in the reconciliation prompt) — see [`Memory-TestPlan-Issues.md`](Memory-TestPlan-Issues.md).

---

## Roll-up

| Deliverable | Total tasks | Active | In Progress | Done |
|---|---:|---:|---:|---:|
| D1 — Common | 15 | 0 | 0 | 15 |
| D2 — Postgres | 15 | 0 | 0 | 15 |
| D3 — Distiller | 17 | 0 | 0 | 17 |
| D4 — Retrieval + Hooks | 9 | 0 | 0 | 9 |
| D5 — Consolidator | 7 | 0 | 0 | 7 |
| D6 — Hygiene | 7 | 0 | 0 | 7 |
| D7 — End-to-end | 6 | 0 | 0 | 6 |
| **Total** | **76** | **0** | **0** | **76** |

---

## Next action

- **Workstream A complete.** All 15 D1 tasks done by SA-A.
- **Workstream B complete.** All 15 D2 tasks done by SA-B. Build: 0 errors, 0 warnings. 48 functional tests green, 151 non-functional tests green.
- **Complete (parallel):** Workstream D (SA-D) and Workstream F (SA-F) — both done.
- **Complete (parallel):** Workstream C (SA-C) — all 17 D3 tasks done. `MemoryHookFactory.Build()` fully wired. `IAsyncEventBus` defined in `Agency.Memory.Common/Events/`. `AddAgencyMemory` DI extension in `Agency.Memory.Distiller/DependencyInjection/`.
- **Complete:** Workstream E (E.0 → E.3) completed by SA-E. All 7 D5 tasks done. Build 0 errors/0 warnings. 16 non-functional tests green.
- **Complete:** Workstream G (G.0 → G.5) completed by SA-G. All 6 D7 tasks done. Build 0 errors/0 warnings. Non-functional test suite unchanged (0 new non-functional tests added). Functional tests: G.2 Pass, G.3 Pass, G.4 Pass, G.5 Pass, G.1 Skipped (LM Studio model not loaded at test time).
- See [`Memory-ProjectPlan.md` §Cross-Cutting / Sequencing Notes](Memory-ProjectPlan.md) for the full parallelisation map.

## Sub-agent dispatch log

| Agent | Workstream/Tasks | Dispatched | Completed | Notes |
|-------|------------------|------------|-----------|-------|
| SA-A | A.0 → A.7 (`Agency.Memory.Common`) | 2026-05-27 | 2026-05-27 | All 15 D1 tasks green; full solution build clean |
| SA-B | B.0 → B.8 (`Agency.Memory.Sql.Postgres`) | 2026-05-27 | 2026-05-28 | All 15 D2 tasks done; 48 functional tests green; build 0 errors/0 warnings |
| SA-D | D.0 → D.4 (`Agency.Memory.Retrieval` + Agentic hook wiring) | 2026-05-28 | 2026-05-28 | All 9 D4 tasks done; 11 retrieval tests + 159 Agentic tests green; build 0 errors/0 warnings |
| SA-F | F.0 → F.3 (`Agency.Memory.Hygiene`) | 2026-05-28 | 2026-05-28 | All 7 D6 tasks done; 11 non-functional tests green; build 0 errors/0 warnings. Added `Microsoft.Extensions.TimeProvider.Testing` v10.1.0 to Directory.Packages.props. |
| SA-C | C.0 → C.8 (`Agency.Memory.Distiller`) | 2026-05-28 | 2026-05-28 | All 17 D3 tasks done; 38 non-functional tests green; build 0 errors/0 warnings. Circular reference resolved via delegate injection (IQ-1). |
| SA-E | E.0 → E.3 (`Agency.Memory.Consolidator`) | 2026-05-28 | 2026-05-28 | All 7 D5 tasks done; 16 non-functional tests green; build 0 errors/0 warnings. Added `MergeAsync`, `UpdateRecordAsync`, `DeleteByIdAsync` to `IMemoryStore`; implemented in `PostgresMemoryStore`. `ConsolidatorBackgroundService` subscribes to `DistillationCompletedEvent`, enforces per-user serial execution with coalescing. `ConsolidatorSubAgentFactory` builds sub-agent with 4 tools and 3 stop conditions. |
| SA-G | G.0 → G.5 (`Agency.Memory.Functional.Test`) | 2026-05-28 | 2026-05-28 | All 6 D7 tasks done. Build 0 errors/0 warnings. Non-functional suite unchanged (0 new non-functional tests). Functional results: G.2 Pass, G.3 Pass, G.4 Pass, G.5 Pass; G.1 Skipped (LM Studio reachable but no model loaded at test time — will pass once a model is loaded). Added `InternalsVisibleTo("Agency.Memory.Functional.Test")` to Distiller, Retrieval, Common, and Consolidator AssemblyInfo. Filed IQ-3 and IQ-4. |

### Carry-over notes for Workstream C (from SA-D)

- **`RetrievalEngine` is in `Agency.Memory.Retrieval` namespace, class `RetrievalEngine` (internal).** To wire it into `MemoryHookFactory.Build()`, SA-C should:
  1. Add a project reference from `Agency.Memory.Common` → `Agency.Memory.Retrieval` (or pass `RetrievalEngine` as an injected delegate).
  2. The recommended approach: `MemoryHookFactory.Build(IMemoryStore, IEmbeddingGenerator, IOptions<MemoryOptions>)` constructs a `RetrievalEngine` and assigns its `RetrieveAsync` to `OnPreIteration` wrapped by `RetrievalGate.ShouldRetrieveAsync`.
  3. `RetrievalGate` is also in `Agency.Memory.Retrieval` namespace (internal static).
  4. Both are visible to `Agency.Memory.Common` via `[assembly: InternalsVisibleTo("Agency.Memory.Retrieval")]` that SA-D added to `Agency.Memory.Common/AssemblyInfo.cs` — but the direction of reference matters. SA-C should instead expose a `public` DI registration method from `Agency.Memory.Retrieval` (e.g., `Agency.Memory.Retrieval.DependencyInjection.RetrievalServiceCollectionExtensions`) and wire `MemoryHookFactory.Build()` to accept a factory delegate.
  5. **Alternative (simpler):** Add `Agency.Memory.Retrieval` as a project reference to `Agency.Memory.Common` and expose `RetrievalEngine`/`RetrievalGate` as `public` — this avoids the DI indirection. SA-C should decide based on the desired coupling model.
- **New context properties added** (SA-D): `UserSpecificContext.Id`, `Context.Focus` (`FocusContext`), `Context.MemoryLastRetrievedAt`, `KnowledgeContext.Records`, `MemoryContext.Records`, `MemoryRecord` DTO — all in `Agency.Agentic`.
- **Hook invocations added to `Agent.cs`:** `OnUserPromptSubmit` at top of `ChatAsync`; `OnPreIteration` before `SystemPromptBuilder.Build` each iteration; `OnPostToolBatch` after `Task.WhenAll` of tool calls.

### Carry-over notes for later workstreams (from SA-A report)

- `MemoryHookFactory` currently exposes only `BuildEmpty()`; full `Build()` wiring (retrieval → `OnPreIteration`, timer → `OnAssistantTurn`) is deferred until D2 (`RetrievalEngine`) and C2 (`InactivityTimerService`) exist. Workstream D and C must close this back-port (Plan §A.5 acceptance includes the `AddAgencyMemory` DI extension).
- `Record.Create(...)` static factory is the only validated constructor — `required` `init` setters are present but bypass validation; downstream code must call `Create`.
- `IMemoryStoreContractTests` lives in `Agency.Memory.Common.Test` as an abstract base. Workstream B (task B.8) will subclass it in `Agency.Memory.Sql.Postgres.Test`.

## Update protocol

When a sub-agent picks up a task:
1. Move its row from **Active** → **In Progress**.
2. On completion (all acceptance criteria met), move **In Progress** → **Done**.
3. Promote the next dependency (typically the paired `*.T` for an implementation, or the next sequential task) from **Active** → **In Progress**.
4. Update the **Last updated** date at the top and the **Roll-up** table.
5. Never mark an implementation task **Done** while its paired `*.T` is not **Done** — this violates the TDD invariant.
