# Long-Term Memory & Asynchronous Distillation — Design Specifications (HLD)

**Status:** Draft — ready for technical-spec breakdown
**Audience:** Implementers, reviewers, future maintainers
**Scope:** `Agency.Harness`, `Agency.Mcp.Memory`, supporting infrastructure
**Companion documents:**
- `Memory-FuctionalSpec.md` — functional requirements (source of behavioural truth)
- `questions.md`, `OpenItems.md` — architectural decision logs

**Last revised:** 2026-06-02 — reconciled with the shipped implementation: hygiene sweep uses the injected `TimeProvider` clock (§6.6, §8.5); service-unreachable failures are transient/retried (§8.6, §12.2); distiller emits a terminal `DistillationSettledEvent` base and suppresses thinking (§6.2, §18.1, now V2); consolidator emits a user-facing `MemoryMutatedEvent` per mutation (§6.3) and the reconciliation prompt gains a structural DELETE rule (§18.2, now V2).

---

## 1. Goal

Endow `Agency.Harness` agents with a **durable, cross-session memory** that allows them to recall facts about users and domains, remember past lived experience, and improve over time — **without compromising the latency of the agent's hot path**.

The system answers three questions for every agent turn:

1. **What does this agent need to know right now?** (Retrieval — read path.)
2. **What just happened that's worth remembering?** (Distillation — write path.)
3. **What does the long-term store look like over time?** (Consolidation + Hygiene — maintenance.)

Success is measured by three properties:

| Property | Measurement |
|---|---|
| **Recall** | An agent in session *N+1* answers correctly using a fact established in session *N*. |
| **Latency neutrality** | The end-to-end turn latency in a memory-enabled session is statistically indistinguishable from a memory-disabled session (excluding the *additive* cost of retrieval injection in the system prompt). |
| **Durability** | The store survives process restarts, can be replicated, and supports a full per-user wipe (GDPR-style `Forget-Me`). |

This is **not** a chat-history feature. The conversation manager already handles in-session history. This system addresses what survives *between* sessions and what gets *learned* from them.

---

## 2. Example Use Cases

| # | Scenario | What happens |
|---|---|---|
| **U1** | User says "I prefer Python." in session 1. Three weeks later, in session 2, asks "Write me a script to deduplicate this list." | Distillation extracts a Fact (`Domain=Preferences, Key=Language, Value="Python"`) at the end of session 1. Retrieval in session 2 surfaces it. Agent writes Python without asking. |
| **U2** | User and agent debug a flaky SSL handshake over a 40-turn session, ultimately discovering it was a DNS validation issue. | At `MarkGoalComplete`, distillation writes a Memory (`Domain=Debugging, Tags=[ssl, dns]`) summarising the trajectory. Three months later, a new SSL issue: retrieval surfaces the prior episode; agent suggests checking DNS first. |
| **U3** | User says "Actually, ignore what I said about preferring Postgres — I switched to SQLite." | Agent calls `Forget(domain="Preferences", key="Database")` on the stale record. The next distillation writes the new preference. Consolidator at session end notices no near-duplicates. |
| **U4** | User says "Delete everything you know about me." | `/forget-me` UI command (not an agent tool) wipes all Records for the `UserId`. |
| **U5** | Agent has produced 12 episodes in the "Auth Debugging" domain over the past month, half of which contradict each other. | Consolidator at next session-end merges overlapping episodes, marks resolved-issue episodes lower-importance, removes superseded ones. |
| **U6** | Agent retrieves 10 candidate memories for a query; 3 are session-tagged from this session, 2 are user-global, 5 are session-tagged from older sessions. | Ranking surfaces this-session matches highest (sessionMatch=1), user-global next (recent + important), older session-tagged last (still visible, lower-ranked). |

These cases drive the design decisions throughout this document.

---

## 3. Non-Goals

The following are **explicitly out of scope for v1**:

| Out of scope | Reason |
|---|---|
| **Per-turn (Turn-tier) extraction** | Granularity below Episode level adds storage volume and LLM cost without proven retrieval value. Deferred until usage data justifies it. |
| **Reflection synthesis across episodes** | Pattern-mining is a v2 capability. The Consolidator covers minimal cross-episode reconciliation. |
| **Procedural memory / skill library** (Voyager-style) | Distinct system shape; out of scope. |
| **Hierarchical summarisation as a separate mechanism** | The Consolidator subsumes it. |
| **Human-in-the-loop dashboard / management UI** | `Forget` + `Forget-Me` + Consolidator cover the operational surface. UI is a host-app concern, not a library concern. |
| **Cross-user `Global` memory** | All Records are partitioned by `UserId`. The "Global" scope means "across all sessions of *this user*", never cross-user. |
| **Cross-agent / cross-deployment memory sharing** | No federation, no export, no import. Single deployment owns the data. |
| **Memory transfer or export between deployments** | Same as above. |
| **Memory import from external sources** | No bulk-load path. |
| **Importance scoring decay over time** | `Importance` is fixed at write time. Recency in the ranking formula does the temporal-relevance job. |
| **Token-budgeted truncation of injected memory** | `RetrievalTopK` is the v1 control. Token-aware truncation may be added in v2 after measurement. |
| **PII redaction at distillation time** | Out of scope; the user remains responsible for what they say. |
| **Per-user encryption at rest** | Relies on transport- and storage-level encryption. |

Drawing this line is itself a load-bearing design decision: every item above represents a feature that would expand v1's surface area significantly. The v1 surface is deliberately narrow so that the **capture / store / retrieve / consolidate loop** can be measured against real usage before adding mechanisms.

---

## 4. Design Principles

The system is governed by six principles. Every component-level decision in §6–§14 traces back to one of them.

### P1 — Hot path is sacred

No memory operation that runs during a user-facing LLM turn may add latency proportional to the size of the store. Retrieval is gated so it runs once-per-turn at most; distillation never runs during a turn; consolidation runs only after sessions end; hygiene runs on a separate schedule.

**Why this matters:** the harness already streams `IAsyncEnumerable<AgentEvent>` and emits OAO triples per turn. Any new mechanism that puts CPU/IO on the per-iteration path will be felt as a regression by every downstream caller. We push *all* memory work to background services.

### P2 — Capture is system-owned

The agent has **no commit tool**. It cannot decide that something is "memorable" — that decision is made by the Distiller after the fact. The agent may *signal* timing (via `MarkGoalComplete`) and *narrow scope* (via `SetFocus`), but never authors a memory directly.

**Why this matters:** Agent-driven memory writes (MemGPT-style) inflate every turn with tool-call overhead and force the model to second-guess what's important *while* solving the task. Background-only capture (Generative-Agents / Letta sleep-time / Nemori-style) decouples capture from solution.

### P3 — Soft signals over hard isolation

`SessionId` is a *ranking signal*, not a *partition*. Records tagged with a session remain visible from other sessions; they just rank lower. There is no scope-based filtering at the SQL level beyond `UserId`.

**Why this matters:** session boundaries are often more porous than the user expects ("this is the same problem we hit last Tuesday"). Hard isolation would force the agent to repeat work. Soft signalling preserves cross-session continuity while still preferring locally-relevant Records.

### P4 — LLM-driven decisions where ambiguity is intrinsic

Distillation, scope assignment, importance scoring, and consolidation are all driven by an LLM, not by heuristics. The reasoning required is genuinely natural-language reasoning, and the cost is amortised across a background context.

**Why this matters:** Importance heuristics introduce designer bias. Similarity-threshold-only consolidation produces brittle merge decisions. The LLM is the right tool for "is this the same thing said differently?" — but only off the hot path.

### P5 — Single source of truth

There is **one** `Record` type, **one** vector collection, **one** memory store interface. `ContentType {Fact, Memory}` is a discriminator column, not a schema branching point. The conversation manager is the source of truth for transcript content; the Channel carries only the *intent to distill*, not the data.

**Why this matters:** duplicated storage paths produce inconsistent retrieval and double-write headaches. We keep one shape and one route.

### P6 — Observability is built-in, not bolted-on

Every background service has an `ActivitySource`, a `Meter`, counters, and structured logs. Errors emit `AgentEvent`s, not silent failures. Dead-letter persistence on final distillation failure means nothing is lost.

**Why this matters:** background systems that fail silently produce phantom bugs ("the agent doesn't remember things"). We need to be able to point at a metric, a trace, or a row in `dead_letter` for every observable behaviour.

---

## 5. Architecture Overview

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                  HOT PATH — synchronous, latency-critical                   │
│                                                                             │
│  User msg ──► ChatSession.SendAsync ──► Agent.RunAsync (loop)              │
│                            │                                                │
│                            ├── OnUserPromptSubmit (audit / enrich)         │
│                            │                                                │
│                            ├── OnPreIteration (RETRIEVAL — gated)          │
│                            │     │                                          │
│                            │     ├─[gate]─► skip if store unchanged        │
│                            │     └─► IMemoryStore.SearchAsync              │
│                            │         └─► ranking → Context.Knowledge       │
│                            │                       Context.Memory          │
│                            │                                                │
│                            ├── SystemPromptBuilder.Build(ctx)              │
│                            ├── LLM call (Strong-tier)                      │
│                            ├── OnAssistantTurn ► TIMER RESTART (only)     │
│                            ├── tool batch  ► OnPostToolBatch              │
│                            └── ... loop until StopCondition                │
│                                                                             │
└──────────────────────────────────────┬──────────────────────────────────────┘
                                       │
                                       │  Triggers (3, see §7.1):
                                       │    1. MarkGoalComplete tool
                                       │    2. Inactivity timer expiry
                                       │    3. ChatSession.Dispose
                                       │
                                       ▼  Channel<DistillationJob> (per session)
┌─────────────────────────────────────────────────────────────────────────────┐
│              COLD PATH — asynchronous, off the hot path                     │
│                                                                             │
│   InactivityTimerService ─┐                                                 │
│   (per-session timer)     │                                                 │
│                           ▼                                                 │
│   DistillerBackgroundService                                                │
│   ├─ dequeue DistillationJob                                                │
│   ├─ read turns (LastDistilledTurnIndex, currentEnd]                       │
│   ├─ LLM Episode-extraction (Cheap-tier OK)                                │
│   ├─ IEmbeddingGenerator.GenerateEmbeddingAsync                            │
│   ├─ IMemoryStore.UpsertAsync                                              │
│   ├─ advance LastDistilledTurnIndex                                        │
│   └─ emit DistillationCompletedEvent / DistillationFailedEvent             │
│         │                                                                   │
│         │ on SessionDisposed trigger ─► after final episode written:       │
│         ▼                                                                   │
│   ConsolidatorBackgroundService                                             │
│   ├─ spin up a sub-agent session                                           │
│   ├─ load all Records for this user                                        │
│   ├─ provide Merge/Update/Delete/Done tools                                │
│   └─ run until sub-agent calls Memory_Done                                 │
│                                                                             │
│   HygieneSweeperBackgroundService (daily cron-style)                       │
│   ├─ TTL pruning                                                           │
│   └─ Importance pruning                                                    │
│                                                                             │
└──────────────────────────────────────┬──────────────────────────────────────┘
                                       │
                                       ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                  STORAGE — PostgreSQL + pgvector                            │
