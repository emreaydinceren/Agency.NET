# Long-Term Memory & Asynchronous Distillation — End-to-End Test Plan

**Status:** Draft — ready for authoring
**Source spec:** [`Memory-Specifications.md`](Memory-Specifications.md)
**Companion plan:** [`Memory-ProjectPlan.md`](Memory-ProjectPlan.md) (component-level TDD plan)
**Tracker:** [`Memory-Tracker.md`](Memory-Tracker.md) (extend with the suite status table from §6 below)
**Audience:** Engineers authoring end-to-end / black-box tests after Workstreams A–F land
**Last updated:** 2026-05-27

---

## 1. Scope & Non-Goals

This document defines the **black-box, end-to-end functional test suite** for
the memory subsystem. Tests in this suite:

- Boot the full host (DI + all four hosted services) per
  [`Memory-Specifications.md` §10](Memory-Specifications.md).
- Use the **real** PostgreSQL + pgvector backend
  (`src/docker-compose.yml`).
- Use the **real** LLM (LM Studio at `http://llm-host.example:1234` per
  [`CLAUDE.md`](../CLAUDE.md)) — except Group 5 (Failure & Recovery) where
  a `FaultInjectingLlmClient` decorator intercepts distiller/consolidator
  LLM calls.
- Use the **real** embedding generator.
- Drive the system **only through the public surface** —
  `ChatSession.SendAsync`, `IMemoryStore.*`, the `Forget-Me`
  admin endpoint, and the hosted-service events.
- Assert on **externally observable state** — store rows, emitted
  `AgentEvent`s, and the rendered system prompt (captured via a test-only
  hook composed after the memory baseline).

All tests carry `[Trait("Category","Functional")]` and are therefore
**excluded** from the default `dotnet test --filter "Category!=Functional"`
run per [`CLAUDE.md` §Build & Test Commands](../CLAUDE.md).

### Non-goals

- White-box assertions that duplicate component-level coverage from
  Workstreams A–F (e.g. "upsert preserves Id" is a B.2 contract test, not
  an E2E test).
- Load tests, fuzz tests, security/pen tests, multi-region.
- Spec changes. Every test in this catalogue is anchored to an existing
  section of [`Memory-Specifications.md`](Memory-Specifications.md). If an
  invariant you want to assert is not in the spec, file a spec gap; do not
  invent the invariant in a test.

### Prerequisites for running the suite

| Dependency | How to provide |
|---|---|
| PostgreSQL 18 + pgvector | `cd src && docker compose up -d` |
| LM Studio | Reachable at `http://llm-host.example:1234`; cap at **2 concurrent slots** ([[project_lmstudio_concurrency]]) |
| Embedding model | Configured via `IEmbeddingGenerator`; dimension must match the `records.embedding vector(N)` column |
| User secrets | `dotnet user-secrets list` from the Functional.Test project — verify connection string |

---

## 2. Test Infrastructure

The suite stands or falls on its fixtures. Without shared primitives,
~30 E2E tests collapse into ~30 copies of host-boot boilerplate.
**Build the fixtures first, then the tests.**

Five primitives, all under `Agency.Memory.Functional.Test/Infrastructure/`.

### 2.1 `MemoryE2EFixture` (xUnit `IAsyncLifetime`, class-scoped)

Responsibilities:

- Build an `IHost` via `Host.CreateApplicationBuilder().Services.AddAgencyMemory(...)`
  wiring **all four hosted services** + `PostgresMemoryStore` + a real
  `ILlmClient` + real `IEmbeddingGenerator` per [`Memory-Specifications.md`
  §10](Memory-Specifications.md).
- Truncate `records`, `watermarks`, `dead_letter`, `user_state` between
  tests using a per-test `userId` scoped to the test name. No shared state
  between tests; full DB isolation by partition key.
