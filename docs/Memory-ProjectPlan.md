# Long-Term Memory & Asynchronous Distillation — Project Plan (TDD-First)

**Source spec:** [`Memory-Specifications.md`](Memory-Specifications.md) (referred to below as **Spec**).
**Companion:** [`Memory-FuctionalSpec.md`](../../../OneDrive/Obsidian/Personal/Projects/Agency/Memory-FuctionalSpec.md).
**Audience:** Sub-agents with zero project context. Every task is self-contained.
**Protocol:** Every implementation task is preceded by a test task. Tests are written first (Red), then implementation makes them pass (Green). No task is complete without passing tests.

---

## 0. Conventions

These conventions apply to every task in this plan; they are not repeated per task.

- **Target framework:** `net10.0`.
- **Test framework:** xUnit (matches existing `*.Test` projects in the solution).
- **Mocking:** Functional/integration tests preferred; sealed classes cannot be mocked (Spec §C# conventions). Mock at interface boundaries (`ILlmClient`, `IEmbeddingGenerator`, `IMemoryStore`).
- **Visibility:** SDK members default to `internal` plus `[assembly: InternalsVisibleTo("<ProjectName>.Test")]` in each implementation project (per user convention). Only the surface that consumers must call is `public`.
- **XML docs:** Every public/internal type and method must have `///` XML doc comments with `<summary>`, `<param>`, `<returns>`. Plain `//` comments are forbidden for documentation.
- **`yield return`** is forbidden inside `try`/`catch` blocks (does not compile in C#).
- **Build verification:** `dotnet build src/Agency.slnx` must succeed after every implementation task.
- **Test verification:** `dotnet test src/Agency.slnx --filter "Category!=Functional"` must pass after every task. Functional tests (`Category=Functional`) are explicitly scoped per task.
- **Solution registration:** Every new project must be added to `src/Agency.slnx` and (if applicable) wired into `src/Directory.Packages.props`.
- **Repo style:** CRLF line endings, 4-space indent, file-scoped namespaces (per `.editorconfig`).
- **Spec cross-reference style:** "Spec §6.1" refers to section 6.1 of `Memory-Specifications.md`.

---

## 1. Deliverable Map

| Deliverable | Workstream | Output projects | Depends on |
|---|---|---|---|
| **D1** Common types & options | A | `Agency.Memory.Common`, `Agency.Memory.Common.Test` | — |
| **D2** Postgres storage | B | `Agency.Memory.Sql.Postgres`, `Agency.Memory.Sql.Postgres.Test` | D1 |
| **D3** Distiller pipeline | C | `Agency.Memory.Distiller`, `Agency.Memory.Distiller.Test` | D1, D2 |
| **D4** Retrieval engine + hooks | D | `Agency.Memory.Retrieval`, `Agency.Memory.Retrieval.Test` | D1, D2 |
| **D5** Consolidator sub-agent | E | `Agency.Memory.Consolidator`, `Agency.Memory.Consolidator.Test` | D1, D2, D3 |
| **D6** Hygiene Sweeper | F | `Agency.Memory.Hygiene`, `Agency.Memory.Hygiene.Test` | D1, D2 |
| **D7** End-to-end / functional | G | `Agency.Memory.Functional.Test` | D1–D6 |

Workstreams A → B are sequential. B → {C, D, F} fan out and may proceed in parallel. E depends on D3 because the Consolidator emits `ConsolidationJob`s on `DistillationCompletedEvent`. G blocks on all.

---

## D1 — `Agency.Memory.Common`

### Task A.0 — Scaffold projects

- **Goal:** Create the two new projects per Spec §6.1 (interface lives here) and Spec §7 (records).
- **Read first:** `src/Agency.slnx`, `src/Directory.Build.props`, `src/Directory.Packages.props`, existing project `src/VectorStore/Agency.VectorStore.Common/Agency.VectorStore.Common.csproj` for shape.
- **Deliverable:**
  - Create directory `src/Memory/Agency.Memory.Common/` and `src/Memory/Agency.Memory.Common.Test/`.
  - Create `Agency.Memory.Common.csproj` targeting `net10.0`, referencing `Agency.Embeddings.Common`, `Agency.Llm.Common`, `Agency.Agentic` (for `Context`, hooks, events).
  - Add `AssemblyInfo.cs` containing `[assembly: InternalsVisibleTo("Agency.Memory.Common.Test")]`.
  - Create `Agency.Memory.Common.Test.csproj` matching the test-project layout of `Agency.VectorStore.Common.Test` (xUnit, FluentAssertions, Microsoft.NET.Test.Sdk).
  - Register both in `src/Agency.slnx`.
- **Acceptance:** `dotnet build src/Agency.slnx` succeeds. `dotnet test src/Memory/Agency.Memory.Common.Test/Agency.Memory.Common.Test.csproj` runs zero tests with exit code 0.

---

### Task A.1.T — Test `Record` type

- **Goal:** Capture the requirements of the `Record` type per Spec §6.1 ("Record" snippet) and Spec §7.1 (storage shape).
- **Read first:** Spec §6.1, Spec §7.1, Spec §17 (Glossary).
- **Deliverable:** In `Agency.Memory.Common.Test/RecordTests.cs`, write xUnit tests:
  - `Record_RequiredFieldsAreEnforcedAtConstruction` — uses `required` keyword; missing required field is a compile error (use `[Fact]` that compiles a minimal valid Record).
  - `Record_AgeIsDerivedFromUpdatedAt` — set `UpdatedAt = now - 3.days`; assert `record.Age` is within 1s of `3.days`.
  - `Record_ImportanceBelowZero_IsInvalid` — constructing with `Importance = -0.1` throws `ArgumentOutOfRangeException`.
  - `Record_ImportanceAboveOne_IsInvalid` — `Importance = 1.5` throws.
  - `ContentType_EnumValuesAre_FactZero_MemoryOne` — explicit underlying-value test to match the SMALLINT column in Spec §7.1.
- **Acceptance:** Tests fail to compile (the type does not yet exist) — this is the Red state. Commit the failing tests separately so reviewers see Red.

### Task A.1 — Implement `Record` + `ContentType`

- **Goal:** Implement the `Record` and `ContentType` types per Spec §6.1 + §7.1.
- **Read first:** `RecordTests.cs` (Task A.1.T), Spec §6.1.
- **Deliverable:** In `Agency.Memory.Common/Records/Record.cs`:
  ```csharp
  public sealed record Record
  {
      public required string Id { get; init; }
      public required string UserId { get; init; }
      public string? SessionId { get; init; }
      public required ContentType ContentType { get; init; }
      public required string Domain { get; init; }
      public required string Key { get; init; }
      public required string Title { get; init; }
      public required string Value { get; init; }                // Markdown
      public required IReadOnlyList<string> Tags { get; init; }
      public required double Importance { get; init; }
      public required DateTimeOffset CreatedAt { get; init; }
      public required DateTimeOffset UpdatedAt { get; init; }
      public DateTimeOffset? LastAccessedAt { get; init; }
      public ReadOnlyMemory<float> Embedding { get; init; }
      public TimeSpan Age => DateTimeOffset.UtcNow - this.UpdatedAt;
  }
  public enum ContentType : short { Fact = 0, Memory = 1 }
  ```
  - Importance range validation in a static factory `Record.Create(...)` OR in a custom setter via `init` validator. Pick the factory for testability.
- **Acceptance:** All A.1.T tests pass Green.

---

### Task A.2.T — Test the ranking formula

- **Goal:** Lock the ranking formula per Spec §8.3.
- **Read first:** Spec §8.3 (formula + worked example), Spec §6.4 (engine usage).
- **Deliverable:** In `Agency.Memory.Common.Test/RankingFormulaTests.cs`:
  - `Score_AtZeroAge_RecencyEqualsOne`.
  - `Score_WorkedExampleFromSpec_Equals_0_845_WithinTolerance` — uses the §8.3 worked example values; tolerance 1e-3.
  - `Score_SessionMatchTrue_AddsPointOne` — diff between same record with sessionMatch=1 vs 0 == `wₘ` (default 0.1).
  - `Score_DefaultWeightsAreFromSpec` — `RankingWeights.Default == new(0.5, 0.3, 0.2, 0.1)`; `RecencyHalfLifeDays == 7`.
  - `Score_ClipsSimilarityToZeroOne` — negative cosine input clamped to 0.
- **Acceptance:** Red.

### Task A.2 — Implement `RankingFormula` + `RankingWeights`

- **Goal:** Implement the linear composite scoring of Spec §8.3.
- **Read first:** A.2.T, Spec §8.3.
- **Deliverable:** In `Agency.Memory.Common/Ranking/`:
  - `RankingWeights.cs`:
    ```csharp
    public sealed record RankingWeights(
        double Similarity,
        double Recency,
        double Importance,
        double SessionMatch)
    {
        public static RankingWeights Default { get; } = new(0.5, 0.3, 0.2, 0.1);
    }
    ```
  - `RankingFormula.cs` — `internal static double Score(double similarity, Record record, string? currentSessionId, DateTimeOffset now, RankingWeights w, double halfLifeDays)`.
  - All math is deterministic; no `Random`.
- **Acceptance:** All A.2.T pass.

---

### Task A.3.T — Test the options records

- **Goal:** Capture the configuration shape locked in OpenItems Item 9 and Spec §10.3.
- **Read first:** Spec §10.3, OpenItems Item 9, Spec §6.6.
- **Deliverable:** In `Agency.Memory.Common.Test/OptionsTests.cs`:
  - `MemoryOptions_DefaultsMatchSpec` — `CollectionName == "agency_memory"`, `RetrievalTopK == 10`, default thresholds per OpenItems Item 9.
  - `DistillerOptions_DefaultsMatchSpec` — `InactivityTimeout == 5.minutes`, `MaxRetries == 3`, `RetryBaseDelay == 2.seconds`, `PerSessionQueueCapacity == 32`, `Backpressure == DropOldest`.
  - `ConsolidatorOptions_DefaultsMatchSpec` — `Trigger == OnSessionEnd`, `MaxIterations == 20`, `MaxCostUsd == 0.50`.
  - `MemoryOptions_BindsFromConfiguration` — round-trip from `appsettings.json`-style `IConfiguration` to options via `IOptions<>` binding.
- **Acceptance:** Red.

### Task A.3 — Implement options records

- **Goal:** Realise the configuration surface per Spec §10.3 + OpenItems Item 9.
- **Read first:** A.3.T, Spec §10.3.
- **Deliverable:** In `Agency.Memory.Common/Options/`:
  - `MemoryOptions.cs` — fields per Spec §10.3 (CollectionName, Ranking, RecencyHalfLifeDays, RetrievalTopK, OverFetchFactor (default 3), ConsolidationSimilarityThreshold dictionary, Ttl dictionary keyed by `ContentType`, ImportancePruneThreshold (default 0.2), StalePruneAge (default `30.days`), HygieneSchedule (default `24.hours`)).
  - `DistillerOptions.cs` — fields per Spec §10.3 + OpenItems 9.
  - `ConsolidatorOptions.cs` — fields per Spec §10.3 + OpenItems 9.
  - `BackpressurePolicy.cs` (enum): `DropOldest`, `DropNewest`, `Wait`.
  - `ConsolidationTrigger.cs` (enum): `OnSessionEnd`, `Manual` (Spec §6.3 V1 column).
  - All records use `init`-only setters; defaults set inline.
- **Acceptance:** All A.3.T pass.

---

### Task A.4.T — Test `IMemoryStore` contract

- **Goal:** Capture the operations contract of Spec §6.1.
- **Read first:** Spec §6.1 ("Inputs/Outputs" table), Spec §8.1 (LastWrittenAt invariant).
- **Deliverable:** In `Agency.Memory.Common.Test/IMemoryStoreContractTests.cs`, write contract-style abstract tests that any `IMemoryStore` implementation must pass:
  - `Upsert_NewRecord_ReturnsAssignedIdAndCreatedAtSet`.
  - `Upsert_ExistingUpsertKey_PreservesId_UpdatesValueAndUpdatedAt`.
  - `Search_FiltersByContentType`.
  - `Search_FiltersByDomain`.
  - `Search_RespectsTopK`.
  - `Search_OrdersByCosineDistance`.
  - `Forget_ExistingKey_ReturnsTrueAndRemovesRow`.
  - `Forget_MissingKey_ReturnsFalse`.
  - `ForgetMe_RemovesAllRowsForUser_DoesNotAffectOthers`.
  - `LastWrittenAt_NullForUnknownUser`.
  - `LastWrittenAt_AdvancesOnUpsert`.
  - `LastWrittenAt_AdvancesOnForget`.
  - `LastWrittenAt_AdvancesOnForgetMe`.
  - Tests are parameterised over a `Func<IMemoryStore>` so Postgres-backed and in-memory implementations both satisfy the contract.
- **Acceptance:** Red — interface does not yet exist.

### Task A.4 — Implement `IMemoryStore` + `SearchQuery`/`SearchHit` DTOs

- **Goal:** Define the storage abstraction per Spec §6.1.
- **Read first:** A.4.T, Spec §6.1.
- **Deliverable:** In `Agency.Memory.Common/Storage/`:
  - `IMemoryStore.cs`:
    ```csharp
    public interface IMemoryStore
    {
        Task<Record> UpsertAsync(Record record, CancellationToken ct = default);
        Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken ct = default);
        Task<Record?> GetByKeyAsync(string userId, string? sessionId, string domain, string key, CancellationToken ct = default);
        Task<bool> ForgetAsync(string userId, string domain, string key, CancellationToken ct = default);
        Task<int> ForgetMeAsync(string userId, CancellationToken ct = default);
        Task<DateTimeOffset?> LastWrittenAtAsync(string userId, CancellationToken ct = default);
        Task<IReadOnlyList<Record>> GetAllForUserAsync(string userId, CancellationToken ct = default);
        Task<int> DeleteWhereTtlExceededAsync(ContentType ct_, TimeSpan ttl, CancellationToken ct = default);
        Task<int> DeleteWhereLowImportanceStaleAsync(double importanceThreshold, TimeSpan staleAge, CancellationToken ct = default);
    }
    ```
  - `SearchQuery.cs` — `record SearchQuery(string UserId, ReadOnlyMemory<float> QueryEmbedding, int TopK, ContentType? ContentType = null, string? Domain = null)`.
  - `SearchHit.cs` — `record SearchHit(Record Record, double Similarity)`.
- **Acceptance:** A.4.T compiles (contract abstract still cannot run — no implementation yet); Workstream B picks it up.

---

### Task A.5.T — Test `MemoryHookFactory` baseline composition

- **Goal:** Capture the baseline-first ordering rule (Spec §6.5, OpenItems Item 6).
- **Read first:** Spec §6.5, `src/Agentic/Agency.Agentic/Hooks/AgentHooks.cs`, `src/Agentic/Agency.Agentic/Hooks/AgentHooksExtensions.cs`.
- **Deliverable:** In `Agency.Memory.Common.Test/MemoryHookFactoryTests.cs`:
  - `Build_ProducesBaselineWithRetrieval_DistillerTimer_AuditHooks`.
  - `Compose_BaselineFirst_UserHookSeesEnrichedContext` — assert `Context.Knowledge` is populated by baseline before user hook runs.
  - `ComposeBefore_UserHookFirst_SeesEmptyContext` — assert escape hatch.
  - `OldOnPostToolUseFailure_NotRegistered` — assert the removed hook is not in the baseline `AgentHooks`.
- **Acceptance:** Red.

### Task A.5 — Implement `MemoryHookFactory` + `ComposeBefore`

- **Goal:** Provide the baseline-hook composition layer per Spec §6.5.
- **Read first:** A.5.T, Spec §6.5, existing `AgentHooksExtensions.Compose`.
- **Deliverable:**
  - In `src/Agentic/Agency.Agentic/Hooks/AgentHooks.cs`: add `OnUserPromptSubmit`, `OnPreIteration`, `OnPostToolBatch` delegates. **Remove** `OnPostToolUseFailure` (Spec §14.7). This is a breaking change in the Agentic project — update call sites in `Agent.cs`.
  - In `AgentHooksExtensions.cs`: add `public static AgentHooks ComposeBefore(this AgentHooks self, AgentHooks first)`.
  - In `Agency.Memory.Common/Hooks/MemoryHookFactory.cs`: factory taking `IMemoryStore`, `IEmbeddingGenerator`, `Channel<DistillationJob>.Writer`, `InactivityTimerService`, `IOptions<MemoryOptions>`; returns a baseline `AgentHooks` wiring retrieval to `OnPreIteration` and timer-restart to `OnAssistantTurn`.
  - In `Agency.Memory.Common/DependencyInjection/MemoryServiceCollectionExtensions.cs`: `public static IServiceCollection AddAgencyMemory(this IServiceCollection services, Action<MemoryBuilder> configure)` that registers all options, the factory, and `PostConfigure<AgencyAgenticOptions>` to set baseline hooks.
- **Acceptance:** All A.5.T pass; `dotnet build` of full solution still succeeds (call sites in `Agent.cs` updated).

---

### Task A.6.T — Test `DistillationJob` + `ConsolidationJob` value semantics

- **Goal:** Lock the job payload shapes per Spec §6.2 + §6.3.
- **Read first:** Spec §6.2 (Inputs/Outputs), Spec §6.3.
- **Deliverable:** In `Agency.Memory.Common.Test/JobsTests.cs`:
  - `DistillationJob_Equality_ByValue`.
  - `DistillationJob_RoundTrips_ThroughSystemTextJson`.
  - `DistillationTrigger_HasThreeValues_GoalCompletion_Inactivity_SessionDisposed`.
  - `ConsolidationJob_Equality_ByValue`.
- **Acceptance:** Red.

### Task A.6 — Implement `DistillationJob`, `ConsolidationJob`, triggers

- **Goal:** Provide the channel-payload types per Spec §6.2 + §6.3.
- **Read first:** A.6.T, Spec §6.2.
- **Deliverable:** In `Agency.Memory.Common/Jobs/`:
  - `DistillationJob.cs`:
    ```csharp
    public sealed record DistillationJob(
        string UserId,
        string SessionId,
        DistillationTrigger Trigger,
        int UpToTurnIndex,
        string? TriggerSummary = null);
    ```
  - `DistillationTrigger.cs`: `enum { GoalCompletion, Inactivity, SessionDisposed }`.
  - `ConsolidationJob.cs`: `record (string UserId, string TriggeredBySessionId)`.
- **Acceptance:** All A.6.T pass.

---

### Task A.7.T — Test memory `AgentEvent` payloads

- **Goal:** Cover the `DistillationCompletedEvent`, `DistillationFailedEvent`, `ConsolidationCompletedEvent` shapes per Spec §6.2.
- **Read first:** Spec §6.2, `src/Agentic/Agency.Agentic/AgentEvents.cs`.
- **Deliverable:** In `Agency.Memory.Common.Test/MemoryEventsTests.cs`: tests for required fields, event-type discriminator, and `IsTerminal` semantics (mirror existing `AgentEvent` pattern).
- **Acceptance:** Red.

### Task A.7 — Implement memory events

- **Goal:** Define the event payloads emitted by background services.
- **Read first:** A.7.T, existing `AgentEvents.cs`.
- **Deliverable:** In `Agency.Memory.Common/Events/MemoryEvents.cs`:
  - `record DistillationCompletedEvent(string UserId, string SessionId, int RecordsWritten, int NewWatermark) : AgentEvent;`
  - `record DistillationFailedEvent(string UserId, string SessionId, string Reason, bool DeadLettered) : AgentEvent;`
  - `record ConsolidationCompletedEvent(string UserId, int Merges, int Updates, int Deletes) : AgentEvent;`
- **Acceptance:** All A.7.T pass.

---

## D2 — `Agency.Memory.Sql.Postgres`

### Task B.0 — Scaffold projects

- **Goal:** Create the Postgres-backed implementation projects per Spec §6.1 + §7.
- **Read first:** Existing `src/VectorStore/Agency.VectorStore.Sql.Postgres/Agency.VectorStore.Sql.Postgres.csproj` for structure, Spec §7.6.
- **Deliverable:**
  - Create `src/Memory/Agency.Memory.Sql.Postgres/` and `src/Memory/Agency.Memory.Sql.Postgres.Test/`.
  - `Agency.Memory.Sql.Postgres.csproj` references: `Agency.Memory.Common`, `Agency.Sql.Postgres`, `Npgsql`, `Pgvector` (versions per `Directory.Packages.props`).
  - `[assembly: InternalsVisibleTo("Agency.Memory.Sql.Postgres.Test")]`.
  - Register in `src/Agency.slnx`.
- **Acceptance:** `dotnet build src/Agency.slnx` succeeds.

---

### Task B.1.T — Test schema initialiser

- **Goal:** Lock the DDL of Spec §7.1–§7.4.
- **Read first:** Spec §7.1, §7.2, §7.3, §7.4. Existing `src/Mcp/Agency.Mcp.Memory` for prior schema-init pattern.
- **Deliverable:** In `Agency.Memory.Sql.Postgres.Test/SchemaInitializerTests.cs` (xUnit, `[Category=Functional]`, requires running Postgres from `src/docker-compose.yml`):
  - `Init_CreatesRecordsTable_WithAllColumnsAndConstraints`.
  - `Init_CreatesHnswIndex_OnEmbedding_WithCosineOps`.
  - `Init_CreatesUniqueIndex_OnUpsertKey`.
  - `Init_CreatesWatermarksTable_WithPrimaryKey`.
  - `Init_CreatesDeadLetterTable_WithCreatedAtIndex`.
  - `Init_IsIdempotent_RunTwiceDoesNotFail`.
  - `Init_RejectsEmbeddingDimMismatch` — passing a different embedding dim than what's in the existing column throws.
  - Use Npgsql to query `information_schema` to verify column types, nullability, defaults, and `pg_indexes` for index definitions.
- **Acceptance:** Red.

### Task B.1 — Implement `MemorySchemaInitializer`

- **Goal:** Provision the schema per Spec §7.
- **Read first:** B.1.T, Spec §7, `src/Mcp/Agency.Mcp.Memory` for prior pattern.
- **Deliverable:** In `Agency.Memory.Sql.Postgres/MemorySchemaInitializer.cs`:
  - Class with `Task InitializeAsync(int embeddingDim, CancellationToken ct)`.
  - Executes the three `CREATE TABLE IF NOT EXISTS` statements verbatim from Spec §7.1, §7.3, §7.4 — with `embedding vector(@dim)` parameterised on `embeddingDim`.
  - Creates indexes from Spec §7.2.
  - HNSW: `CREATE INDEX IF NOT EXISTS records_embedding_hnsw ON records USING hnsw (embedding vector_cosine_ops) WITH (m = 16, ef_construction = 64);`.
  - Throws `InvalidOperationException` if `embedding vector(N)` already exists with a different N.
  - Registered as `IHostedService` (or invoked from `AddAgencyMemory(...).UsePostgres(...)`).
- **Acceptance:** All B.1.T pass against Docker Postgres.

---

### Task B.2.T — Test `PostgresMemoryStore.UpsertAsync`

- **Goal:** Cover upsert + LastWrittenAt invariant per Spec §6.1 ("Internal flow" steps 1–4).
- **Read first:** Spec §6.1 Upsert flow, Spec §8.1 (cache invariant), A.4.T.
- **Deliverable:** In `Agency.Memory.Sql.Postgres.Test/PostgresMemoryStoreTests_Upsert.cs`:
  - `Upsert_NewRecord_AssignsIdAndTimestamps_AndRowExistsInDb`.
  - `Upsert_SameUpsertKey_OverwritesValue_PreservesId_BumpsUpdatedAt`.
  - `Upsert_DifferentSessionId_SameDomainKey_CreatesSecondRow`.
  - `Upsert_BumpsLastWrittenAtCacheAndDb`.
  - `Upsert_TwoConcurrentInsertsSameKey_OneWins_OtherUpdates` — `Task.WhenAll`, then count == 1.
  - `Upsert_GeneratesEmbedding_WhenEmpty` — uses a stub `IEmbeddingGenerator`.
- **Acceptance:** Red.

### Task B.2 — Implement `PostgresMemoryStore.UpsertAsync`

- **Goal:** Implement upsert per Spec §6.1 + §8.1.
- **Read first:** B.2.T, Spec §6.1.
- **Deliverable:** In `Agency.Memory.Sql.Postgres/PostgresMemoryStore.cs`:
  - Constructor: `(NpgsqlDataSource dataSource, IEmbeddingGenerator embedder, IOptions<MemoryOptions> options, ILogger<PostgresMemoryStore> log)`.
  - SQL: `INSERT INTO records (...) VALUES (...) ON CONFLICT ON CONSTRAINT records_upsert_key DO UPDATE SET title=EXCLUDED.title, value=EXCLUDED.value, tags=EXCLUDED.tags, importance=EXCLUDED.importance, embedding=EXCLUDED.embedding, updated_at=now() RETURNING id, created_at, updated_at;`.
  - In-memory `ConcurrentDictionary<string, DateTimeOffset>` for `LastWrittenAt` cache; write-through on every successful upsert.
  - OpenTelemetry: `ActivitySource("Agency.Memory.Sql.Postgres")`, `Meter` with `memory.upsert.count`, `memory.upsert.duration` per Spec §6 observability pattern.
- **Acceptance:** All B.2.T pass.

---

### Task B.3.T — Test `PostgresMemoryStore.SearchAsync`

- **Goal:** Cover filtered vector search per Spec §6.1 (Search flow) and §6.4.
- **Read first:** Spec §6.1 Search flow.
- **Deliverable:** In `PostgresMemoryStoreTests_Search.cs`:
  - `Search_OrdersByCosineDistance_LowestFirst`.
  - `Search_FilterByContentType_ExcludesOthers`.
  - `Search_FilterByDomain_ExcludesOthers`.
  - `Search_RespectsTopK`.
  - `Search_BumpsLastAccessedAt_AsynchronouslyForHits` — wait 100ms after call, then query; `LastAccessedAt` set for returned rows only.
  - `Search_EmptyStore_ReturnsEmptyList`.
- **Acceptance:** Red.

### Task B.3 — Implement `PostgresMemoryStore.SearchAsync`

- **Goal:** Vector search per Spec §6.1.
- **Read first:** B.3.T, Spec §6.1.
- **Deliverable:** In `PostgresMemoryStore.cs`:
  - SQL: `SELECT id, ..., embedding <=> $1 AS distance FROM records WHERE user_id = $2 [AND content_type = $3] [AND domain = $4] ORDER BY distance ASC LIMIT $5;`.
  - Compute `similarity = 1 - distance` (cosine distance → similarity).
  - After result materialised, fire-and-forget batched `UPDATE records SET last_accessed_at = now() WHERE id = ANY($1)` on the thread pool (do not await).
- **Acceptance:** All B.3.T pass.

---

### Task B.4.T — Test forget operations

- **Goal:** Cover `ForgetAsync` and `ForgetMeAsync` per Spec §6.1 + Use Case U4.
- **Read first:** Spec §6.1, Spec §2 (U4).
- **Deliverable:** In `PostgresMemoryStoreTests_Forget.cs`:
  - `Forget_KnownKey_ReturnsTrue_DeletesRow_BumpsLastWritten`.
  - `Forget_UnknownKey_ReturnsFalse_NoSideEffects`.
  - `ForgetMe_DeletesAllRecordsForUser_ReturnsCount`.
  - `ForgetMe_DoesNotAffectOtherUsers` — seed two users, forget one, assert the other intact.
  - `ForgetMe_BumpsLastWrittenAt` (per Spec §8.1 invariant).
- **Acceptance:** Red.

### Task B.4 — Implement forget operations

- **Goal:** Hard-delete paths per Spec §6.1.
- **Read first:** B.4.T.
- **Deliverable:** Two SQL `DELETE` statements; bump `LastWrittenAt` cache and a `user_state` table column on every mutation. No tombstones (Spec §6.1 Constraints).
- **Acceptance:** All B.4.T pass.

---

### Task B.5.T — Test `LastWrittenAtAsync`

- **Goal:** Cover the cache + persistence behaviour per Spec §8.1.
- **Read first:** Spec §8.1, Spec §6.1 `LastWrittenAtAsync` row.
- **Deliverable:** In `PostgresMemoryStoreTests_LastWritten.cs`:
  - `LastWrittenAt_UnknownUser_ReturnsNull`.
  - `LastWrittenAt_AfterUpsert_ReturnsUpsertTime`.
  - `LastWrittenAt_CacheHydratesFromDbOnRestart` — construct two store instances over the same connection; first writes, second reads without write.
  - `LastWrittenAt_BumpedByForget_AndForgetMe`.
- **Acceptance:** Red.

### Task B.5 — Implement `LastWrittenAtAsync` + cache hydration

- **Goal:** Power the retrieval gate per Spec §8.1.
- **Read first:** B.5.T, Spec §8.1.
- **Deliverable:** `user_state(user_id PK, last_written_at TIMESTAMPTZ NOT NULL)` table (add to `MemorySchemaInitializer` — back-port to B.1). `LastWrittenAtAsync` checks cache; on miss, SELECTs and seeds cache. All mutations write-through. Schema migration: add task A.0/B.1 covers this in the initial DDL.
- **Acceptance:** All B.5.T pass.

---

### Task B.6.T — Test `WatermarkRepository`

- **Goal:** Cover Spec §7.3 + Spec §6.2 watermark behaviour.
- **Read first:** Spec §6.2 (idempotency), Spec §7.3.
- **Deliverable:** In `WatermarkRepositoryTests.cs`:
  - `Get_UnknownSession_ReturnsZero`.
  - `Advance_MonotonicallyIncreases_OldValueIgnored` — advance(5), advance(3) → still 5.
  - `Advance_RestartHydrationFromDb` — write, dispose, new repo reads same value.
  - `Delete_OnSessionDispose_RemovesRow` (optional cleanup path; v1 may leave rows).
- **Acceptance:** Red.

### Task B.6 — Implement `WatermarkRepository`

- **Goal:** Per-session watermark persistence per Spec §7.3.
- **Read first:** B.6.T.
- **Deliverable:** `WatermarkRepository.cs` with `GetAsync(userId, sessionId)`, `AdvanceAsync(userId, sessionId, candidate)` using `INSERT ... ON CONFLICT DO UPDATE SET last_distilled_turn_idx = GREATEST(watermarks.last_distilled_turn_idx, EXCLUDED.last_distilled_turn_idx)`.
- **Acceptance:** All B.6.T pass.

---

### Task B.7.T — Test `DeadLetterRepository`

- **Goal:** Cover Spec §7.4.
- **Read first:** Spec §7.4, Spec §8.6 (Error taxonomy).
- **Deliverable:** In `DeadLetterRepositoryTests.cs`:
  - `Write_PersistsJobPayloadAsJsonb_AndErrorText`.
  - `ListSince_ReturnsRowsCreatedAfterCutoff`.
- **Acceptance:** Red.

### Task B.7 — Implement `DeadLetterRepository`

- **Goal:** Persist failed jobs per Spec §7.4.
- **Read first:** B.7.T, Spec §7.4.
- **Deliverable:** `DeadLetterRepository.cs` with `WriteAsync(string jobKind, object payload, Exception error)` and `ListSinceAsync(DateTimeOffset cutoff)`. Payload serialised via `System.Text.Json` to `jsonb`.
- **Acceptance:** All B.7.T pass.

---

### Task B.8 — Re-run A.4.T contract tests against `PostgresMemoryStore`

- **Goal:** Prove the implementation satisfies the contract from Task A.4.T.
- **Read first:** A.4.T, B.1–B.7 implementations.
- **Deliverable:** In `Agency.Memory.Sql.Postgres.Test/PostgresMemoryStoreContractTests.cs`, instantiate the abstract contract from `Agency.Memory.Common.Test` (move it to a shared `*.TestKit` project if cross-project sharing requires it) and run against a Docker Postgres connection.
- **Acceptance:** All contract tests Green. Add `[Category=Functional]` so they only run when Postgres is available.

---

## D3 — Distiller (`Agency.Memory.Distiller`)

### Task C.0 — Scaffold

- **Goal:** Project skeleton per Spec §6.2 + §10.
- **Read first:** Spec §6.2, Spec §10, existing `BackgroundService` host wiring in the repo.
- **Deliverable:** `src/Memory/Agency.Memory.Distiller/` + `.Test`. References: `Agency.Memory.Common`, `Microsoft.Extensions.Hosting`, `Microsoft.Extensions.Logging`, `Agency.Llm.Common`, `Agency.Embeddings.Common`, `Agency.Agentic`. Register in `Agency.slnx`. Add `InternalsVisibleTo`.
- **Acceptance:** Build succeeds.

---

### Task C.1.T — Test `EpisodeExtractionPrompt` rendering & parsing

- **Goal:** Lock the prompt template per Spec §18.1 and parser per Spec §8.2.
- **Read first:** Spec §18.1, Spec §8.2.
- **Deliverable:** In `Agency.Memory.Distiller.Test/EpisodeExtractionPromptTests.cs`:
  - `Render_IncludesTriggerName_FocusFields_KnownDomains_RecentFactsDump`.
  - `Render_OmitsTriggerSummary_WhenNull`.
  - `Parse_StripsCodeFences_BeforeJson` — handles ` ```json ` wrapping.
  - `Parse_ValidPayload_ReturnsRecords`.
  - `Parse_InvalidJson_ThrowsExtractionParseException` — used to drive 1 retry.
  - `Parse_EmptyRecordsArray_IsValid_ReturnsZeroRecords` (Spec §18.1 "If nothing is worth recording…").
  - `Golden_FactExtraction_FromPythonPreferenceTranscript` — `PromptVersion=1` golden file (per Spec §18.5).
  - `Golden_MemoryExtraction_FromSslDebuggingTranscript` — OAO-shaped output golden file.
- **Acceptance:** Red.

### Task C.1 — Implement `EpisodeExtractionPrompt` + parser

- **Goal:** Spec §18.1 + §8.2 prompt and parser.
- **Read first:** C.1.T.
- **Deliverable:**
  - `Prompts/EpisodeExtractionPrompt.cs` — `Render(DistillationJob job, IReadOnlyList<Message> turns, FocusContext focus, IReadOnlyList<string> knownDomains, IReadOnlyList<Record> recentFacts)` returns the prompt string. Embeds the verbatim template from Spec §18.1.
  - `Prompts/EpisodeExtractionParser.cs` — `Parse(string llmResponse)` strips fences, deserialises, validates required fields per Spec §18.1 "Required fields per Record", returns `IReadOnlyList<Record>` (with `CreatedAt = now`, `UpdatedAt = now`, `Id = Guid.NewGuid().ToString()`, embedding empty).
  - `EpisodeExtractionPrompt.Version = 1` constant per Spec §18.5.
- **Acceptance:** All C.1.T pass (golden files committed alongside).

---

### Task C.2.T — Test `InactivityTimerService`

- **Goal:** Cover the per-session timer per Spec §10 + §6.2.
- **Read first:** Spec §10.2 + §6.2 (3 triggers).
- **Deliverable:** In `InactivityTimerServiceTests.cs`:
  - `Start_FirstCall_StartsTimer`.
  - `Restart_ResetsTimer_DoesNotFireEarly` — use injected `TimeProvider` from `Microsoft.Extensions.TimeProvider.Testing`.
  - `Expiry_EnqueuesDistillationJob_WithInactivityTrigger`.
  - `Stop_OnDispose_CancelsTimer_NoFireAfter`.
  - `Restart_AfterExpiry_StartsFreshTimer`.
- **Acceptance:** Red.

### Task C.2 — Implement `InactivityTimerService`

- **Goal:** Per Spec §10 ("Cardinality 1 per process, state per-session").
- **Read first:** C.2.T, Spec §10.2.
- **Deliverable:** `Services/InactivityTimerService.cs` — `IHostedService` singleton holding `ConcurrentDictionary<string sessionId, (Timer, DistillerSessionState)>`. Methods: `Restart(string userId, string sessionId, int currentTurnIndex)`, `Stop(string sessionId)`. On expiry enqueues to `Channel<DistillationJob>.Writer`. Uses `TimeProvider` for testability.
- **Acceptance:** All C.2.T pass.

---

### Task C.3.T — Test `DistillerBackgroundService` happy path

- **Goal:** Cover the core loop of Spec §6.2 "Internal flow".
- **Read first:** Spec §6.2 pseudocode, Spec §11.4 (cost considerations).
- **Deliverable:** In `DistillerBackgroundServiceTests.cs`:
  - `Distill_DequeueJob_ReadsTurnsBetweenWatermarkAndUpTo_CallsLlm_UpsertsRecord_AdvancesWatermark`.
  - `Distill_EmitsDistillationCompletedEventWithCount`.
  - `Distill_UsesConversationManagerReadOnly`.
  - `Distill_EmptyTurnRange_SkipsLlmCall_EmitsCompletedWithZero`.
  - Test doubles: `FakeLlmClient` returning a canned JSON payload; `FakeEmbeddingGenerator` returning a deterministic vector; in-memory `IMemoryStore` (from contract testkit); in-memory `IConversationManager`.
- **Acceptance:** Red.

### Task C.3 — Implement `DistillerBackgroundService` happy path

- **Goal:** Spec §6.2 pseudocode realised.
- **Read first:** C.3.T, Spec §6.2.
- **Deliverable:** `DistillerBackgroundService.cs` extending `BackgroundService`. Reads from `ChannelReader<DistillationJob>`. Per job: resolve session from registry → load turns → render prompt (C.1) → call `ILlmClient.SendAsync` → parse → embed each Record (concatenate `Title + "\n\n" + Value` per Spec §6.2 Implementation notes) → `IMemoryStore.UpsertAsync` → `WatermarkRepository.AdvanceAsync` → emit `DistillationCompletedEvent` through an `IAsyncEventBus` (define in `Agency.Memory.Common/Events`).
- **Acceptance:** All C.3.T pass.

---

### Task C.4.T — Test idempotency by watermark

- **Goal:** Cover Spec §6.2 Constraints "Idempotency by watermark" + Spec §13 step 07.
- **Read first:** Spec §6.2 Constraints.
- **Deliverable:** In `DistillerBackgroundServiceTests.cs`:
  - `Distill_JobUpToIndexAlreadyReached_NoOps_NoLlmCall_NoUpsert`.
  - `Distill_TwoQueuedJobs_SecondSkipsBecauseFirstAdvancedWatermark`.
- **Acceptance:** Red.

### Task C.4 — Implement watermark guard

- **Goal:** Idempotency per Spec §6.2.
- **Read first:** C.4.T.
- **Deliverable:** In `DistillerBackgroundService` core loop: read current watermark before dispatch; if `job.UpToTurnIndex <= currentWatermark`, log + emit `DistillationCompletedEvent { RecordsWritten = 0 }` + continue.
- **Acceptance:** All C.4.T pass.

---

### Task C.5.T — Test retry + dead-letter

- **Goal:** Cover the error taxonomy of Spec §8.6.
- **Read first:** Spec §8.6, Spec §6.2 Implementation notes (retry).
- **Deliverable:** In `DistillerBackgroundServiceTests_Retry.cs`:
  - `Distill_TransientLlmFailure_RetriesUpToMaxRetries_ThenDeadLetters`.
  - `Distill_PermanentLlmFailure_DeadLettersImmediately`.
  - `Distill_RetryDelay_FollowsExponentialBackoff` — assert `Task.Delay` calls via `TimeProvider.Testing` advance.
  - `Distill_MalformedJsonOnce_RetriesWithStricterPrompt_ThenSucceeds`.
  - `Distill_MalformedJsonTwice_DeadLetters`.
  - `Distill_CancellationToken_PropagatesWithoutDeadLetter`.
- **Acceptance:** Red.

### Task C.5 — Implement retry + dead-letter

- **Goal:** Spec §6.2 retry loop + §8.6 taxonomy.
- **Read first:** C.5.T, Spec §8.6.
- **Deliverable:** Wrap the per-job body in `for (int attempt = 0; attempt <= maxRetries; attempt++)` with classification: HTTP 429/503/timeout/Postgres deadlock → transient; HTTP 400/401/422 + final-parse-failure → permanent. On permanent or exhaustion, call `DeadLetterRepository.WriteAsync` and emit `DistillationFailedEvent { DeadLettered = true }`. `OperationCanceledException` propagates without dead-letter.
- **Acceptance:** All C.5.T pass.

---

### Task C.6.T — Test `MarkGoalCompleteTool` + `SetFocusTool`

- **Goal:** Cover Spec §6.7.
- **Read first:** Spec §6.7.1, §6.7.2.
- **Deliverable:** In `Agency.Memory.Distiller.Test/Tools/`:
  - `MarkGoalCompleteToolTests`:
    - `Invoke_EnqueuesJobWithGoalCompletionTrigger_AndOptionalSummary`.
    - `Invoke_DoesNotStopLoop_ReturnsSuccessToolResult`.
    - `Invoke_ConcurrentTwiceInSameSession_BothJobsEnqueue_SecondNoOpsOnDequeue` (combined with C.4).
  - `SetFocusToolTests`:
    - `Invoke_UpdatesContextFocus`.
    - `ToolDescription_ListsDistinctDomainsForUser` (queries `IMemoryStore`).
    - `Invoke_SameValuesTwice_IsNoOp_ReturnsPriorFocus`.
- **Acceptance:** Red.

### Task C.6 — Implement `MarkGoalCompleteTool` + `SetFocusTool`

- **Goal:** Spec §6.7.
- **Read first:** C.6.T, existing tools under `src/Agentic/Agency.Agentic/Tools/` for shape.
- **Deliverable:** Two `AgentTool` subclasses in `Agency.Memory.Distiller/Tools/`. `MarkGoalCompleteTool` constructor takes `ChannelWriter<DistillationJob>` + session/turn accessors. `SetFocusTool` takes `IMemoryStore` for dynamic description.
- **Acceptance:** All C.6.T pass.

---

### Task C.7.T — Test `OnAssistantTurn` hook is timer-only

- **Goal:** Negative regression test for Spec §14.9.
- **Read first:** Spec §14.9, Spec §6.2 Implementation notes (first bullet).
- **Deliverable:** In `OnAssistantTurnHookTests.cs`:
  - `OnAssistantTurn_RestartsInactivityTimer_DoesNotEnqueueJob` — assert `Channel.Reader.Count == 0` after the hook fires.
- **Acceptance:** Red.

### Task C.7 — Implement timer-only `OnAssistantTurn`

- **Goal:** Spec §14.9.
- **Read first:** C.7.T.
- **Deliverable:** In `MemoryHookFactory.Build()` (cross-project edit), wire `OnAssistantTurn = (msg, ctx, ct) => timer.Restart(ctx.User.Id, ctx.Session.Id, ctx.Conversation.LastTurnIndex)` — no other side-effects.
- **Acceptance:** All C.7.T pass.

---

### Task C.8.T — Test backpressure

- **Goal:** Cover Spec §10.3 channel cap.
- **Read first:** Spec §10.3, Spec §6.2 Constraints (Bounded channel).
- **Deliverable:** `Channel_AtCapacity_DropsOldest_LogsWarning` — fill channel with 33 items, assert item #1 dropped, log emitted.
- **Acceptance:** Red.

### Task C.8 — Configure bounded channel

- **Goal:** Spec §10.3.
- **Read first:** C.8.T.
- **Deliverable:** In DI wiring, register `Channel.CreateBounded<DistillationJob>(new BoundedChannelOptions(opts.PerSessionQueueCapacity) { FullMode = BoundedChannelFullMode.DropOldest })`. Per-session segmentation via `ConcurrentDictionary<sessionId, Channel<DistillationJob>>` to honour Spec §5 "One queue per session".
- **Acceptance:** C.8.T passes.

---

## D4 — Retrieval Engine + Hooks (`Agency.Memory.Retrieval`)

### Task D.0 — Scaffold

- **Goal:** Project skeleton per Spec §6.4 + §6.5.
- **Deliverable:** `src/Memory/Agency.Memory.Retrieval/` + `.Test`. References `Agency.Memory.Common`, `Agency.Agentic`, `Agency.Embeddings.Common`. Register in `Agency.slnx`.
- **Acceptance:** Build succeeds.

---

### Task D.1.T — Test the retrieval gate

- **Goal:** Cover Spec §8.1 gate logic and §6.4 internal flow.
- **Read first:** Spec §8.1, §6.4.
- **Deliverable:** In `RetrievalGateTests.cs`:
  - `Gate_FirstCall_RunsSearch` — `Context.MemoryLastRetrievedAt == null` → executes.
  - `Gate_StoreUnchangedSinceLastRetrieval_Skips` — pre-set `MemoryLastRetrievedAt`; store `LastWrittenAt < it` → no search call.
  - `Gate_StoreWrittenSince_RunsSearch`.
  - `Gate_DoesNotMutateContext_OnSkip`.
- **Acceptance:** Red.

### Task D.1 — Implement `RetrievalGate`

- **Goal:** Spec §8.1.
- **Read first:** D.1.T.
- **Deliverable:** `RetrievalGate.cs` — `internal static async ValueTask<bool> ShouldRetrieveAsync(Context ctx, IMemoryStore store, CancellationToken ct)` returning true iff `lastWritten == null || ctx.MemoryLastRetrievedAt == null || lastWritten > ctx.MemoryLastRetrievedAt`.
- **Acceptance:** D.1.T passes.

---

### Task D.2.T — Test `RetrievalEngine`

- **Goal:** Cover Spec §6.4.
- **Read first:** Spec §6.4 (Internal flow), Spec §8.3, Spec §13 (E2E trace).
- **Deliverable:** In `RetrievalEngineTests.cs`:
  - `Retrieve_OverFetches_TopK_TimesOverFetchFactor_FromStore`.
  - `Retrieve_AppliesRankingFormula_OrderingByCompositeScore` — fixture with deliberately-mis-ordered similarity to prove composite wins.
  - `Retrieve_PartitionsByContentType_FactsToKnowledge_MemoriesToMemory`.
  - `Retrieve_RespectsTopK_CapsCombined`.
  - `Retrieve_EmptyStore_AssignsEmptyLists_DoesNotThrow`.
  - `Retrieve_SetsMemoryLastRetrievedAtToNow`.
  - `Retrieve_AppendsFocusToQueryText` — assert embedding called with `lastUserMessage + " " + focus.Title + " " + focus.Domain + " " + tagsCsv`.
- **Acceptance:** Red.

### Task D.2 — Implement `RetrievalEngine`

- **Goal:** Spec §6.4.
- **Read first:** D.2.T.
- **Deliverable:** `RetrievalEngine.cs` matching the pseudocode of Spec §6.4. Uses `RankingFormula.Score`. Writes to `Context.Knowledge` (`KnowledgeContext`) and `Context.Memory` (`MemoryContext`). Sets `Context.MemoryLastRetrievedAt`.
- **Acceptance:** All D.2.T pass.

---

### Task D.3.T — Test `SystemPromptBuilder` memory rendering

- **Goal:** Cover Spec §6.4 ("System prompt rendering") + Spec §13 sample output.
- **Read first:** Spec §6.4, Spec §13 (Session N+1 system prompt).
- **Deliverable:** In `Agency.Agentic.Test/SystemPromptBuilderTests_Memory.cs`:
  - `Build_WithFacts_RendersFactsSection_WithRecencyHint_NotRawTimestamp`.
  - `Build_WithMemories_RendersMemoriesSection_OaoMarkdownPreserved`.
  - `Build_EmptyKnowledgeAndMemory_RendersNoRelevantMemoriesNote`.
  - `Build_NeverIncludesRawScoreOrEmbedding`.
- **Acceptance:** Red.

### Task D.3 — Extend `SystemPromptBuilder`

- **Goal:** Spec §6.4 prompt assembly.
- **Read first:** D.3.T, `src/Agentic/Agency.Agentic/SystemPromptBuilder.cs`.
- **Deliverable:** Edit `SystemPromptBuilder` to add `## Facts` and `## Memories` sections, formatting each as `- **{Title}** (Updated {RelativeTime}) \n  {Value}`. Relative time via a helper `Humanize(TimeSpan)` (no NuGet — implement: "just now" / "{N} minutes ago" / "{N} hours ago" / "{N} days ago" / "{N} weeks ago" / "{N} months ago").
- **Acceptance:** All D.3.T pass; existing `SystemPromptBuilder` tests still pass.

---

### Task D.4.T — Test `OnUserPromptSubmit` and `OnPostToolBatch` wiring

- **Goal:** Cover the new hook firing points per Spec §6.5.
- **Read first:** Spec §6.5 Inputs/Outputs table, `src/Agentic/Agency.Agentic/Agent.cs` (locate where to invoke).
- **Deliverable:** In `Agency.Agentic.Test/HookFiringTests.cs`:
  - `OnUserPromptSubmit_FiresOnceBeforeFirstIteration`.
  - `OnUserPromptSubmit_FiresEveryChatAsyncCall`.
  - `OnPreIteration_FiresBeforeSystemPromptBuild`.
  - `OnPostToolBatch_FiresAfterTaskWhenAllReturns_BeforeNextLlmCall`.
- **Acceptance:** Red.

### Task D.4 — Wire new hook invocations in `Agent.cs`

- **Goal:** Spec §6.5.
- **Read first:** D.4.T, `src/Agentic/Agency.Agentic/Agent.cs`.
- **Deliverable:** Edit `Agent.RunAsync` to invoke `OnUserPromptSubmit` at the top of `ChatAsync`, `OnPreIteration` at top of each iteration before `SystemPromptBuilder.Build`, `OnPostToolBatch` after `Task.WhenAll` of tool calls. Each hook awaited; exceptions propagate per existing exception policy.
- **Acceptance:** All D.4.T pass.

---

## D5 — Consolidator (`Agency.Memory.Consolidator`)

### Task E.0 — Scaffold

- **Goal:** Project skeleton per Spec §6.3.
- **Deliverable:** `src/Memory/Agency.Memory.Consolidator/` + `.Test`. References: `Agency.Memory.Common`, `Agency.Agentic` (sub-agent reuses Agent harness), `Microsoft.Extensions.Hosting`.
- **Acceptance:** Build succeeds.

---

### Task E.1.T — Test consolidator tools

- **Goal:** Cover the four tools of Spec §6.3 "Tools given to the consolidator sub-agent".
- **Read first:** Spec §6.3.
- **Deliverable:** In `ConsolidatorToolsTests.cs`:
  - `MemoryMerge_DeletesListedRecords_InsertsNewRecord_Atomically` — kill the connection mid-transaction; assert no partial state.
  - `MemoryMerge_NewRecord_GetsFreshIdAndTimestamps`.
  - `MemoryUpdate_OnlyValue_UpdatesValueAndUpdatedAt_PreservesImportance`.
  - `MemoryUpdate_OnlyImportance_UpdatesImportance_NotValue`.
  - `MemoryDelete_HardDeletes_BumpsLastWritten`.
  - `MemoryDone_SignalsStop` — wraps in a stop-condition test.
- **Acceptance:** Red.

### Task E.1 — Implement consolidator tools

- **Goal:** Spec §6.3.
- **Read first:** E.1.T, Spec §6.3 + §8.4.
- **Deliverable:** Four `AgentTool` subclasses in `Agency.Memory.Consolidator/Tools/`. `MemoryMergeTool` uses a single `NpgsqlTransaction`: `DELETE ... WHERE id = ANY(@ids)` then `INSERT`. `MemoryUpdateTool` uses `UPDATE records SET value = COALESCE(@v, value), importance = COALESCE(@i, importance), updated_at = now()`. All four bump `LastWrittenAt` cache.
- **Acceptance:** All E.1.T pass.

---

### Task E.2.T — Test `ConsolidatorReconciliationPrompt`

- **Goal:** Cover Spec §18.2.
- **Read first:** Spec §18.2.
- **Deliverable:** In `ConsolidatorReconciliationPromptTests.cs`:
  - `Render_IncludesUserId_RecordsDump_ThresholdHints_MaxIterations`.
  - `Render_RecordsDump_FormatsEachRecordWithIdContentTypeDomainKeyTitleTagsImportanceAgeAndValuePreview`.
  - `Golden_PromptOutput_ForFiveRecordsFixture`.
- **Acceptance:** Red.

### Task E.2 — Implement reconciliation prompt

- **Goal:** Spec §18.2.
- **Read first:** E.2.T.
- **Deliverable:** `Prompts/ConsolidatorReconciliationPrompt.cs` embedding the verbatim template from Spec §18.2. `Version = 1` constant.
- **Acceptance:** All E.2.T pass.

---

### Task E.3.T — Test `ConsolidatorSubAgentFactory` + service happy path

- **Goal:** Cover Spec §6.3 internal flow + §8.4 decision loop.
- **Read first:** Spec §6.3, §8.4.
- **Deliverable:** In `ConsolidatorBackgroundServiceTests.cs`:
  - `Consolidate_DuplicateFacts_MergesIntoOne_ViaSubAgent` — stub LLM returns canned tool-call sequence: `Memory_Merge([id1,id2], newRec)` then `Memory_Done()`.
  - `Consolidate_NoRecords_ExitsImmediately_NoLlmCall`.
  - `Consolidate_PerUser_SerialExecution_TwoTriggersCoalesce` — fire two `ConsolidationJob`s for same user; assert sub-agent invoked once or one-at-a-time.
  - `Consolidate_StopOnDone_TerminatesLoop`.
  - `Consolidate_MaxIterationsReached_StopsAndLogsWarning`.
  - `Consolidate_MaxCostUsdExceeded_StopsAndLogsWarning`.
  - `Consolidate_EmitsConsolidationCompletedEventWithCounts`.
- **Acceptance:** Red.

### Task E.3 — Implement `ConsolidatorSubAgentFactory` + `ConsolidatorBackgroundService`

- **Goal:** Spec §6.3 + §8.4.
- **Read first:** E.3.T, existing `Agent.cs` to understand sub-agent composition.
- **Deliverable:**
  - `ConsolidatorSubAgentFactory.cs` — `Create(string userId, IReadOnlyList<Record> records, IMemoryStore store)` builds an `Agent` instance with the consolidation tools, the reconciliation prompt, a `MaxIterations` stop condition, and a `MaxCostUsd` stop condition.
  - `ConsolidatorBackgroundService.cs` — `BackgroundService` consuming a `Channel<ConsolidationJob>` partitioned per `userId` (per-user serial; cross-user parallel per Spec §10.2). Subscribes to `DistillationCompletedEvent` and enqueues a `ConsolidationJob` (Spec §6.3 trigger).
  - Coalescing: a `ConcurrentDictionary<string userId, bool inFlight>` ensures only one job runs per user; subsequent triggers set a "pending" flag instead of enqueueing.
  - Warning emitted if `records.Count > MaxRecordsPerPass` (Spec §6.3 V1 column).
- **Acceptance:** All E.3.T pass.

---

## D6 — Hygiene Sweeper (`Agency.Memory.Hygiene`)

### Task F.0 — Scaffold

- **Goal:** Project skeleton per Spec §6.6.
- **Deliverable:** `src/Memory/Agency.Memory.Hygiene/` + `.Test`. References: `Agency.Memory.Common`, `Microsoft.Extensions.Hosting`.
- **Acceptance:** Build succeeds.

---

### Task F.1.T — Test TTL pass

- **Goal:** Cover Spec §6.6 TTL pass + §8.5.
- **Read first:** Spec §6.6, §8.5.
- **Deliverable:** In `HygieneSweeperTests_Ttl.cs`:
  - `Ttl_FactsOlderThanFactTtl_AndNotAccessedSince_Deleted`.
  - `Ttl_FactsAccessedRecently_NotDeleted` — `LastAccessedAt > now - ttl` survives.
  - `Ttl_NoTtlConfiguredForContentType_NoDeletes`.
  - `Ttl_DistinctTtlsPerContentType_AppliedIndependently`.
- **Acceptance:** Red.

### Task F.1 — Implement TTL pass

- **Goal:** Spec §6.6 TTL bulk DELETE.
- **Read first:** F.1.T, Spec §8.5 SQL.
- **Deliverable:** In `HygieneSweeperBackgroundService.cs`, the TTL pass calls `IMemoryStore.DeleteWhereTtlExceededAsync(ContentType, TimeSpan)` for each configured TTL. Postgres implementation (back-port to B.4): executes the SQL from Spec §8.5.
- **Acceptance:** All F.1.T pass.

---

### Task F.2.T — Test importance pass

- **Goal:** Cover Spec §6.6 importance pass.
- **Read first:** Spec §6.6.
- **Deliverable:** In `HygieneSweeperTests_Importance.cs`:
  - `Importance_LowImportance_StaleAge_Deleted`.
  - `Importance_LowImportance_RecentlyAccessed_NotDeleted`.
  - `Importance_HighImportance_StaleAge_NotDeleted`.
- **Acceptance:** Red.

### Task F.2 — Implement importance pass

- **Goal:** Spec §6.6.
- **Read first:** F.2.T, Spec §8.5 SQL.
- **Deliverable:** Second `DeleteWhereLowImportanceStaleAsync` call. Each pass commits independently.
- **Acceptance:** All F.2.T pass.

---

### Task F.3.T — Test schedule

- **Goal:** Cover Spec §6.6 schedule + §10.3 jitter.
- **Read first:** Spec §10.3.
- **Deliverable:** In `HygieneSweeperTests_Schedule.cs`:
  - `Schedule_FiresAtConfiguredInterval` — use `TimeProvider.Testing`.
  - `Schedule_AppliesJitterUpTo15Min`.
  - `Schedule_CancellationToken_TerminatesLoop`.
  - `Schedule_EmitsDeletionCountMetric_PerPass`.
- **Acceptance:** Red.

### Task F.3 — Implement scheduler

- **Goal:** Spec §6.6 + §10.3.
- **Read first:** F.3.T.
- **Deliverable:** `HygieneSweeperBackgroundService.ExecuteAsync` uses `PeriodicTimer` with `opts.HygieneSchedule + Random.Shared.Next(-15, 15).Minutes()`. Logs and metrics emit deletion counts per pass.
- **Acceptance:** All F.3.T pass.

---

## D7 — End-to-end Functional (`Agency.Memory.Functional.Test`)

### Task G.0 — Scaffold

- **Goal:** Functional-test project per Spec §15 Workstream G.
- **Read first:** `src/Llm/Agency.Llm.Test/` for the `Category=Functional` pattern.
- **Deliverable:** `src/Memory/Agency.Memory.Functional.Test/`. References every memory project + `Agency.Llm.OpenAI` / `Agency.Llm.Claude`. All tests have `[Trait("Category", "Functional")]`. README in project root explaining prereqs (Postgres up, LM Studio at `http://llm-host.example:1234`).
- **Acceptance:** Build succeeds; `dotnet test --filter "Category=Functional"` runs zero tests with exit code 0 initially.

---

### Task G.1 — `EndToEnd_FactWrittenInSessionN_RecalledInSessionNPlus1`

- **Goal:** Reproduce Spec §13 end-to-end.
- **Read first:** Spec §13.
- **Deliverable:** Single test that:
  1. Boots a host with all memory services.
  2. Opens `ChatSession`, sends "I prefer Python." Expects no recall.
  3. Waits for `DistillationCompletedEvent` (with timeout 60s).
  4. Disposes session; waits for second `DistillationCompletedEvent` (no-op by watermark).
  5. Waits for `ConsolidationCompletedEvent`.
  6. Opens new `ChatSession` for same `userId`. Sends "Write me a script to deduplicate this list."
  7. Asserts the assistant's system prompt (captured via a test-only inspection hook) contains "Python".
- **Acceptance:** Test passes against real LM Studio + Postgres. Marked `[Category=Functional]`.

---

### Task G.2 — `EndToEnd_ForgetMeWipesAllUserData`

- **Goal:** Spec §12.4 + Use Case U4.
- **Deliverable:** Seed 10 records for two users; invoke `IMemoryStore.ForgetMeAsync(userA)`; assert `GetAllForUserAsync(userA)` empty, `userB` untouched, `LastWrittenAt[userA]` bumped.
- **Acceptance:** Passes.

---

### Task G.3 — `EndToEnd_HighFrequencyTurns_HotPathLatencyUnaffected`

- **Goal:** Spec §11.1 budget.
- **Deliverable:** Run a 30-turn session with the memory pipeline enabled; capture per-iteration `OnPreIteration` duration via `Activity` events; assert p95 ≤ 500 ms and that 90% of iterations are gate-hits (no embedding+search).
- **Acceptance:** Passes.

---

### Task G.4 — `EndToEnd_DistillerCrash_RecoversFromWatermark`

- **Goal:** Spec §12.2 (Process crash mid-distillation).
- **Deliverable:** Stub `ILlmClient` that throws on first invocation; assert watermark unchanged; second run with normal stub completes; watermark advances; turns re-distilled exactly once.
- **Acceptance:** Passes.

---

### Task G.5 — `EndToEnd_ConsolidatorMergesContradiction_LatestStateRetained`

- **Goal:** Spec §6.3 + Use Case U3.
- **Deliverable:** Pre-seed two contradictory facts (`prefers Postgres` then `prefers SQLite`) with the same `(Domain, Key)`. Trigger consolidator. Assert one Record remains; `Value` references SQLite; `Importance ≥ old.Importance`.
- **Acceptance:** Passes.

---

## Cross-Cutting / Sequencing Notes

### Parallelisation map

```
A.0 ─► A.1 ─► A.2 ─► A.3 ─► A.4 ─► A.5 ─► A.6 ─► A.7
                                         │
              ┌──────────────────────────┴───────────────────────┐
              ▼                                                  ▼
   B.0 ─► B.1 ─► B.2 ─► B.3 ─► B.4 ─► B.5 ─► B.6 ─► B.7 ─► B.8
                                                              │
   ┌──────────────────────────────┬──────────────────────────┤
   ▼                              ▼                          ▼
   C.0 ─► C.1 ─► ... ─► C.8       D.0 ─► D.1 ─► ... ─► D.4   F.0 ─► F.1 ─► F.2 ─► F.3
                            │
                            ▼
                       E.0 ─► E.1 ─► E.2 ─► E.3
                                          │
   ┌──────────────────────────────────────┘
   ▼
   G.0 ─► G.1, G.2, G.3, G.4, G.5  (parallel within G)
```

### Done definition (per task)

A task is complete only when **all** of the following hold:

1. Tests in the corresponding `*.T` task are Green.
2. `dotnet build src/Agency.slnx` succeeds.
3. `dotnet test src/Agency.slnx --filter "Category!=Functional"` passes.
4. New code includes XML doc comments on every public/internal type and method.
5. No `// TODO` or `// FIXME` left in the code (the task is closed; if scope grew, file a follow-up against Spec §16 Open Items).
6. No new public surface beyond what the Spec lists (resist scope drift — see CLAUDE.md §3 "Surgical Changes").

### Tracking / handoff

- Bug tracker (per CLAUDE.md): https://www.notion.so/99f10b50431d4089b667a8ec603e9e60.
- Task tracker: https://www.notion.so/262d51d057a942dcb0af645e4d8d76ae.
- Each task in this plan should be mirrored to a Notion task before assignment; the Notion task carries status, the markdown task carries the spec.
- Do **not** commit code unless explicitly asked (CLAUDE.md). Submit work via diff/PR review.

---

*End of project plan.*