│                                                                             │
│   records                   watermarks            dead_letter               │
│   ──────────────────        ─────────────         ──────────────            │
│   id           uuid PK      user_id    text       id           uuid PK     │
│   user_id      text         session_id text       user_id      text        │
│   session_id   text?        last_idx   int        session_id   text?       │
│   content_type smallint     PK(user_id,           job_payload  jsonb       │
│   domain       text             session_id)       error        text        │
│   key          text                               created_at   tstz        │
│   title        text                                                         │
│   value        text                               user_state               │
│   tags         text[]                             ──────────────            │
│   importance   double                             user_id      text PK     │
│   embedding    vector(N)                          last_written_at tstz     │
│   created_at   tstz                                   NOT NULL             │
│   updated_at   tstz                                                         │
│   last_access  tstz?                                                        │
│                                                                             │
│   INDEXES:                                                                  │
│     - PK on id                                                              │
│     - UNIQUE (user_id, session_id, domain, key)  ← upsert key              │
│     - btree (user_id, content_type)                                         │
│     - btree (user_id, domain)                                               │
│     - HNSW (embedding vector_cosine_ops)                                    │
│     - btree (updated_at) for TTL sweeps                                     │
└─────────────────────────────────────────────────────────────────────────────┘
```

Key invariants the diagram implies:

- **Three triggers**, exactly. Nothing else queues distillation work.
- **Three background services**: Distiller, Consolidator, Hygiene Sweeper. They share the same `IMemoryStore` but never directly call each other.
- **One vector collection**. `ContentType` is a filter column.
- **One queue per session**, not per user — to keep work parallelisable while bounding per-session backpressure.

---

## 6. System Components

Each subsystem is detailed below in a uniform shape: **Purpose / Responsibilities / Inputs+Outputs / Internal flow / Implementation notes / Constraints / V1 vs V2 / Test surface (TDD-first)**.

### 6.1 The Memory Store (`IMemoryStore`)

#### Purpose

A vector-backed, user-partitioned, content-typed store of `Record` items. Single source of truth for all durable memory.

#### Responsibilities

- Persist Records with embeddings.
- Search by query embedding with metadata filters.
- Maintain per-user "last-write" timestamps to power the retrieval gate (§8.1).
- Apply hard deletes for `Forget(domain, key)` and `Forget-Me`.
- Track `LastAccessedAt` on retrieval to support TTL decay reset.

#### Inputs / Outputs

| Operation | Inputs | Outputs |
|---|---|---|
| `UpsertAsync(Record)` | `Record` (with embedding) | `Record` (with assigned `Id`, timestamps) |
| `SearchAsync(SearchQuery)` | embedding, optional `ContentType`/`Domain`/`UserId` filters, `topK` | `IReadOnlyList<SearchHit>` |
| `GetByKeyAsync(userId, sessionId, domain, key)` | upsert-key tuple | `Record?` |
| `ForgetAsync(userId, domain, key)` | tuple | bool |
| `ForgetMeAsync(userId)` | userId | `int` (deleted count) |
| `LastWrittenAtAsync(userId)` | userId | `DateTimeOffset?` |

#### Internal flow

For `UpsertAsync`:

1. If `Record.Embedding` is empty, generate via `IEmbeddingGenerator` (write-time embedding generation per Q6.3).
2. Compute upsert-key `(UserId, SessionId, Domain, Key)`.
3. Begin transaction:
   - `INSERT ... ON CONFLICT (upsert_key) DO UPDATE SET ...` returning the assigned `id`.
   - Update the per-user `last_write` timestamp (in-memory cache + persisted column on a `user_state` table).
4. Commit.

For `SearchAsync`:

1. Build SQL with `WHERE user_id = ?` plus optional `content_type IN (...)`, optional `domain IN (...)`.
2. `ORDER BY embedding <=> :query_vec LIMIT topK * over_fetch_factor`.
3. Return `SearchHit { Record, Similarity }`. **Ranking beyond similarity is the caller's job.**
4. After return, asynchronously bump `last_accessed_at` for the hits (fire-and-forget batched UPDATE).

#### Implementation notes

- **Backend**: PostgreSQL 18 + pgvector (existing infra).
- **Embedding model**: configured via `IEmbeddingGenerator`. Dimension must match the column declaration; changes require a migration (out of scope per Q6.5 "rebuild on breaking change").
- **HNSW index**: `vector_cosine_ops`, parameters tuned later. Cosine distance because all our distances are cosine-similarity-derived.
- **`SessionId` is a column, not a partition key.** Searches *include* records with non-matching `SessionId`. Ranking handles preference.
- **`last_write` cache**: stored in a small in-memory `ConcurrentDictionary<string, DateTimeOffset>` keyed by `userId`, hydrated lazily, written through on every mutation. The cache is the source of truth for the retrieval gate; the persisted column is the durability backup on process restart.
- **`tags`** stored as native PostgreSQL `text[]`. Indexed only if usage shows we filter on tags often (v2).
- **Embeddings** stored only in the database, never serialised over the network as JSON. `pgvector` handles wire format.

#### Constraints

- Hard delete only. No tombstone, no archive table in v1.
- No tenant-level (cross-user) operations except admin scripts.
- The store does **not** consolidate. ADD/UPDATE/SKIP semantics live in the Consolidator (§6.3).

#### V1 vs V2

| Aspect | V1 | V2 |
|---|---|---|
| Schema versioning | Rebuild on breaking change | Migration tooling |
| Tag indexing | None | GIN on `tags` if profiling demands |
| Sharding | Single Postgres instance | Per-user-bucket sharding |
| Soft delete | No | Optional audit/archive |

#### Test surface (TDD-first)

| Test task | Implementation task |
|---|---|
| `Upsert_NewRecord_AssignsIdAndTimestamps` | Implement `UpsertAsync` insert path |
| `Upsert_ExistingKey_OverwritesValueAndPreservesId` | Implement `UpsertAsync` update path |
| `Search_FiltersByContentType` | Implement filter clauses |
| `Search_RanksByCosineDistance` | Validate HNSW behaviour |
| `Search_BumpsLastAccessedAt` | Implement async access tracking |
| `Forget_KnownKey_DeletesAndReturnsTrue` | Implement `ForgetAsync` |
| `Forget_UnknownKey_ReturnsFalse` | Negative path |
| `ForgetMe_AllRecordsDeletedForUser_DoesNotAffectOtherUsers` | Multi-user isolation test |
| `LastWrittenAt_ReflectsMostRecentUpsert` | Drives retrieval gate correctness |
| `LastWrittenAt_AfterDelete_Updates` | Cover the delete path of the gate |

---

### 6.2 The Distiller (`DistillerBackgroundService`)

#### Purpose

Convert recent conversation turns into one or more durable `Record`s, asynchronously, off the agent's hot path.

#### Responsibilities

- Dequeue `DistillationJob`s from per-session bounded channels (see §10.2 for design rationale).
- Read the relevant conversation turns from the session's `IConversationManager`.
- Invoke an LLM to extract Episode/Fact Records (Markdown body, Title, Domain, Tags, Scope, Importance).
- Embed and persist via `IMemoryStore`.
- Advance the session watermark (`LastDistilledTurnIndex`).
- Emit success/failure `AgentEvent`s.
- Retry on transient failure; dead-letter on permanent failure.

#### Inputs / Outputs

| In | Out |
|---|---|
| `DistillationJob { UserId, SessionId, Trigger, UpToTurnIndex }` | 0..N `Record` writes via `IMemoryStore` |
| Session's `IConversationManager` (read-only) | `DistillationCompletedEvent` / `DistillationFailedEvent` (both derive from terminal base `DistillationSettledEvent`) |

#### Internal flow

```
loop:
  // Drain all per-session channels before suspending.
  processed = false
  for each (sessionId, channel) in channelRegistry.GetAll():
    while channel.Reader.TryRead(out job):
      process(job)
      processed = true
  if not processed:
    // Suspend until a NotifyingChannelWriter releases the work semaphore.
    await channelRegistry.WaitForWorkAsync(stopToken)

process(job):
  using activity = ActivitySource.StartActivity("memory.distill", Internal)
  turns = conversations.Get(job.SessionId).MessagesBetween(watermark, job.UpToTurnIndex)
  if turns is empty: skip + log
  attempt = 0
  while attempt < MaxRetries:
    try:
      payload  = await llm.SendAsync(EpisodeExtractionPrompt(turns, focus, knownDomains, recentFacts))
      records  = ParseExtraction(payload)            // 0..N records
      foreach r in records:
        r.Embedding = await embedder.GenerateAsync(r.Title + "\n\n" + r.Value)
        await store.UpsertAsync(r)
      session.LastDistilledTurnIndex = job.UpToTurnIndex
      emit DistillationCompletedEvent
      break
    catch transient ex:
      attempt++
      await Task.Delay(RetryBaseDelay * 2^attempt)
    catch fatal ex:
      await deadLetter.WriteAsync(job, ex)
      emit DistillationFailedEvent
      break
```

#### Implementation notes

- **The channel carries `DistillationJob`, not OAO records.** Per-turn enqueueing is explicitly *not* the model (§7.2 of the functional spec). `OnAssistantTurn` only restarts the inactivity timer.
- **`UpToTurnIndex`** is a snapshot at trigger time. Concurrent new turns arriving after the snapshot are handled by a later job, not the current one.
- **Conversation manager access** is read-only from the Distiller's perspective. The manager is owned by the session and is concurrent-safe for reads.
- **Per-session sessions dictionary** (`IDistillerSessionRegistry`): a `ConcurrentDictionary<string, DistillerSessionState>` holding the watermark and the inactivity timer reference. Pruned on session disposal.
- **Embedding text** is `Title + "\n\n" + Value` — title alone is too sparse, value alone loses the semantic anchor. Configurable.
- **Goal-complete vs. inactivity vs. dispose** all use the same Episode extraction prompt; only metadata differs. The prompt is given `Trigger` as context so it can word the Episode appropriately ("Goal achieved: …" vs. "Session ended without explicit goal completion: …").
- **Thinking suppression (TI-8.2).** The distiller's chat client sets `LlmClientOptions.SuppressThinking = true` and the extraction prompt opens with a `/no_think` directive (§18.1). Episode extraction is deterministic JSON authoring that does not benefit from chain-of-thought, and the suppression avoids thinking-token latency on the large prompt.
- **Terminal-event symmetry (TI-8.1).** `DistillationCompletedEvent` and `DistillationFailedEvent` both derive from an abstract `DistillationSettledEvent`. Because `InMemoryEventBus` dispatches polymorphically, a consumer can `Subscribe<DistillationSettledEvent>` to observe a job settling on **either** outcome — so a waiter never hangs when a job fails. This mirrors the Consolidator always emitting `ConsolidationCompletedEvent`.

#### Constraints

- **Idempotency by watermark**: re-running the same job (same `UpToTurnIndex`) when the watermark has already passed is a no-op. Safe to retry across process restarts.
- **No cross-session work** in a single job. Per-user consolidation is the Consolidator's job (§6.3).
- **Bounded channel** (`PerSessionQueueCapacity = 32`, policy = `DropOldest`). A buggy producer cannot OOM the process.

#### V1 vs V2

| Aspect | V1 | V2 |
|---|---|---|
| Pipeline tiers | Episode only | Turn-tier + Reflection-tier |
| Model selection | Single model per Distiller | Per-tier model routing |
| Prompt | Static template | Few-shot, dynamic exemplars |
| Cost optimization | None | Batched extraction, distillation of multiple sessions in one call |

#### Test surface (TDD-first)

| Test task | Implementation task |
|---|---|
| `Distill_WithSyntheticTurns_WritesOneEpisode` | Implement core extraction path |
| `Distill_AdvancesWatermark` | Watermark progression |
| `Distill_HonoursWatermark_DoesNotReprocessTurns` | Idempotency guard |
| `Distill_OnTransientLlmFailure_RetriesUpToMax` | Retry loop |
| `Distill_OnPermanentFailure_WritesToDeadLetter` | Dead-letter path |
| `Distill_OnGoalCompleteTrigger_EmitsCompletedEventWithTriggerTag` | Event emission |
| `OnAssistantTurn_RestartsTimer_DoesNotEnqueueJob` | Hot-path discipline (negative test) |
| `Timer_Expiry_EnqueuesInactivityJob` | Timer integration |
| `SessionDispose_EnqueuesFinalJobAndCancelsTimer` | Lifecycle correctness |
| `Channel_AtCapacity_DropsOldest` | Backpressure |

---

### 6.3 The Consolidator (`ConsolidatorBackgroundService`)

#### Purpose

Periodically reason over a user's existing Records and resolve duplication, contradiction, and decay — using an LLM agent.

#### Responsibilities

- After each session ends, run one consolidation pass for the affected `UserId`.
- Load Records, spin up a sub-agent session with consolidation tools, let it run to completion, dispose.
- Apply the sub-agent's Merge/Update/Delete tool calls back to `IMemoryStore`.

#### Inputs / Outputs

| In | Out |
|---|---|
| `ConsolidationJob { UserId, TriggeredBySessionId }` | Idempotent mutations on `IMemoryStore`; `MemoryMutatedEvent` per mutation; `ConsolidationCompletedEvent` on completion |

#### Internal flow

```
on SessionDistillationCompleted(userId, sessionId):
  await consolidatorChannel.Writer.WriteAsync(new ConsolidationJob(userId, sessionId))