- Expose:
  - `IServiceProvider Services`
  - `IMemoryStore Store`
  - `Func<string userId, string sessionId, IChatSession> NewSession`
  - `Task WaitForDistillationAsync(string sessionId, TimeSpan timeout)` —
    polls `DistillationCompletedEvent`; default 60s, throws on timeout.
  - `Task WaitForConsolidationAsync(string userId, TimeSpan timeout)`.
  - `IReadOnlyList<AgentEvent> CapturedEvents` for assertions.

### 2.2 `SystemPromptCaptureHook`

A test-only `OnPreIteration` hook, composed **after** the memory baseline,
that snapshots `Context.Knowledge` and `Context.Memory` into a thread-safe
collection keyed by `(sessionId, iterationIndex)`. This is the canonical
way E2E tests assert "the system prompt contains X" — without parsing the
prompt string itself.

### 2.3 `InMemoryDeadLetterAssertions`

Helper that queries the `dead_letter` table by `(jobKind, userId)` and
asserts presence/absence with partial `error` substring match. Used by
Group 5.

### 2.4 `FaultInjectingLlmClient`

`ILlmClient` decorator that wraps the real client. Configurable per-test
to:

- Return HTTP 429 for the next *N* calls (transient).
- Return HTTP 400 once (permanent).
- Return malformed JSON once, then defer to the real client.
- Throw `HttpRequestException` on the next call (network/embedding down).

Real LLM is still called for happy turns; only the distiller/consolidator
calls are intercepted. Constructed via a marker interface so DI can swap
it in only for Group 5 tests.

### 2.5 `TimeShim`

Uses `Microsoft.Extensions.TimeProvider.Testing` to advance virtual time
without sleeping. Critical for Group 4 (Hygiene) — these tests advance
virtual time by `Ttl + 1.day` and trigger the sweep manually instead of
waiting 24h.

---

## 3. The Test Catalogue

~36 tests in eight scenario families. Each is anchored to a row in
[`Memory-Specifications.md` §2 (Use Cases)](Memory-Specifications.md),
§12 (Edge cases), or §13 (E2E flow).

Naming convention: `Scenario_PreconditionOrTrigger_ExpectedObservable`.
xUnit `[Fact]`; `[Trait("Category","Functional")]` mandatory;
`[Trait("Group","CaptureAndRecall")]` (or equivalent) to allow group-level
filtering via `--filter "Group=Hygiene"`.

### Group 1 — Capture & Recall

The headline scenarios: a memory written via one of the three triggers is
recalled in a later turn or session.

| # | Test | Spec |
|---|---|---|
| **E1.1** | `Fact_PythonPreference_RecalledInLaterSession` | U1, §13 |
| **E1.2** | `Memory_SslDebuggingOAO_WrittenOnGoalComplete_RecalledForSimilarIssue` | U2 |
| **E1.3** | `MarkGoalComplete_TriggersDistillation_LoopContinuesWithoutStopping` | §7.1, §6.7.2 |
| **E1.4** | `InactivityTimer_FiresAfterConfiguredTimeout_DistillationOccurs` | §7.1, §6.2 |
| **E1.5** | `SessionDispose_TriggersFinalDistillation_NoOpWhenWatermarkAdvanced` | §13 step 07 |
| **E1.6** | `ColdStart_EmptyStore_RetrievalReturnsNothing_SystemPromptNotesIt` | §6.4, §12.4 |
| **E1.7** | `SetFocus_NarrowsRetrievalQuery_FocusDomainRecordsRankHigher` | §6.7.1, §6.4 |
| **E1.8** | `SessionScopedRecord_VisibleFromOtherSessionButLowerRanked` | U6, §5, §8.3 |

#### E1.1 — `Fact_PythonPreference_RecalledInLaterSession`

- **Setup:** Empty store; `userId = "u-e11-{guid}"`.
- **Steps:**
  1. Open `ChatSession` "s1"; send `"I prefer Python."`.
  2. Await `DistillationCompletedEvent` for "s1" (timeout 60s).
  3. Dispose "s1"; await second `DistillationCompletedEvent` (expected no-op).
  4. Await `ConsolidationCompletedEvent` for `userId`.
  5. Open new `ChatSession` "s2"; send
     `"Write me a script to deduplicate this list."`.
- **Acceptance:**
  - `SystemPromptCaptureHook` for "s2" iteration 1 contains a Record whose
    `Title` or `Value` mentions "Python".
  - `Store.GetAllForUserAsync(userId).Count >= 1`.

#### E1.2 — `Memory_SslDebuggingOAO_WrittenOnGoalComplete_RecalledForSimilarIssue`

- **Setup:** Empty store; scripted ~10-turn debugging conversation where
  the user and agent diagnose an SSL handshake failure as a DNS validation
  issue.
- **Steps:**
  1. Send the scripted turns through "s1".
  2. Have the agent (via a test-controlled tool sequence) call
     `MarkGoalComplete`.
  3. Await `DistillationCompletedEvent`.
  4. Open "s2" for the same `userId`; send `"My API client is hanging on
     handshake — any ideas?"`.
- **Acceptance:**
  - A `Record` of `ContentType.Memory` exists with `Domain="Debugging"` (or
    similar) and `Value` containing all four OAO sections.
  - "s2" iteration 1 captured prompt contains that Memory in `## Memories`.

#### E1.3–E1.8

Authored to the same template; see catalogue rows above for spec anchors.
Each test:
- Uses a unique `userId`.
- Boots its own fixture state.
- Asserts at most one **causal** invariant.

### Group 2 — Privacy & Forget

| # | Test | Spec |
|---|---|---|
| **E2.1** | `AgentForgetTool_RemovesNamedFact_NextRetrievalDoesNotSurfaceIt` | U3 |
| **E2.2** | `ForgetMe_WipesAllUserData_OtherUsersUnaffected` | U4 |
| **E2.3** | `ForgetMe_MidSession_NextRetrievalEmpty_InFlightDistillationStillWritesNewRecord` | §12.4 |
| **E2.4** | `CrossUserIsolation_GlobalScopeNeverLeaksAcrossUserIds` | §17, Principle P3 |

### Group 3 — Consolidation

LLM-driven; each test waits for a `ConsolidationCompletedEvent`.

| # | Test | Spec |
|---|---|---|
| **E3.1** | `Consolidator_MergesContradiction_LatestStateRetained` | U3 + §6.3 |
| **E3.2** | `Consolidator_MergesDuplicateFacts_IntoSingleRecord` | U5, §6.3 |
| **E3.3** | `Consolidator_ExpandsSparseRecord_WithDetailFromNewer` | §6.3 "Expansion" |
| **E3.4** | `Consolidator_DeletesStaleLowImportanceMemory_ViaSubAgent` | §6.3, §8.4 |
| **E3.5** | `Consolidator_PerUserSerial_TwoSessionEndsCoalesceIntoOnePass` | §10.2, §12.1 |
| **E3.6** | `Consolidator_LargeCorpus_EmitsWarning_AndStillCompletes` | §6.3 V1, §12.4 |

### Group 4 — Hygiene

All four tests use `TimeShim` to advance virtual time past the TTL/stale
window. The hygiene sweep is triggered by `PeriodicTimer.Tick` unblocking.

| # | Test | Spec |
|---|---|---|
| **E4.1** | `HygieneSweep_MemoryOlderThanTtl_AndNotAccessed_Deleted` | §6.6 TTL, §8.5 |
| **E4.2** | `HygieneSweep_RetrievalResetsLastAccessedAt_PreservesRecord` | §6.6 reset |
| **E4.3** | `HygieneSweep_LowImportanceStaleAge_Deleted_HighImportancePreserved` | §6.6 importance pass |
| **E4.4** | `HygieneSweep_FactsWithNullTtl_NeverExpire` | §10.3 |