loop:
  job = await consolidatorChannel.Reader.ReadAsync(stopToken)
  using activity = ActivitySource.StartActivity("memory.consolidate", Internal)
  records = await store.GetAllForUserAsync(job.UserId)
  if records.Count == 0: continue

  subAgent = ConsolidatorSubAgentFactory.Create(opts.Model, ConsolidationTools(store, userId))
  ctx      = Context.For(consolidationPromptFor(records))
  await foreach evt in subAgent.RunAsync(ctx):
    // the tools already mutate the store directly; for each successful
    // Merge/Update/Delete tool call we also emit a MemoryMutatedEvent (TI-8.3)
    if evt is ToolInvokedEvent t and t maps to Merge/Update/Delete and not t.Result.IsError:
      emit MemoryMutatedEvent(userId, operation, t.Result.Content)
  
  emit ConsolidationCompletedEvent
```

`MemoryMutatedEvent` is a first-class, user-facing observable (TI-8.3): hosts subscribe to it to tell the user when the agent has autonomously reorganised its own long-term memory. The consolidator also logs each mutation at Information level. This is an intentional transparency feature for this product — memory edits the user never typed should be visible.

#### Tools given to the consolidator sub-agent

| Tool | Signature | Effect |
|---|---|---|
| `Memory_Merge` | `(recordIds: string[], newRecord: Record)` | Deletes the listed records, inserts the new combined record. Atomic. |
| `Memory_Update` | `(recordId, newValue, newImportance?)` | Updates `Value`, optionally `Importance`. Refreshes `UpdatedAt`. |
| `Memory_Delete` | `(recordId)` | Hard delete. |
| `Memory_Done` | `()` | Signals stop; the consolidator session terminates. |

These tools are **only** registered for the Consolidator sub-agent, never for the primary agent.

#### Implementation notes

- **The Consolidator is itself an Agent** built on `Agency.Harness`. It uses the same harness primitives (Context, ToolRegistry, hooks, stop conditions) as a user-facing agent — but with a different tool set and a different system prompt.
- **Stop condition**: `Memory_Done` called, or max iterations reached, whichever comes first.
- **Model**: configured separately (`ConsolidatorOptions.Model`). May be the same as Distiller or different; typically Strong-tier because the reasoning is subtle.
- **Loading "all records"**: in v1, no scale bound. For users with >`MaxRecordsPerPass` records, the spec notes deferred work (per-domain batching) but does not implement it. A warning log is emitted if the threshold is exceeded.

#### Constraints

- **Per-user serial**: at most one consolidation pass running per user at any time. Multiple session-end triggers for the same user collapse into one pending job.
- **Read-modify-write hazard**: while the Consolidator is reading, a separate session could be writing a new Record. Mitigation: the sub-agent operates on a snapshot, and its mutations use `id`-based addressing — concurrent inserts simply aren't in the snapshot and won't be touched.
- **Crash mid-run**: each tool call is its own transaction. A partial run is recoverable; the next session-end re-triggers consolidation.

#### V1 vs V2

| Aspect | V1 | V2 |
|---|---|---|
| Scale strategy | Full corpus into one prompt | Per-domain passes, batched fallbacks |
| Trigger | On session end | Threshold-based (record-count delta) + schedule |
| Decision strategy | LLM-only | Deterministic prefilter + LLM where uncertain |
| Cross-batch insight loss | Accepted; documented in `OpenItems.md` Item 4 | Coalescer step that reads merge logs |

#### Test surface (TDD-first)

| Test task | Implementation task |
|---|---|
| `Consolidate_NoRecords_ExitsImmediately` | Empty-store guard |
| `Consolidate_DuplicateFacts_MergesIntoOne` | End-to-end merge via synthetic LLM stub |
| `Consolidate_ContradictoryFacts_UpdatesToLatest` | Update tool wiring |
| `Consolidate_StaleLowImportanceMemory_Deletes` | Delete tool wiring |
| `Consolidate_PerUser_DoesNotTouchOtherUsers` | Multi-tenant isolation |
| `Consolidate_ConcurrentTriggersForSameUser_Coalesce` | Serial-per-user guarantee |
| `Consolidate_LargeCorpus_LogsWarningAndProceeds` | Scale-overflow signal |
| `Consolidate_PartialFailure_LeavesValidatableState` | Crash recovery |

---

### 6.4 The Retrieval Engine

#### Purpose

Surface the most relevant Records for the current iteration and inject them into `Context.Knowledge` and `Context.Memory`.

#### Responsibilities

- Decide whether to retrieve at all (gate, §8.1).
- Build the retrieval query from the user message plus `Context.Focus`.
- Search the store.
- Rank candidates by the composite scoring formula.
- Partition the top-K by `ContentType` and assign to the right Context property.

#### Inputs / Outputs

| In | Out |
|---|---|
| `Context` (focus, conversation history, watermarks) | Mutated `Context.Knowledge`, `Context.Memory`, `Context.MemoryLastRetrievedAt` |

#### Internal flow

```
inside OnPreIteration hook:
  lastWritten = await store.LastWrittenAtAsync(ctx.User.Id)
  if ctx.MemoryLastRetrievedAt is not null and lastWritten <= ctx.MemoryLastRetrievedAt:
    return                                            // GATE: skip

  query   = BuildQuery(ctx.Conversation.LastUserMessage, ctx.Focus)
  qVec    = await embedder.GenerateAsync(query)
  hits    = await store.SearchAsync(new SearchQuery(
              userId: ctx.User.Id,
              queryEmbedding: qVec,
              topK: opts.RetrievalTopK * opts.OverFetchFactor))
  scored  = hits.Select(h => (Hit: h, Score: ComputeScore(h, ctx.Session.Id, now)))
                .OrderByDescending(x => x.Score)
                .Take(opts.RetrievalTopK)

  ctx.Knowledge = new(Records: scored.Where(s => s.Hit.Record.ContentType is Fact).Select(s => s.Hit.Record).ToList())
  ctx.Memory    = new(Records: scored.Where(s => s.Hit.Record.ContentType is Memory).Select(s => s.Hit.Record).ToList())
  ctx.MemoryLastRetrievedAt = now
```

#### Implementation notes

- **Over-fetch factor** (default 3×): retrieve more candidates than `topK` from the store, then re-rank with the full formula. Pure vector similarity is not the final ranking signal.
- **Query composition**: free text from the last user turn + `Focus.Title + Focus.Domain + " ".Join(Focus.Tags)`. Focus terms get appended to bias retrieval without forcing exact-match.
- **System prompt rendering**: see §8.4 of the functional spec. Each Record is rendered with a human-readable recency string ("Updated 3 days ago"). The LLM never sees raw timestamps or scores.
- **Empty results**: both `Context.Knowledge` and `Context.Memory` become empty lists. `SystemPromptBuilder` may insert a "No relevant memories yet." note when both are empty for explicitness.

#### Constraints

- **Cannot be called outside `OnPreIteration`** in v1. Other call sites would defeat the gate.
- **No paging.** `RetrievalTopK` is the cap; v1 does not support "next page" retrieval.
- **Sync-call to embedder is acceptable here** because it's gated and runs at most once per turn. If profiling shows it as a hotspot, batch-embed multiple sub-queries.

#### V1 vs V2

| Aspect | V1 | V2 |
|---|---|---|
| Trigger | OnPreIteration (gated) | OnPreIteration + on-demand sub-query during turn |
| Ranking | Linear combination (§8.3 FS) | Learned reranker |
| Budget | Top-K | Token-aware truncation |
| Cold-start | Empty result | Bootstrap synthesis from CLAUDE.md / docs |

#### Test surface (TDD-first)

| Test task | Implementation task |
|---|---|
| `Retrieve_FirstCall_RunsSearch` | Initial uncached path |
| `Retrieve_StoreUnchangedSinceLastRetrieval_SkipsSearch` | Gate behaviour |
| `Retrieve_StoreWrittenAfterLastRetrieval_RunsSearch` | Gate invalidation |
| `Retrieve_AppliesRankingFormula` | Score correctness |
| `Retrieve_SessionMatchBoost_PrefersThisSession` | Soft-signal scope (P3) |
| `Retrieve_PartitionsByContentType` | Output shape |
| `Retrieve_EmptyStore_AssignsEmptyLists` | Cold-start |
| `Retrieve_RespectsTopK` | Bounded output |

---

### 6.5 The Hook Layer

#### Purpose

Provide deterministic, composable extension points for memory operations to integrate with the agent loop without touching `Agent.cs`.

#### Responsibilities

- Expose three new lifecycle hooks: `OnUserPromptSubmit`, `OnPreIteration`, `OnPostToolBatch`.
- Drop the unfireable `OnPostToolUseFailure` hook.
- Define a baseline ordering: memory hooks first, user hooks composed after.
- Provide a `ComposeBefore` escape hatch.

#### Inputs / Outputs

| Hook | Fires | Receives | Side effects |
|---|---|---|---|
| `OnUserPromptSubmit` | Every `ChatAsync` call, including first | `(userMessage, ctx)` | Audit / per-turn enrichment |
| `OnPreIteration` | Start of every loop iteration, before system prompt rebuild | `ctx` | Mutate `ctx.Knowledge`, `ctx.Memory` (Retrieval Engine) |
| `OnPostToolBatch` | After `Task.WhenAll` of tool calls, before next LLM call | `(toolEvents, ctx)` | Batch-level audit |
| ~~`OnPostToolUseFailure`~~ | (dropped) | — | — |

#### Internal flow — registration

```csharp
services.AddAgencyMemory(cfg => {
    cfg.Memory = ...;
    cfg.Distiller = ...;
    cfg.Consolidator = ...;
});

// Inside AddAgencyMemory:
services.AddSingleton<MemoryHookFactory>();
services.PostConfigure<AgencyHarnessOptions>(o => {
    o.BaselineHooks = MemoryHookFactory.Build();      // baseline FIRST
});

// In Agent ctor:
this._hooks = options.UserHooks is null
    ? options.BaselineHooks
    : options.BaselineHooks.Compose(options.UserHooks);
```

#### Implementation notes

- The existing `AgentHooks` record gets three new optional delegates.
- `AgentHooksExtensions.Compose` already exists. New: `ComposeBefore` puts the argument *first*, intended for advanced users who specifically need to see pre-retrieval Context.
- The Retrieval Engine, the Distiller's `OnAssistantTurn` timer-restart, and the `OnUserPromptSubmit` audit are all bound via the baseline hooks built by `MemoryHookFactory`.

#### Constraints

- **Ordering is non-configurable by default**. The escape hatch (`ComposeBefore`) is explicit. We do not let user code accidentally run before retrieval.
- **No `async void` hooks.** Every hook returns `Task` and is `await`ed in the loop; exceptions propagate to the harness's error handling.

#### V1 vs V2

| Aspect | V1 | V2 |
|---|---|---|
| Ordering | Baseline-first, fixed | Priority numbers per hook |
| Cancellation | Cooperative via `CancellationToken` | Hook-scoped timeouts |
| Sandbox | None | Resource-limited execution |

#### Test surface (TDD-first)

| Test task | Implementation task |
|---|---|
| `OnUserPromptSubmit_FiresOnFirstTurn` | Hook wiring |
| `OnUserPromptSubmit_FiresOnSubsequentTurns` | Idempotency across turns |
| `OnPreIteration_FiresBeforeSystemPromptBuild` | Ordering in `Agent.RunAsync` |
| `OnPostToolBatch_FiresAfterAllToolsSettled` | After-`Task.WhenAll` injection point |
| `Compose_BaselineFirst_UserHooksSeeEnrichedContext` | Default ordering |
| `ComposeBefore_UserHookSeesEmptyMemoryContext` | Escape hatch |
| `OldOnPostToolUseFailure_NotRegistered` | Negative test for removed hook |

---

### 6.6 The Hygiene Sweeper (`HygieneSweeperBackgroundService`)

#### Purpose

Bound the size of the memory store passively, complementing the active Consolidator.

#### Responsibilities

- Run a daily sweep (configurable).
- Delete Records exceeding TTL.
- Delete Records below an importance threshold that have been stale for too long.

#### Inputs / Outputs

| In | Out |
|---|---|
| `IMemoryStore` (full scan, batched) | DELETE statements; structured logs |

#### Internal flow

```
on schedule:
  using activity = ActivitySource.StartActivity("memory.sweep", Internal)
  
  // TTL pass
  foreach contentType in [Fact, Memory]:
    ttl = opts.Ttl[contentType]
    if ttl is null: continue
    deleted = await store.DeleteWhereAsync(c =>
        c.ContentType == contentType &&
        c.UpdatedAt < now - ttl &&
        (c.LastAccessedAt == null or c.LastAccessedAt < now - ttl))
    log.Info("TTL sweep deleted {Count} {ContentType} records", deleted, contentType)
    metric.Add("memory.swept.ttl", deleted, tags)
  
  // Importance-pruning pass
  deleted = await store.DeleteWhereAsync(c =>
      c.Importance < opts.ImportancePruneThreshold &&
      (c.LastAccessedAt == null or c.LastAccessedAt < now - opts.StalePruneAge))
  log.Info("Importance pruning deleted {Count} records", deleted)
  metric.Add("memory.swept.importance", deleted, tags)