### Group 5 — Failure & Recovery

`FaultInjectingLlmClient` injects the failure; `InMemoryDeadLetterAssertions`
queries `dead_letter`.

| # | Test | Spec |
|---|---|---|
| **E5.1** | `DistillerCrash_WatermarkUnchanged_NextRunReprocessesTurnsExactlyOnce` | §12.2, §9 |
| **E5.2** | `Llm429Transient_RetriedWithExponentialBackoff_EventuallySucceeds` | §8.6 Transient |
| **E5.3** | `Llm400Permanent_DeadLetteredImmediately_DistillationFailedEventEmitted` | §8.6 Permanent |
| **E5.4** | `MalformedJson_OneParseRetryWithStricterPrompt_ThenDeadLetter` | §18.1, §8.6 |
| **E5.5** | `EmbeddingServiceDown_DistillationRetriesAndDeadLetters_DoesNotBlockHotPath` | §12.2 |
| **E5.6** | `ProcessRestart_LastWrittenAtCacheHydratedFromDb_OneTurnPenaltyOnly` | §12.2, §6.1 |

### Group 6 — Performance & Hot-path Discipline

| # | Test | Spec |
|---|---|---|
| **E6.1** | `HotPath_OnPreIteration_GateMiss_p95_UnderBudget_500ms` | §11.1 |
| **E6.2** | `MultiIterationTurn_GateSkipsAfterFirstIteration_NoExtraSearches` | §8.1 |
| **E6.3** | `DistillerWriteDuringSession_NextIterationInvalidatesGate_AndResearches` | §8.1 |

Latency tests collect samples via `Activity` events emitted by the
retrieval engine, then compute p50/p95 over ≥30 samples. They are
**advisory** — flake-prone on slow LLM days — and tagged additionally with
`[Trait("Profile","Latency")]` so they can be skipped in unstable
environments.

### Group 7 — Hook Composition

| # | Test | Spec |
|---|---|---|
| **E7.1** | `UserHookComposedAfterBaseline_SeesEnrichedContextKnowledgeAndMemory` | §6.5 default ordering |
| **E7.2** | `ComposeBefore_UserHookSeesEmptyContext_BeforeRetrieval` | §6.5 escape hatch |

### Group 8 — Concurrency

| # | Test | Spec |
|---|---|---|
| **E8.1** | `TwoMarkGoalCompleteCalls_BothEnqueueJobs_SecondNoOpsByWatermark` | §12.1 |
| **E8.2** | `DistillerWritesDuringRetrievalSearch_MvccSnapshotConsistent_NextRetrievalSeesNew` | §12.1 |
| **E8.3** | `ConsolidatorDeletesDuringRetrieval_TransientStaleRecordOneTurn_RecoveredNextTurn` | §12.1 |

---

## 4. Test Authoring Rules

- Each test uses a **unique `userId`** of the form `{TestName}-{Guid}` so
  failed-run residue cannot poison the next run.
- Each test boots its own `MemoryE2EFixture` (or shares a class-scoped
  fixture if cheap to reset). **No xUnit `[Collection]`-shared state across
  test classes** — Group 8 deliberately exercises concurrency.
- Real LLM is the default. Only Group 5 swaps in `FaultInjectingLlmClient`.
  Real embeddings always.
- Asserts go through the public surface only: store rows, `AgentEvent`s,
  captured system prompts via `SystemPromptCaptureHook`. **No reflection
  into internals.**
- Per [`CLAUDE.md` §3 (Surgical Changes)](../CLAUDE.md), no "improvements"
  to adjacent memory code while authoring tests — file follow-up issues
  instead.
- Each test asserts at most one **causal** invariant. Multi-assertion is
  OK only when the assertions describe the same outcome (e.g. "row exists"
  AND "event emitted with matching id").
- Test names: `Scenario_PreconditionOrTrigger_ExpectedObservable`.
- XML doc comments on every test method per
  [`CLAUDE.md` §C# Conventions](../CLAUDE.md).
- No `yield return` inside `try`/`catch` (does not compile in C#).
- `OperationCanceledException` from `stopToken` must always propagate
  unchanged; tests must not assert dead-letter behaviour for cancellation
  per [`Memory-Specifications.md` §8.6](Memory-Specifications.md).

---

## 5. Run Modes & CI

### Local development

```powershell
# Full suite
dotnet test src/Memory/Agency.Memory.Functional.Test --filter "Category=Functional"

# Single group
dotnet test src/Memory/Agency.Memory.Functional.Test --filter "Category=Functional&Group=Capture"

# Skip latency-sensitive tests
dotnet test src/Memory/Agency.Memory.Functional.Test --filter "Category=Functional&Profile!=Latency"
```

Add a short `README.md` to `src/Memory/Agency.Memory.Functional.Test/`
listing the prerequisites (Postgres up, LM Studio reachable, user secrets)
and the filter examples above.

### CI

The suite is **not** part of the default CI run. A nightly job (out of
scope for this plan; tracked as a follow-up) executes it against the
shared LM Studio host. Document this explicitly so reviewers don't expect
green-on-PR for E2E tests.

---

## 6. Tracker Integration

Extend [`Memory-Tracker.md`](Memory-Tracker.md) with a new section after
the existing Workstream G status table:

```markdown
## E2E Suite Status (Memory-TestPlan.md)

| Test | Active | In Progress | Done |
|---|:---:|:---:|:---:|
| E1.1 Fact_PythonPreference_RecalledInLaterSession | ✔ | | |
| E1.2 Memory_SslDebuggingOAO_WrittenOnGoalComplete_RecalledForSimilarIssue | ✔ | | |
| ... (one row per E*.* above) ...
| E8.3 ConsolidatorDeletesDuringRetrieval_TransientStaleRecordOneTurn_RecoveredNextTurn | ✔ | | |
```

Convention is the same as the existing tracker. For E2E tests the test
body **is** the deliverable — there's no separate `T` and impl pair.

### Relation to the original `Memory-ProjectPlan.md` Workstream G

The five tasks G.1–G.5 in the project plan map to entries in this catalogue:

| Project Plan | E2E Plan |
|---|---|
| G.1 | E1.1 |
| G.2 | E2.2 |
| G.3 | E6.1 |
| G.4 | E5.1 |
| G.5 | E3.1 |

When the E2E test for an entry is authored, mark the corresponding G.*
task **Done** in [`Memory-Tracker.md`](Memory-Tracker.md) with a pointer
to its E*.* row. Do not edit [`Memory-ProjectPlan.md`](Memory-ProjectPlan.md)
itself.

---

## 7. Verification

The plan is verified when:

1. [`Memory-TestPlan.md`](Memory-TestPlan.md) (this file) is reviewed and
   committed.
2. [`Memory-Tracker.md`](Memory-Tracker.md) reflects the new suite section
   from §6.
3. The `Agency.Memory.Functional.Test` project (scaffolded by Task G.0
   in [`Memory-ProjectPlan.md`](Memory-ProjectPlan.md)) builds and runs
   zero tests with exit code 0 against an empty suite — proving the
   project scaffold is suite-ready.
4. The five `Infrastructure/` primitives from §2 are implemented before
   any E*.* test body.
5. Each E*.* test from §3, once authored, passes against real LM Studio +
   real Postgres at least three consecutive runs.

---

## 8. Out of Scope (deferred to follow-up)

- Nightly CI job configuration.
- Load testing (concurrent users, sustained throughput).
- Fuzz testing of the episode-extraction JSON parser.
- Cross-deployment / cross-region scenarios.
- Tests for V2 features (Turn-tier, Reflections) listed in
  [`Memory-Specifications.md` §16](Memory-Specifications.md).

---

*End of E2E test plan.*