```

#### Implementation notes

- The sweep is a **single SQL statement** per content type, not a loop in C#. Postgres handles the batching.
- The sweep is **not transactional across content types**. Each statement commits independently.
- **`now` is the injected clock (TI-4).** `RunOnceAsync` reads `_timeProvider.GetUtcNow()` once and passes it to both passes, so the staleness comparison is measured against the service's `TimeProvider` rather than the database wall clock (see §8.5).
- **`LastAccessedAt` reset by retrieval** means actively used Records get a fresh decay clock. This is the intended decay-reset behaviour.
- The schedule is configured via `MemoryOptions.HygieneSchedule` (default: every 24h, randomised by ±15 min to avoid herd thunder).

#### Constraints

- Must not race with the Consolidator on the same `UserId`. Mitigation: the sweep only touches records the Consolidator wouldn't (low importance, not accessed in 30+ days). In practice, no overlap.
- Must surface deletion counts as a metric for monitoring.

#### V1 vs V2

| Aspect | V1 | V2 |
|---|---|---|
| Schedule | Fixed daily | Cron-style configurable |
| Per-user policies | Global thresholds | Per-user opt-in to aggressive pruning |
| Archive | No | Optional cold-store offload |

#### Test surface (TDD-first)

| Test task | Implementation task |
|---|---|
| `Sweep_RecordsOlderThanTtl_AreDeleted` | TTL clause |
| `Sweep_RecordsAccessedRecently_AreNotDeleted` | LastAccessedAt reset behaviour |
| `Sweep_FactsWithNoTtl_AreNotDeleted` | Per-content-type TTL |
| `Sweep_LowImportanceStale_AreDeleted` | Importance pass |
| `Sweep_HighImportanceStale_AreNotDeleted` | Importance threshold |
| `Sweep_EmitsMetricsAndLog` | Observability |

---

### 6.7 Agent-Facing System Tools

Two new tools added to the agent's tool registry. **Only two**, deliberately minimal.

#### 6.7.1 `SetFocus(title?, domain?, tags?)`

Updates `Context.Focus`. Informs the retrieval query (§6.4).

**Dynamic tool description**: lists the user's existing `Domain` values and (per-domain) the most-used `Tag` values. The agent reuses established vocabulary rather than coining parallel labels. In v1, the full corpus is listed; in v2, top-N by frequency.

**Idempotent**: setting the same value twice is a no-op. Returns the previous focus values.

#### 6.7.2 `MarkGoalComplete(summary?)`

Enqueues a `DistillationJob` for the session with `Trigger = GoalCompletion`. The optional `summary` parameter is attached to the job as a hint for the Distiller's extraction prompt ("the agent believes this was achieved: …").

**Does not stop the loop.** The agent may continue if the user has follow-ups. The next inactivity timer / dispose / `MarkGoalComplete` triggers another distillation; the watermark prevents reprocessing.

#### Constraints

- These are the **only** two tools the agent gains from this work. No `Memorize`, `Recall`, `Forget-Me`, `Reflect`. The existing `Forget(domain, key)` remains in `MemoryTool` (Q5 OpenItems).
- Both tools are registered via the existing `ToolRegistry` mechanism; no new tool-host machinery.

#### Test surface (TDD-first)

| Test task | Implementation task |
|---|---|
| `SetFocus_UpdatesContextFocus` | Tool wiring |
| `SetFocus_ToolDescription_ListsKnownDomains` | Dynamic description |
| `MarkGoalComplete_EnqueuesJob` | Channel write |
| `MarkGoalComplete_DoesNotStopLoop` | Loop semantics |
| `MarkGoalComplete_WithSummary_AttachesToJob` | Parameter wiring |

---

## 7. Data Model / Storage Layer

### 7.1 The `records` table

```sql
CREATE TABLE records (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id         TEXT NOT NULL,
    session_id      TEXT NULL,                    -- NULL = Global (user-wide)
    content_type    SMALLINT NOT NULL,            -- 0 = Fact, 1 = Memory
    domain          TEXT NOT NULL,
    key             TEXT NOT NULL,
    title           TEXT NOT NULL,
    value           TEXT NOT NULL,                -- Markdown
    tags            TEXT[] NOT NULL DEFAULT '{}',
    importance      DOUBLE PRECISION NOT NULL CHECK (importance >= 0 AND importance <= 1),
    embedding       vector(1536) NOT NULL,         -- dimension per IEmbeddingGenerator
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    last_accessed_at TIMESTAMPTZ NULL
);

-- Upsert key: functional unique index so that NULL session_id (Global scope) is treated
-- as a single empty-string bucket rather than infinitely many distinct NULLs (the Postgres
-- default NULL != NULL semantics for plain UNIQUE constraints).  COALESCE(session_id, '')
-- ensures exactly one row per (user_id, domain, key) for Global-scope records.
CREATE UNIQUE INDEX records_upsert_key
    ON records (user_id, COALESCE(session_id, ''), domain, key);
```

Notes:
- `session_id` is `NULL` for Global-scope records. The **functional unique index** `COALESCE(session_id, '')` maps `NULL` to `''`, so all Global records for the same `(user_id, domain, key)` share a single index entry and collapse to one row on upsert. A plain table-level `UNIQUE` constraint would treat each `NULL` as distinct (Postgres default), allowing unbounded duplicates; the functional index closes that gap at the database level.
- The corresponding `ON CONFLICT` clause in `UpsertAsync` targets the same expression: `ON CONFLICT (user_id, COALESCE(session_id, ''), domain, key) DO UPDATE SET ...`.
- `embedding vector(N)` — `N` must match the configured `IEmbeddingGenerator`. Mismatch is a startup-time failure, not runtime.
- `tags TEXT[]` — Postgres-native array. No separate `record_tags` table.

### 7.2 Indexes

| Index | Purpose |
|---|---|
| `PRIMARY KEY (id)` | Surrogate addressing for delete/merge |
| `UNIQUE (user_id, COALESCE(session_id, ''), domain, key)` | Upsert idempotency; functional index collapses NULL session_id (Global scope) to one row per key |
| `BTREE (user_id, content_type)` | Filtered search prefilter |
| `BTREE (user_id, domain)` | Domain-scoped queries |
| `HNSW (embedding vector_cosine_ops)` | Vector search; m=16, ef_construction=64 (defaults; tune later) |
| `BTREE (updated_at)` | TTL sweep range scans |
| `BTREE (last_accessed_at) WHERE last_accessed_at IS NOT NULL` | Importance pruning scans |

### 7.3 The `watermarks` table

```sql
CREATE TABLE watermarks (
    user_id                 TEXT NOT NULL,
    session_id              TEXT NOT NULL,
    last_distilled_turn_idx INTEGER NOT NULL DEFAULT 0,
    last_updated_at         TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (user_id, session_id)
);
```

A single row per session. Read on Distiller startup (cold cache), written by the Distiller after a successful episode write. In-memory cache in front.

### 7.4 The `dead_letter` table

```sql
CREATE TABLE dead_letter (
    id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id     TEXT NOT NULL,
    session_id  TEXT NULL,
    job_kind    TEXT NOT NULL,           -- 'distillation' | 'consolidation'
    job_payload JSONB NOT NULL,
    error       TEXT NOT NULL,
    stack       TEXT NULL,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX dead_letter_created_at_idx ON dead_letter(created_at);
```

For operational inspection only. Never read by the live system.

### 7.5 The `user_state` table

```sql
CREATE TABLE IF NOT EXISTS user_state (
    user_id         TEXT PRIMARY KEY,
    last_written_at TIMESTAMPTZ NOT NULL
);
```

A single row per user. Written on every `UpsertAsync`, `ForgetAsync`, and `ForgetMeAsync` call via a `GREATEST(...)` upsert (so the column always holds the most recent write timestamp across concurrent writers). Read lazily on cache miss or process restart to warm the in-memory `ConcurrentDictionary<string, DateTimeOffset>` that powers the retrieval gate (§8.1).

Notes:
- `last_written_at` is `NOT NULL` **without** a `DEFAULT now()`, unlike the timestamp columns in `records`, `watermarks`, and `dead_letter`. This is intentional: every write path supplies the value explicitly, so a server-side default would be redundant. Be aware of this asymmetry if adding new write paths — the column must always receive an explicit value.
- No index beyond the primary key is needed: all access is a single-row point lookup by `user_id`.

### 7.6 Data flow

```
[ Agent loop ]
       │ writes turns
       ▼
[ IConversationManager (in-memory) ]
       │ source-of-truth for transcript
       ▼
[ DistillationJob via Channel ]   (triggered: tool / timer / dispose)
       │
       ▼
[ Distiller ]
       │ reads turns
       │ writes records (UPSERT)
       │ writes watermarks
       ▼
[ records, watermarks ]
       │
       │ on session end:
       ▼
[ Consolidator ]
       │ reads records
       │ runs sub-agent
       │ writes records (UPDATE / DELETE) via consolidator tools
       ▼
[ records ]
       │
       │ daily:
       ▼
[ Hygiene Sweeper ]
       │ deletes records by TTL / Importance
       ▼
[ records ]

       Retrieval path (read-only):
       │
       ▼
[ OnPreIteration → Retrieval Engine ]
       │ reads records (vector search)
       │ writes Context.Knowledge / Context.Memory (in-memory only)
       ▼
[ Context → SystemPromptBuilder → LLM call ]
```

### 7.7 Differences between storage backends

V1 ships with PostgreSQL + pgvector only. The `IMemoryStore` interface is designed to allow:

- A future **SQLite + sqlite-vss** implementation for single-user/desktop scenarios.
- A future **in-memory** implementation for tests (already partially exists in the `Agency.KeyValueStore.Common` family).

Postgres-specific details (HNSW parameters, `vector_cosine_ops`, JSONB dead-letter) live in the `Agency.Memory.Sql.Postgres` project. The abstraction in `Agency.Memory.Common` is backend-neutral.

---

## 8. Core Algorithms

### 8.1 The Retrieval Gate

**Goal**: avoid running a vector search when the store hasn't changed since the last retrieval.

```
function ShouldRetrieve(ctx, store):
    lastWritten = store.LastWrittenAtAsync(ctx.User.Id)         // in-memory cache hit
    return ctx.MemoryLastRetrievedAt is null
        or lastWritten > ctx.MemoryLastRetrievedAt
```

The cache makes this comparison O(1) — no I/O. Within a single user turn (multiple iterations), the store almost never changes (the Distiller is async), so the gate skips on every iteration after the first.

**Correctness invariant**: any operation that writes to the store *must* update `LastWrittenAt` for the affected user. This includes `UpsertAsync`, `ForgetAsync`, `ForgetMeAsync`, and Consolidator tool calls. Forgetting to bump it produces stale retrievals — which is a silent bug. Test the bump.

### 8.2 Episode Extraction

The Distiller's core LLM call. Prompt schema:

```
SYSTEM:
You are a memory distiller. Given a conversation excerpt, produce
zero or more Records that capture what was learned.

A Record has:
- ContentType: "Fact" (impersonal, durable) or "Memory" (episodic, OAO-shaped)
- Title: short, ≤60 chars
- Domain: from {{ existing_domains }} or new
- Key: stable identifier within domain (e.g. "LanguagePreference")
- Tags: 0..5 short tags
- Scope: "Global" (across user's sessions) or "Session" (this session)
- Importance: 0.0..1.0 — how valuable for future reference
- Value: Markdown body

For Memory records, the Value follows the OAO template:
  ## Observation
  ...
  ## Action
  ...
  ## Outcome
  ...
  ## Lesson
  ...

For Fact records, the Value is a concise statement.

Avoid duplicating facts already established (see "Known Records").
Avoid trivia. If nothing is worth remembering, return an empty array.

CONTEXT:
- Trigger: {{ trigger }}                              // GoalCompletion | Inactivity | SessionDisposed
- Trigger summary (if any): {{ trigger_summary }}
- Session focus: {{ focus_title }} / {{ focus_domain }} / {{ focus_tags }}
- Known domains: [{{ known_domains }}]
- Known recent facts (top 10): [{{ recent_facts }}]

TURNS:
{{ formatted_turns }}

RESPONSE FORMAT:
Strictly valid JSON: { "records": [ {Record}, ... ] }
```

The response is parsed; each Record is embedded; each is upserted under its `(user_id, session_id, domain, key)` key.

### 8.3 The Ranking Formula

```
score(record, query, currentSessionId, now) =
        wₛ · similarity(query.embedding, record.embedding)
      + wᵣ · recency(record.updated_at, now)
      + wᵢ · record.importance
      + wₘ · sessionMatch(record.session_id, currentSessionId)

where:
    similarity   = cosine(qVec, rVec)               ∈ [-1, 1] → clipped to [0, 1]
    recency      = exp(-ageDays / halfLifeDays)     ∈ (0, 1]
    importance   = record.importance                ∈ [0, 1]
    sessionMatch = 1 if record.session_id == currentSessionId else 0

defaults:
    wₛ = 0.5,  wᵣ = 0.3,  wᵢ = 0.2,  wₘ = 0.1
    halfLifeDays = 7

normalisation:
    weights are NOT auto-normalised to sum to 1. The sum (1.1 in
    defaults) is intentional; sessionMatch is an additive bonus
    on top of the GA-style core. Tune weights to taste; the system
    treats the raw score as an ordinal, not a probability.
```

**Worked example** — a record from the current session, similar to query (sim=0.8), 2 days old, importance=0.6:

```
score = 0.5·0.8 + 0.3·exp(-2/7) + 0.2·0.6 + 0.1·1
      = 0.40    + 0.3·0.751      + 0.12     + 0.10
      = 0.40    + 0.225          + 0.12     + 0.10
      = 0.845
```

The same record viewed from a *different* session (sessionMatch=0):

```
score = 0.40 + 0.225 + 0.12 + 0 = 0.745
```

A 0.1 absolute difference — meaningful but not dominating.

### 8.4 Consolidation Decision Loop

```
loop while sub-agent has not called Memory_Done:
    sub-agent receives:
        - all records (id, contentType, domain, key, title, value, tags, importance, age)
        - similarity threshold hints per ContentType
    sub-agent reasons; produces zero or more tool calls per turn:
        Memory_Merge(ids, newRecord)
          → store.BeginTx
          → delete(ids), insert(newRecord)
          → commit
        Memory_Update(id, newValue, newImportance?)
          → store.UpdateAsync(id, newValue, newImportance)
        Memory_Delete(id)
          → store.DeleteAsync(id)
        Memory_Done()
          → terminates the consolidation pass
```

Stop conditions: `Memory_Done`, OR `iterationCount >= MaxIterations` (safety), OR `Cost >= MaxCostUsd`.

### 8.5 The Hygiene Sweep

Two passes, each a single bulk DELETE per content type:

```sql
-- TTL pass (per ContentType where TTL is set)
DELETE FROM records
WHERE content_type = :ct
  AND updated_at < :now - :ttl
  AND (last_accessed_at IS NULL OR last_accessed_at < :now - :ttl);

-- Importance-pruning pass
DELETE FROM records
WHERE importance < :imp_threshold
  AND (last_accessed_at IS NULL OR last_accessed_at < :now - :stale_age);
```

The passes are independent. Deletion counts are reported per pass.

**Reference time (TI-4):** `:now` is supplied by `HygieneSweeperBackgroundService` from its injected `TimeProvider` (`_timeProvider.GetUtcNow()`), **not** the database `now()`. The staleness window is therefore driven by the same clock the service is constructed with, so a `FakeTimeProvider` can drive expiry deterministically in tests (and production uses `TimeProvider.System`, which equals the DB wall clock). `IMemoryStore.DeleteWhereTtlExceededAsync` / `DeleteWhereLowImportanceStaleAsync` take this `now` as an explicit parameter.

### 8.6 Error Handling — taxonomy

| Class | Examples | Action |
|---|---|---|
| **Transient** | LLM 429 / 503; LLM or embedding service **unreachable** — a connection-level `HttpRequestException` with no HTTP response (connection refused / DNS / socket reset / transport timeout, i.e. `StatusCode is null`); Postgres deadlock | Retry with exponential backoff up to `MaxRetries` |
| **Permanent** | LLM 400 / 401; malformed JSON in extraction response after parse retries; constraint violation | Dead-letter; emit `*FailedEvent`; log |
| **Cancellation** | `OperationCanceledException` from `stopToken` | Propagate without retry; do not dead-letter |
| **Programmer error** | `ArgumentNullException`, `InvalidOperationException` | Fail-fast, log, emit `*FailedEvent` |

The Distiller and the Consolidator use the same taxonomy.

---

## 9. Incremental vs Full Processing

The system has a deliberate mix of incremental and full-scan operations. The choice per component is driven by cost and consistency requirements.

| Component | Mode | Reason |
|---|---|---|
| **Distiller** | **Incremental** (via `LastDistilledTurnIndex`) | Re-processing already-distilled turns is waste; the watermark makes re-runs idempotent. |
| **Consolidator** | **Full per user, per session-end trigger** | The whole point is cross-record reasoning; partial views miss merges. Scaled in v2. |
| **Retrieval** | **On-demand, gated, top-K** | Per-iteration cost is bounded by `topK`; gate suppresses no-op work. |
| **Hygiene Sweeper** | **Full scan, daily** | Set-based SQL handles the scan; one delete per content type is cheap. |
| **Embedding generation** | **Incremental, write-time** | Embedding is generated once per Record on upsert. Re-embedding only happens on update of `Value`. |
| **Watermark advancement** | **Incremental, monotone** | `MAX(stored, candidate)` semantics — never moves backwards. |

The Distiller's incremental nature is what makes recovery cheap: a process crash mid-distillation leaves the watermark un-advanced, so the next run picks up exactly where it left off without dedup logic.

---

## 10. Background Workers / Async Components

Four background components run outside the agent loop. All are `IHostedService` (registered via `AddHostedService<T>()`) so they participate in the host's startup/shutdown lifecycle.

| Component | Type | Trigger | Cardinality |
|---|---|---|---|
| `DistillerBackgroundService` | `BackgroundService` | `Channel<DistillationJob>` | 1 per process, shared across sessions; per-session work coordinated via `ConcurrentDictionary` |
| `ConsolidatorBackgroundService` | `BackgroundService` | `Channel<ConsolidationJob>` | 1 per process; serial-per-user inside |
| `HygieneSweeperBackgroundService` | `BackgroundService` (`PeriodicTimer`) | Daily schedule | 1 per process |
| `InactivityTimerService` | `IHostedService` (singleton) | n/a (passive); state lives in `ConcurrentDictionary<sessionId, Timer>` | 1 per process |

### 10.1 Startup & shutdown

- **Startup**: Postgres schema initialiser (`MemorySchemaInitializer`, modelled on the existing `Agency.Mcp.Memory` pattern) runs first. Channels and timers are instantiated lazily on first use.
- **Shutdown**: each service responds to the `stopToken`. The Distiller drains its channel up to `ShutdownDrainTimeout` (default 30s) before forcing exit. Any in-flight jobs at force-exit are dead-lettered so they can be picked up after restart by re-deriving from watermarks.

### 10.2 Concurrency model

- Channels are MPSC (multi-producer, single-consumer) via `System.Threading.Channels`.
- **Distiller channel model (intentional deviation from the original single-channel design):**
  The Distiller uses **one bounded `Channel<DistillationJob>` per session** (managed by
  `ChannelSessionRegistry`) rather than a single global channel. This gives per-session
  `DropOldest` backpressure cheaply (capacity 32 per session, §10.3). The single consumer
  (`DistillerBackgroundService`) drains all session channels in a sweep, then suspends on a
  `SemaphoreSlim` that any `NotifyingChannelWriter` releases on every successful enqueue.
  This event-driven wake eliminates the latency floor and idle CPU wakeups that a polling
  loop would introduce; a newly-enqueued job is picked up in the next scheduler tick.
- The Consolidator is **serial per user** but parallel across users — implemented by partitioning the channel by `userId` (each partition gets its own consumer task).
- The Hygiene Sweeper is single-threaded; bulk SQL handles the heavy lifting.

### 10.3 Resource bounds

| Bound | Default | Configurable via |
|---|---|---|
| Per-session distillation queue | 32 jobs (DropOldest) | `DistillerOptions.PerSessionQueueCapacity` |
| Distillation retries | 3 | `DistillerOptions.MaxRetries` |
| Retry base delay | 2s | `DistillerOptions.RetryBaseDelay` |
| Consolidation iterations | 20 | `ConsolidatorOptions.MaxIterations` |
| Consolidation cost ceiling | $0.50/pass | `ConsolidatorOptions.MaxCostUsd` |
| Hygiene sweep frequency | 24h ±15 min jitter | `MemoryOptions.HygieneSchedule` |

---

## 11. Performance Expectations

The dominant cost is LLM calls. Storage is comparatively free.

### 11.1 Hot-path budgets

| Operation | Target p50 | Target p95 |
|---|---|---|
| `OnPreIteration` gate hit (no I/O) | < 50 µs | < 200 µs |
| `OnPreIteration` gate miss → embedding → search → rank | < 200 ms | < 500 ms |
| `OnAssistantTurn` timer restart | < 100 µs | < 500 µs |
| `SystemPromptBuilder.Build` after retrieval | < 5 ms | < 20 ms |

A hot-path operation crossing its p95 budget should emit a warning log + metric. These budgets assume an embedding service co-located within the same data center (≤ 5 ms RTT).

### 11.2 Cold-path budgets

| Operation | Target p50 |
|---|---|
| Episode extraction (Strong-tier LLM) | 3–10 s |
| Single Record embed + upsert | < 100 ms (excluding embedder) |
| Full consolidation pass (50 Records) | 10–30 s |
| Full consolidation pass (500 Records) | unbounded in v1; warn |
| Daily hygiene sweep (100k records) | < 5 s |

### 11.3 Throughput considerations

- A single Distiller instance can process roughly **6–20 distillation jobs/min** depending on model and Record count per job.
- The bottleneck is LLM throughput, not storage. Adding Distiller replicas in v2 is straightforward (they're stateless modulo the watermark, which is durable).

### 11.4 Cost considerations

- Each Episode extraction is one LLM call. At 10 episodes/session/user/day and 100 active users, that's 1000 LLM calls/day on the cold path.
- Consolidation at 1 pass/session/user/day = same 100/day.
- Hygiene sweeps are LLM-free.
- Embedding is per-record (5–10 per session) — embedding model is cheaper, but adds up.

### 11.5 Storage growth

- A Record averages ~2 KB (Markdown body + embedding). 10 records/session/day × 100 users × 365 days × 2 KB ≈ **0.7 GB/year**. Consolidator + Hygiene Sweeper should compress this significantly.
- HNSW index size is roughly 1.5× the embedding column.

---

## 12. Edge Cases and Failure Modes

### 12.1 Concurrency

| Case | Behaviour |
|---|---|
| Two iterations in the same session both call `OnPreIteration` simultaneously | Each one independently runs the gate; both may issue a search, but writes-to-Context are race-safe (last write wins for an in-memory list assignment). Acceptable; not a correctness issue. |
| Distiller writes a Record while Retrieval is mid-search | Postgres MVCC handles it; the in-flight search sees a consistent snapshot. The next retrieval will see the new Record. |
| Consolidator deletes a Record while Retrieval has it in flight | The retrieved Record exists in memory in the caller; the user-facing system prompt may transiently contain a deleted Record for one turn. Acceptable; corrected on next retrieval. |
| `MarkGoalComplete` called twice in one session before the first distillation finishes | The second job is enqueued. The first job advances the watermark; the second sees an empty window and exits as a no-op. Idempotent. |
| Inactivity timer expires *during* `MarkGoalComplete` execution | Both enqueue jobs. First wins on watermark; second is a no-op. |

### 12.2 Failure modes

| Failure | Detection | Recovery |
|---|---|---|
| LLM returns malformed JSON in extraction | Parse exception, classified as transient (1 retry with stricter prompt) → permanent | Dead-letter; next session retries |
| LLM 429 rate limit | HTTP status | Exponential backoff retry; eventually dead-letter |
| Embedding service down | HTTP timeout or connection failure (no response) | Distillation retries (classified transient — TI-5); if persistent → dead-letter |
| Postgres unavailable | Connection exception | Distillation retries; agent hot path also fails (entire system degraded) |
| Process crash mid-distillation | Watermark not advanced | Next process start picks up via channel rehydration from watermark; turns are re-distilled |
| Process crash mid-consolidation | Partial mutations committed | Next session-end re-triggers consolidation; LLM observes already-consolidated state and skips |
| `LastWrittenAt` cache misses on restart | First retrieval hits the DB to hydrate | Acceptable one-turn penalty |

### 12.3 Data hazards

| Hazard | Mitigation |
|---|---|
| Embedding dimension change | Startup check; fail-fast if column dim ≠ generator dim |
| `tags` array unbounded growth | Soft cap of 5 tags per Record enforced by extraction prompt; no hard DB constraint v1 |
| `value` Markdown injection (e.g., script tags) | Markdown rendered as plain text in system prompt; no XSS surface (not a browser) |
| Adversarial user trying to pollute Global memory | Cross-user pollution is impossible (per-user partitioning); intra-user only |
| Consolidator hallucinates a Merge that loses information | Mitigated by prompt design + LLM-tier choice; acceptable risk for v1 |

### 12.4 Operational edge cases

| Case | Behaviour |
|---|---|
| User has zero Records (cold start) | Retrieval returns empty; system prompt notes "No relevant memories yet." |
| User has 10,000 Records | Retrieval still bounded by `RetrievalTopK`. Consolidator emits a warning and proceeds; risk of context-overflow noted for v2. |
| `Forget-Me` called mid-session | All Records deleted; Retrieval gate sees `LastWrittenAt` updated; next retrieval returns empty. Distillation in flight may write a fresh Record — by design, since the user is mid-session and presumably wants future memory. |
| Two different users sharing a `userId` (deployment bug) | Out of scope; the deployment is responsible for `userId` distinctness. |

---

## 13. End-to-end Flow

A complete walkthrough of one user-facing turn that produces a memory and recalls it next session.

### Session N — first interaction

```
01. User: "I prefer Python."
02. ChatSession.SendAsync("I prefer Python.")
03. Agent.RunAsync starts iteration 1
    ├─ OnUserPromptSubmit fires           (memory: no-op for now)
    ├─ OnPreIteration fires
    │   ├─ store.LastWrittenAtAsync(userId) → null (no prior writes)
    │   ├─ ctx.MemoryLastRetrievedAt = null → run search
    │   ├─ store.SearchAsync(qVec, topK=10) → []
    │   ├─ ctx.Knowledge = []   ;   ctx.Memory = []
    │   └─ ctx.MemoryLastRetrievedAt = now
    ├─ SystemPromptBuilder.Build(ctx) → includes "No relevant memories yet."
    ├─ LLM call → "Got it. I'll keep that in mind."
    ├─ OnAssistantTurn fires
    │   └─ InactivityTimerService.Restart(sessionId, userId)
    ├─ no tool calls → StopCondition met (NoToolCalls)
    └─ AgentResultEvent emitted

04. 5 minutes pass with no new turn.
05. InactivityTimer fires.
    └─ DistillationJob { userId, sessionId, Trigger=Inactivity, UpToTurnIndex=1 }
       enqueued to Channel<DistillationJob>.

06. DistillerBackgroundService dequeues.
    ├─ reads turns (0, 1]
    ├─ LLM extraction → [
    │     { ContentType: Fact, Domain: "Preferences", Key: "Language",
    │       Title: "Python preference", Value: "User prefers Python.",
    │       Tags: ["language", "python"], Scope: Global, Importance: 0.7 }
    │   ]
    ├─ embed + upsert → store
    ├─ store.LastWrittenAt[userId] = now
    ├─ watermark advances to 1
    └─ DistillationCompletedEvent emitted

07. Session disposal triggers final DistillationJob (UpToTurnIndex=1).
    └─ Distiller dequeues; sees watermark already 1; no-op. ✓

08. ConsolidatorBackgroundService picks up.
    ├─ loads all records for userId → [the Python preference Fact]
    ├─ sub-agent runs; sees 1 record, nothing to consolidate
    ├─ sub-agent calls Memory_Done
    └─ ConsolidationCompletedEvent emitted
```

### Session N+1 — three weeks later

```
01. User: "Write me a script to deduplicate this list."
02. ChatSession.SendAsync(...)
03. Agent.RunAsync starts iteration 1
    ├─ OnUserPromptSubmit fires
    ├─ OnPreIteration fires
    │   ├─ store.LastWrittenAtAsync(userId) → (3 weeks ago)
    │   ├─ ctx.MemoryLastRetrievedAt = null → run search
    │   ├─ qVec = embed("Write me a script to deduplicate this list.")
    │   ├─ store.SearchAsync(qVec, topK=10) → [
    │   │     { record: Python preference Fact, similarity: 0.62 }, ...
    │   │   ]
    │   ├─ score:
    │   │     0.5·0.62 + 0.3·exp(-21/7) + 0.2·0.7 + 0.1·0
    │   │   = 0.31     + 0.0149          + 0.14     + 0
    │   │   = 0.4649
    │   ├─ partition by ContentType:
    │   │     ctx.Knowledge = [Python preference Fact]
    │   │     ctx.Memory    = []
    │   └─ ctx.MemoryLastRetrievedAt = now
    ├─ SystemPromptBuilder.Build(ctx) → includes:
    │     ## Facts
    │     - **Python preference** (Updated 3 weeks ago)
    │       User prefers Python.
    ├─ LLM call → Python script.
    ├─ OnAssistantTurn fires → timer restarts
    ├─ iteration 2 begins
    │   ├─ OnPreIteration fires
    │   │   ├─ store.LastWrittenAtAsync = (3 weeks ago, unchanged)
    │   │   ├─ ctx.MemoryLastRetrievedAt > store.LastWrittenAt → SKIP
    │   │   └─ (search not performed)
    │   ├─ SystemPromptBuilder.Build uses already-populated ctx
    │   └─ ... continues
    └─ ... loop completes
```

The key observations from this trace:

- **Only one search per turn**, even for multi-iteration turns (the gate).
- **The Fact written in Session N is recalled in Session N+1** — even though the query is *about deduplication*, not preferences. Vector similarity surfaces the preference because "Python", "script", "list" are semantically nearby. Ranking + importance keeps it in the top-K.
- **The agent has no idea this happened.** It just sees a system prompt with a `## Facts` section. From its perspective, knowing the user prefers Python is a static fact.

---

## 14. Design Notes / Rationale

This section records the reasoning behind the most consequential decisions, including the alternatives considered and why they were rejected. These are decisions that future contributors might be tempted to revisit — and should know the context of before doing so.

### 14.1 Why distiller-only (no agent commit tool)?

**Alternative considered**: agent calls a `Memorize(domain, key, value)` tool whenever it learns something memorable.

**Rejected because**:
1. **Hot-path cost**: every tool call adds latency and tokens. A multi-turn debugging session could include dozens of memorise calls.
2. **Authoring quality**: the agent in the middle of solving a problem is not the best author of "what is memorable about this exchange." Hindsight, applied post-hoc by the Distiller, produces better summaries.
3. **Inconsistency**: agents trained on different system prompts produce wildly different memorisation patterns. Distiller-only gives uniform output regardless of the primary agent's model or prompt.

The closest precedent is Letta's "sleep-time agents," which similarly decouple capture from the conversation.

### 14.2 Why a single `Record` type with a `ContentType` discriminator?

**Alternative considered**: separate `Fact` and `Memory` C# types, possibly with shared interface.

**Rejected because**:
1. **Storage**: both go to the same vector store, with the same operations. A discriminator column is the natural representation.
2. **Retrieval**: ranking applies uniformly to both. A shared shape makes the formula trivially generic.
3. **Boilerplate**: distinct types double the surface (constructors, validators, mappers, tests) for what amounts to a categorisation.

A discriminator does cost us compile-time exhaustiveness — but `ContentType` has two values and is unlikely to grow much. Worth the simplicity.

### 14.3 Why is session a soft signal, not a partition?

**Alternative considered**: hard isolation — `SessionId` is part of the partition key, queries from session A never see session B's records.

**Rejected because**:
1. **Cross-session continuity is often the goal.** "Last time we hit this same problem…" requires reaching across sessions.
2. **Forcing the LLM to classify at write time produces poor results.** "Will this be useful in *other* sessions?" is hard to answer prospectively.
3. **Soft signalling is reversible at retrieval time.** Weights are configurable; if a deployment wants stricter isolation, set `wₘ` to a much higher value (or implement filtering as a post-hoc filter at the Retrieval Engine layer).

The cost is that "Session-scope" records *are* visible in other sessions (just lower-ranked). This is explicit and documented; users who need hard isolation must compose a filter into the retrieval path.

### 14.4 Why is `Value` Markdown, not structured?

**Alternative considered**: typed discriminated union: `Value: FactBody | OaoBody | EpisodeBody`, with structured fields.

**Rejected because**:
1. **The OAO schema is loose**, deliberately. Different episodes have different shape (no Action for purely informational turns; no Outcome for goals that were abandoned).
2. **Markdown is what the LLM consumes anyway**. Structured fields would just be serialised back to Markdown for the system prompt.
3. **Schema evolution is brittle.** Adding a field to `OaoBody` means schema migration and a rewrite of all stored Records. Markdown is forward-compatible: new fields are just new sections.

The cost is no compile-time schema checking. Mitigated by the extraction prompt being the canonical schema-enforcement point.

### 14.5 Why on-session-end consolidation, not on a schedule?

**Alternative considered**: a cron-style schedule (e.g., once a day per user).

**Rejected because**:
1. **Session-end gives a natural quiescence point.** The Distiller has just finished writing; the corpus is in a known state.
2. **No idle users.** A schedule wastes resources on dormant users. Session-end self-throttles to active usage.
3. **Lower latency to "freshly consolidated" state.** A user returning to a new session benefits from yesterday's consolidation, not last-week's.

The cost is that very chatty users may trigger consolidation more often than is optimal. Mitigated by the serial-per-user guarantee (multiple session-ends collapse into one pending job).

### 14.6 Why LLM-based consolidation, not deterministic similarity thresholds?

**Alternative considered**: similarity > 0.9 → UPDATE old, similarity in [0.7, 0.9] → flag for review, else ADD.

**Rejected because**:
1. **Contradictions don't always have high similarity.** "I love Paris" and "I find Paris too crowded" are about the same subject but may not be vector-close.
2. **Merge requires synthesis.** "Add a new sentence to the existing record" is a reasoning task, not a threshold task.
3. **Confidence calibration.** The LLM can mark uncertain merges; a fixed threshold cannot.

The cost is per-consolidation LLM cost. Bounded by `ConsolidatorOptions.MaxCostUsd` and `MaxIterations`. Acceptable for v1.

### 14.7 Why drop `OnPostToolUseFailure`?

The harness already catches all non-cancellation tool exceptions and converts them to `ToolResult(IsError: true)` at `Agent.cs:390`. There are no unhandled tool exceptions. A hook firing on a never-happens condition is dead code. Consumers who want failure-specific handling can use `OnPostToolUse` and check `result.IsError`.

### 14.8 Why is recency stored as a derived property, not a column?

It's a function of two known values (`UpdatedAt` and `now`). Storing it would mean either continuously updating it (write amplification) or storing it stale (correctness bug). Compute on read; render as a human-readable string for the LLM.

### 14.9 Why timer-only `OnAssistantTurn`?

Per OpenItems Item B / functional spec §7.2: the Channel carries `DistillationJob` items, not per-turn OAO records. `OnAssistantTurn` restarts the inactivity timer and returns. Reading turns from the conversation manager happens *inside* the Distiller, lazily, at dequeue time.

**Alternative considered**: queue an OAO record per turn, Distiller batches them.

**Rejected because**: it duplicates information already in the conversation manager. The manager *is* the transcript; copying it into the channel is unnecessary data motion and creates a synchronisation problem if both diverge.

### 14.10 Why `ComposeBefore` as an extension method, not a configuration flag?

User code that needs pre-retrieval hooks is rare and the use case is specific. Making it an opt-in method call keeps the default safe (memory enriches first, user sees the enriched view) while still permitting the advanced case explicitly. A configuration flag would either be a foot-gun (easy to flip globally) or just-as-explicit-but-uglier than a method call.

---

## 15. Implementation Task Breakdown (TDD-First)

Every implementation task is preceded by its test task. Tests are written first and define the requirement.

### Workstream A — `Agency.Memory.Common`

| # | Test task | Implementation task |
|---|---|---|
| A.1 | `RecordTests` — equality, derived `Age` property, validation of `Importance` range | `Record`, `ContentType` enum |
| A.2 | `RankingTests` — score calculations across worked examples, weight changes, edge cases (recency at age=0, max-age) | `RankingFormula`, `RankingWeights` |
| A.3 | `MemoryOptionsTests` — defaults, override binding | `MemoryOptions`, `DistillerOptions`, `ConsolidatorOptions` |
| A.4 | `MemoryHookFactoryTests` — produces baseline hooks; composes with user hooks in correct order | `MemoryHookFactory`, `AddAgencyMemory` extension |

### Workstream B — `Agency.Memory.Sql.Postgres`

| # | Test task | Implementation task |
|---|---|---|
| B.1 | `SchemaInitTests` — fresh DB gets correct tables + indexes | `MemorySchemaInitializer` |
| B.2 | `PostgresMemoryStoreTests.Upsert*` — insert, update, race | `PostgresMemoryStore.UpsertAsync` |
| B.3 | `PostgresMemoryStoreTests.Search*` — filters, ordering, top-K, over-fetch | `PostgresMemoryStore.SearchAsync` |
| B.4 | `PostgresMemoryStoreTests.Forget*` — single-record delete, multi-user isolation | `ForgetAsync`, `ForgetMeAsync` |
| B.5 | `PostgresMemoryStoreTests.LastWritten*` — cache hit, persistence, restart recovery | `LastWrittenAtAsync` + cache |
| B.6 | `WatermarkRepositoryTests` — monotonic advancement, restart re-hydration | `WatermarkRepository` |
| B.7 | `DeadLetterRepositoryTests` — write, scan-for-ops | `DeadLetterRepository` |

### Workstream C — Distiller

| # | Test task | Implementation task |
|---|---|---|
| C.1 | `DistillationJobTests` — equality, serialisation | `DistillationJob`, `DistillationTrigger` |
| C.2 | `EpisodeExtractionPromptTests` — golden-file extraction outputs against stub LLM | Extraction prompt + parser |
| C.3 | `DistillerBackgroundServiceTests.Happy*` — dequeue, extract, embed, upsert, advance watermark | `DistillerBackgroundService` core loop |
| C.4 | `DistillerBackgroundServiceTests.Idempotent*` — re-run of same watermark = no-op | Watermark guard |
| C.5 | `DistillerBackgroundServiceTests.Retry*` — transient LLM failure retries, permanent → dead-letter | Retry + DLQ wiring |
| C.6 | `InactivityTimerServiceTests` — start, restart, expiry → channel write, cancel-on-dispose | `InactivityTimerService` |
| C.7 | `MarkGoalCompleteToolTests` — enqueues correct job, does not stop loop | `MarkGoalCompleteTool` |
| C.8 | `OnAssistantTurnHookTests` — timer-restart only, no channel write | Hook wiring (regression guard) |

### Workstream D — Retrieval Engine & Hooks

| # | Test task | Implementation task |
|---|---|---|
| D.1 | `RetrievalGateTests` — first-call runs, repeat-call skipped, post-write invalidation | `OnPreIteration` baseline hook |
| D.2 | `RetrievalEngineTests` — search, rank, partition by ContentType, top-K cap | `RetrievalEngine` |
| D.3 | `SystemPromptBuilderTests` — renders Records with recency hints, handles empty | `SystemPromptBuilder` update |
| D.4 | `SetFocusToolTests` — updates Context.Focus, dynamic description, idempotent | `SetFocusTool` |
| D.5 | `OnUserPromptSubmitTests` — fires first turn, fires subsequent turns | Hook wiring |
| D.6 | `OnPostToolBatchTests` — fires after Task.WhenAll | Hook wiring |
| D.7 | `HookOrderingTests` — baseline first, ComposeBefore escape hatch | `AgentHooksExtensions.ComposeBefore` |

### Workstream E — Consolidator

| # | Test task | Implementation task |
|---|---|---|
| E.1 | `ConsolidatorJobTests` — equality, serialisation | `ConsolidationJob` |
| E.2 | `ConsolidatorToolsTests.Merge*` — atomic delete-then-insert | `Memory_Merge` tool |
| E.3 | `ConsolidatorToolsTests.Update*` — content update, importance update | `Memory_Update` tool |
| E.4 | `ConsolidatorToolsTests.Delete*` — single delete | `Memory_Delete` tool |
| E.5 | `ConsolidatorSubAgentTests` — full happy-path against stub LLM | `ConsolidatorSubAgentFactory` |
| E.6 | `ConsolidatorBackgroundServiceTests.PerUserSerial*` — coalescing | `ConsolidatorBackgroundService` |
| E.7 | `ConsolidatorBackgroundServiceTests.LargeCorpus*` — warning emission | Scale guard |

### Workstream F — Hygiene Sweeper

| # | Test task | Implementation task |
|---|---|---|
| F.1 | `HygieneSweeperTests.Ttl*` — per-content-type TTL, LastAccessedAt reset | `HygieneSweeperBackgroundService` TTL pass |
| F.2 | `HygieneSweeperTests.Importance*` — threshold + stale age | Importance pass |
| F.3 | `HygieneSweeperTests.Schedule*` — daily fire with jitter, cancellation | Scheduler |

### Workstream G — End-to-end (Functional)

| # | Test task |
|---|---|
| G.1 | `EndToEnd_FactWrittenInSessionN_RecalledInSessionNPlus1` — full scenario from §13 |
| G.2 | `EndToEnd_ForgetMeWipesAllUserData` — privacy guarantee |
| G.3 | `EndToEnd_HighFrequencyTurns_HotPathLatencyUnaffected` — performance regression guard |
| G.4 | `EndToEnd_DistillerCrash_RecoversFromWatermark` — durability |
| G.5 | `EndToEnd_ConsolidatorMergesContradiction_LatestStateRetained` — consolidation correctness |

These functional tests run against real LM Studio + PostgreSQL via the existing `Category=Functional` filter.

---

## 16. Open Items Tracked for V2

Items intentionally deferred but documented for the next iteration:

| # | Item | Rationale for deferral |
|---|---|---|
| O1 | Turn-tier extraction (Distiller granularity) | Storage and cost not justified before measurement |
| O2 | Reflection-tier extraction (cross-episode synthesis) | Requires episode volume that v1 doesn't have yet |
| O3 | Per-domain consolidator batching | Single-pass works at small scale; revisit when corpus per user > ~500 records |
| O4 | Cross-batch insight preservation in consolidation | Only relevant once O3 is in |
| O5 | Top-N domain/tag corpus in `SetFocus` description | Prompt-bloat mitigation when corpus is large |
| O6 | Token-budgeted retrieval truncation | Measure top-K token impact first |
| O7 | Dynamic-tier model routing in Distiller | Single-model is fine until cost or quality demands more |
| O8 | Soft-delete with audit trail | Only needed if compliance team asks |
| O9 | Importance scoring decay (vs. fixed at write) | Recency already does the temporal job |
| O10 | Memory dashboard / HITL UI | Host-app concern, not library concern |

---

## 17. Glossary

| Term | Definition |
|---|---|
| **Record** | The single durable item type in the memory store. |
| **Fact** | A Record with `ContentType = Fact`. Static, impersonal, durable. |
| **Memory** | A Record with `ContentType = Memory`. Episodic, OAO-shaped. |
| **Episode** | A goal-bounded synthesis of conversation turns. The unit of long-term Memory. |
| **OAO** | Observation-Action-Outcome — the trajectory triple the Distiller extracts from. |
| **Distiller** | The async background service that converts turns to Records. |
| **Consolidator** | The async background sub-agent that reconciles existing Records. |
| **Hygiene Sweeper** | The daily background service that prunes Records by TTL and importance. |
| **Watermark** | `LastDistilledTurnIndex` per session; monotonically advanced by the Distiller. |
| **Retrieval Gate** | The timestamp comparison that suppresses retrieval when the store hasn't changed. |
| **SessionMatch** | The boolean term in the ranking formula that boosts Records whose `SessionId` matches the current one. |
| **Soft signal** | A property that affects ranking without affecting visibility. Opposite of "hard isolation". |
| **Hot path** | The synchronous code path that runs per-iteration of the agent loop. |
| **Cold path** | The asynchronous code paths driven by background services. |

---

## 18. System Prompts (Appendix)

This appendix contains the canonical LLM system prompts used by the v1 components, plus the v2 prompts kept as reference for future work. These are operational artifacts — they ship with the implementation, not as documentation.

**Provenance note:** the original functional spec contained six prompt drafts ([`Untitled.md`](../../../OneDrive/Obsidian/Personal/Projects/Agency/Untitled.md)). Two of them — "Knowledge Capture" and "OAO Memory Capture" — were authored for an agent-driven commit tool that was rejected (Q1.1: capture is system-owned). Their substance is folded into §18.1 below, where the Distiller does the same authoring work after the fact. The remaining four prompts are reproduced here in their v1 form or marked as v2 deferred.

### 18.1 Distiller — Episode Extraction Prompt (V2)

Used by `DistillerBackgroundService` on every `DistillationJob`. This single prompt covers both Fact and Memory extraction; the LLM decides which (or both, or neither) to emit per excerpt.

**V2 (TI-8.2):** opens with a thinking-suppression directive (`/no_think` + explicit "output only JSON"). This is the prompt-level fallback that complements the SDK-level `LlmClientOptions.SuppressThinking = true` set on the distiller's chat client — for reasoning-capable models/APIs that ignore the SDK flag, the prompt still suppresses chain-of-thought so extraction returns strict JSON.

````text
SYSTEM:
/no_think
Output discipline: do NOT produce any chain-of-thought, reasoning, analysis, or
`<think> … </think>` blocks. Reason silently and respond with ONLY the final JSON
object described under "Response format". This is a prompt-level fallback for models
or APIs that do not honour the SDK-level thinking-suppression option.

You are a memory distiller for an AI agent. Your job is to read a conversation
excerpt and produce zero or more durable Records that capture what was learned
during the exchange. You operate AFTER the fact — the agent has already done its
work; you decide what is worth remembering.

## Record kinds

You may produce two kinds of Records:

1. **Fact** (`ContentType = "Fact"`)
   - Static, impersonal, durable information.
   - Examples: user preferences, organisational rules, environment configurations,
     domain conventions, names, identifiers.
   - `Value` is a concise statement (1–3 sentences).
   - Example: "User prefers Python for scripting tasks."

2. **Memory** (`ContentType = "Memory"`)
   - Episodic experience — a goal-bounded narrative following the
     Observation–Action–Outcome (OAO) pattern.
   - `Value` follows this Markdown template:

     ## Observation
     Describe the initial situation and the user's intent.

     ## Action
     Record the agent's reasoning, the tools called, and the decisions made.

     ## Outcome
     Assess the result. Did the goal succeed? Why or why not?

     ## Lesson
     A single-sentence takeaway for the agent's future self.

## Required fields per Record

- **`ContentType`** — `"Fact"` or `"Memory"`.
- **`Title`** (≤ 60 chars) — a concise label suitable for a system-prompt heading.
- **`Domain`** — group label; reuse one from "Known Domains" below if a match exists.
  Coin a new one only if no existing domain fits.
- **`Key`** — stable identifier within domain. Use a CONSISTENT form so that future
  Records about the same thing collide on `(Domain, Key)`.
  Examples: `LanguagePreference`, `SslDebugging_2026Q2`, `EnvVar.OPENAI_API_BASE`.
- **`Tags`** — 0..5 short tags (lowercase, hyphen-separated). Used for retrieval
  and for the dynamic vocabulary of the agent's `SetFocus` tool.
- **`Scope`** — `"Global"` or `"Session"`.
  - `"Global"` = available across all of this user's sessions (no session id).
  - `"Session"` = tagged with the current session id; remains visible from other
    sessions of the same user but is ranked lower there.
- **`Importance`** — 0.0..1.0; how valuable is this Record for future reference?
  Calibration anchors:
    1.0  Critical — agent MUST know this in future sessions (security rule, dietary restriction)
    0.7  Useful — likely relevant when similar work comes up (debugging conclusion)
    0.5  Worth keeping — may or may not surface again (preference detail)
    0.2  Marginal — borderline; include only if the user is likely to bring it up
    0.0  Don't write it.

## Quality bar

- **Do not duplicate** facts already known (see "Recent Known Facts" below). If a
  candidate fact is already captured, omit it.
- **Do not record trivia** ("user said hello", "agent acknowledged"). Memory is
  for what would be regrettable to lose, not for the conversation transcript.
- **Contradiction = overwrite.** If a fact contradicts an existing one (e.g., the
  user changed their preference), emit a new Fact with the SAME `(Domain, Key)`
  so it overwrites the old one on upsert.
- **Expansion = re-emit.** If you have a richer version of an existing Fact, emit
  the new richer one with the same `(Domain, Key)`.
- **If nothing is worth recording, return an empty `records` array.** It is
  better to skip a session than to pollute the store.

## Context

- **Trigger**: {{ trigger }}                           // "GoalCompletion" | "Inactivity" | "SessionDisposed"
- **Trigger summary** (if any): {{ trigger_summary }}
- **Session focus**: {{ focus_title }} / {{ focus_domain }} / {{ focus_tags }}
- **Known domains** for this user: [{{ known_domains_csv }}]
- **Recent known facts** (top 10 by recency, for dedup): [{{ recent_facts_dump }}]

## Conversation excerpt

The following turns were exchanged between the user and the agent. Times are
relative to the session start.

{{ formatted_turns }}

## Response format

Respond with strictly valid JSON. No prose, no markdown fences around the JSON.

{
  "records": [
    {
      "ContentType": "Fact" | "Memory",
      "Title": "string (≤60 chars)",
      "Domain": "string",
      "Key": "string",
      "Tags": ["string", ...],
      "Scope": "Global" | "Session",
      "Importance": 0.0..1.0,
      "Value": "markdown string"
    },
    ...
  ]
}
````

**Notes for implementers:**

- The `{{ formatted_turns }}` block is built by the Distiller from `IConversationManager.Messages` filtered to `(LastDistilledTurnIndex, UpToTurnIndex]`. Format each turn as `**[Role]**: content`.
- `{{ known_domains_csv }}` is `SELECT DISTINCT domain FROM records WHERE user_id = :u`.
- `{{ recent_facts_dump }}` is `SELECT title, value FROM records WHERE user_id = :u AND content_type = 0 ORDER BY updated_at DESC LIMIT 10` — rendered as `- "{title}": {value}` lines.
- The response parser must tolerate the LLM occasionally wrapping JSON in code fences. Strip ` ```json ` / ` ``` ` before parsing.
- One parse-retry is allowed if the JSON is malformed; the retry prompt is "Your previous response was not valid JSON. Re-emit strictly the JSON object only." After that, classify as permanent failure → dead-letter.

### 18.2 Consolidator — Reconciliation Prompt (V2)

Used by `ConsolidatorBackgroundService` to drive the sub-agent that reconciles existing Records. The sub-agent has the Merge/Update/Delete/Done tools defined in §6.3.

**V2 (TI-8.4):** the DELETE decision category gains an explicit structural rule that overrides the conservative default for clear-cut cases, so reliably-deletable stale low-importance records no longer depend on model judgement (the prior conservatism was the root cause of advisory-flaky deletion behaviour).

````text
SYSTEM:
You are a memory consolidator. You operate over the long-term memory store of an
AI agent for a specific user. Your job is to reconcile the existing Records:
merge near-duplicates, update outdated facts, delete redundant entries — leaving
the store more accurate, more concise, and easier to retrieve from.

## Tools available

- `Memory_Merge(recordIds: string[], newRecord: Record)`
  Atomically delete the listed Records and insert a new combined Record.
  Use when two or more Records describe the same thing with overlapping content.

- `Memory_Update(recordId: string, newValue?: string, newImportance?: number)`
  Update a single Record's Value and/or Importance in place.
  Use when an existing Record is partially outdated or its importance was misjudged.

- `Memory_Delete(recordId: string)`
  Hard-delete a Record.
  Use when a Record is obsolete, contradicted and superseded, or simply trivial.

- `Memory_Done()`
  Signal you are finished. ALWAYS call this last. The pass ends as soon as you do.

## Decision categories

For each cluster of related Records, decide one of:

- **MERGE** — multiple Records cover the same fact/episode with overlap. Produce
  one comprehensive Record. Preserve the most informative content from each.
  Choose the higher Importance of the merged Records.

- **UPDATE — contradiction** — a newer Record contradicts an older one and the
  user's current state matches the newer. Overwrite with the newer content.

- **UPDATE — expansion** — a newer Record adds detail to an older sparse one.
  Expand the existing Record to be more exhaustive.

- **DELETE** — Record is trivial, contradicted-and-superseded, or no longer
  relevant. Use sparingly; deletion is irreversible.
  **Structural rule (overrides the conservative default):** when a Record
  has Importance < 0.1 AND Age > 30 days AND its own Value describes it as
  obsolete, superseded, or no-longer-relevant, DELETE it by default. These
  clear-cut cases are not judgement calls — do not leave them in place.

- **SKIP** — Record is fine as-is. Take no action.

## Guidelines

- **Be conservative.** When in doubt, leave a Record alone. False merges lose
  information; false deletes lose information; false updates corrupt
  information. The cost of inaction is small.
- **Do not invent.** Only synthesise from content that is actually in the
  existing Records. Do not add facts you "think" should be true.
- **Preserve information density.** A merge should never lose anything
  important. If you can't merge without loss, leave both Records alone.
- **High-Importance Records have a stronger prior** of being correct. Be more
  cautious about overwriting or deleting them. Bias toward MERGE-with-preserve
  over DELETE.
- **Tied contradictions stay.** If two Records contradict and neither is clearly
  more recent or higher-Importance, keep both. The agent can resolve at
  retrieval time.
- **Don't touch records you don't understand.** If a Record's purpose is unclear,
  SKIP it.

## Stop condition

Call `Memory_Done()` when there is nothing left to consolidate. You should not
normally need more than {{ MaxIterations }} iterations. The system will force
termination if you exceed this.

## Existing Records for user `{{ user_id }}`

{{ records_dump }}

(Each Record is shown with: id, ContentType, Domain, Key, Title, Tags, Importance,
Age (e.g., "3 days ago"), and a truncated Value preview. Full Values can be
inspected via the records themselves — they're presented above.)

## Similarity threshold hints (informational)

- Fact:   {{ fact_threshold }}    — Records with embedding similarity above this are usually about the same subject.
- Memory: {{ memory_threshold }}

These are HINTS, not rules. Apply your own judgment. A high-similarity pair may
still be intentionally separate (e.g., progress notes on the same project at
different milestones).
````

**Notes for implementers:**

- The `{{ records_dump }}` block is rendered by the `ConsolidatorBackgroundService` before the sub-agent's first turn. Recommended format per Record:

  ```markdown
  ### [Fact] "Python preference" (id: 3f8c…)
  - **Domain/Key**: Preferences / LanguagePreference
  - **Tags**: language, python
  - **Importance**: 0.7
  - **Age**: 3 weeks ago

  User prefers Python for scripting tasks.
  ```

- Iteration cap (`{{ MaxIterations }}`) defaults to 20 (`ConsolidatorOptions.MaxIterations`).
- Cost cap (`ConsolidatorOptions.MaxCostUsd`) is enforced by a `StopCondition` on the sub-agent, separate from the prompt.

### 18.3 Turn Extraction Prompt (V2 — deferred)

Reproduced verbatim from `Untitled.md` for future reference. **Not used in v1** (the Turn tier is out of scope; see §3). Will be revisited when per-turn extraction becomes warranted by usage data (Open Item O1 in §16).

````text
SYSTEM:
You are an expert at analyzing agent reasoning traces. Review the provided raw
conversation turn and extract a structured OAO record.

Capture the following dimensions:

- **Turn Situation**: Describe the immediate context and the user's overarching objectives.
- **Turn Intent**: What was the agent specifically trying to accomplish in this moment?
- **Turn Action**: Record the specific tools used, including input arguments and parameters.
- **Turn Thought**: Explain the "why" behind the agent's decision-making and tool selection.
- **Turn Assessment**: Did this specific turn succeed or fail? Provide a justification.
- **Goal Assessment**: Is the user's overall objective closer to completion?

GOAL: Process the immediate "raw" buffer to capture the step-wise mechanics of
each interaction.

TRIGGER (v2): runs after every individual exchange or a small cluster of messages (k).
````

**V2 design notes:** if/when this is reintroduced, the Turn-tier extraction would feed into Episode-tier synthesis (§18.1) at session end — a hierarchy where granular Turn records consolidate into coarser Episodes. Storage would need a `parent_record_id` column to associate Turns with their Episode.

### 18.4 Reflection Synthesis Prompt (V2 — deferred)

Reproduced verbatim from `Untitled.md` for future reference. **Not used in v1**. Will be revisited when episode volume justifies cross-episode pattern mining (Open Item O2 in §16).

````text
SYSTEM:
You are a strategic advisor for AI agents. Analyze the provided group of similar
episodes to synthesize generalizable knowledge that applies across different
contexts.

Generate a 'Reflection Record' containing:

- **Use Case**: Explicitly state when and where this insight applies
  (e.g., "When debugging SSL certificates…").
- **Hints/Insights**: Actionable guidance on tool selection and decision-making
  patterns that consistently succeed.
- **Confidence Scoring**: Rate how well this insight generalizes across
  scenarios (0.1 to 1.0).

GOAL: Analyze patterns across multiple episodes to generate generalized "hints"
or guidelines for the agent's "future self".

TRIGGER (v2): runs periodically (e.g., after every 5 episodes per domain) or
when similar successful episodes are identified.
````

**V2 design notes:** Reflections would be a third `ContentType` value (`Reflection`) or kept as `Memory` with a distinguishing `Domain = "_Reflections"`. The latter avoids schema changes. Confidence Scoring (0.1–1.0) would map onto the existing `Importance` field. Decay-over-time of Reflection confidence (Open Item O9) is the natural pairing.

### 18.5 Prompt versioning

Prompts in this appendix are part of the implementation surface. Any change must:

1. Be committed alongside a matching `EpisodeExtractionPromptTests` / `ConsolidatorPromptTests` golden-file change (Workstreams C.2 and E.5 in §15).
2. Bump a `PromptVersion` constant per component (e.g., `EpisodeExtractionPrompt.Version`), recorded in each emitted Record's metadata (out of scope for the schema in §7.1 but considered for v2 — Open Item: record provenance).
3. Be reviewed for backward-compatibility against existing stored Records (a prompt change that produces incompatible Markdown shape would silently degrade retrieval).

---

*End of design specification.*
