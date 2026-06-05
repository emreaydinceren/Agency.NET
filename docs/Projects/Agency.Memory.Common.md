# Agency.Memory.Common
#memory #common #abstractions

## What It Is

Agency.Memory.Common is the shared contract and data-model library for the Agency long-term memory system. It defines every type that crosses a subsystem boundary — the `Record` entity, `IMemoryStore`, the event bus, job payloads, ranking primitives, and configuration options — so that the Distiller, Retrieval Engine, Consolidator, and Hygiene Sweeper can all compile against one stable surface without introducing circular dependencies between implementation projects.

**Namespace:** `Agency.Memory.Common`

---

## API Surface

### Records

```csharp
// File: src/Memory/Agency.Memory.Common/Records/ContentType.cs
using Agency.Memory.Common.Records;

/// <summary>Discriminates between durable factual knowledge and episodic memories.</summary>
public enum ContentType : short
{
    /// <summary>An impersonal, durable fact (e.g., user preference, domain constant).</summary>
    Fact = 0,

    /// <summary>An episodic memory of a past interaction, stored in OAO format.</summary>
    Memory = 1,
}
```

```csharp
// File: src/Memory/Agency.Memory.Common/Records/Record.cs
using Agency.Memory.Common.Records;

/// <summary>
/// A single durable memory entry. Represents either a Fact or a Memory (OAO-shaped).
/// </summary>
public sealed record Record
{
    public required string Id { get; init; }
    public required string UserId { get; init; }
    public string? SessionId { get; init; }                  // null = user-global
    public required ContentType ContentType { get; init; }
    public required string Domain { get; init; }
    public required string Key { get; init; }
    public required string Title { get; init; }
    public required string Value { get; init; }              // Markdown body
    public required IReadOnlyList<string> Tags { get; init; }
    public required double Importance { get; init; }         // [0, 1], fixed at write time
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? LastAccessedAt { get; init; }
    public ReadOnlyMemory<float> Embedding { get; init; }
    public TimeSpan Age => DateTimeOffset.UtcNow - this.UpdatedAt;

    /// <summary>Validates importance is in [0, 1] and returns a new Record.</summary>
    public static Record Create(
        string id, string userId, string? sessionId,
        ContentType contentType, string domain, string key,
        string title, string value, IReadOnlyList<string> tags,
        double importance, DateTimeOffset createdAt, DateTimeOffset updatedAt,
        DateTimeOffset? lastAccessedAt = null,
        ReadOnlyMemory<float> embedding = default);
}
```

---

### Storage

```csharp
// File: src/Memory/Agency.Memory.Common/Storage/SearchQuery.cs
using Agency.Memory.Common.Records;
using Agency.Memory.Common.Storage;

/// <summary>Parameters for a vector similarity search against the memory store.</summary>
public sealed record SearchQuery(
    string UserId,
    ReadOnlyMemory<float> QueryEmbedding,
    int TopK,
    ContentType? ContentType = null,   // null = no filter
    string? Domain = null);            // null = no filter
```

```csharp
// File: src/Memory/Agency.Memory.Common/Storage/SearchHit.cs
using Agency.Memory.Common.Records;
using Agency.Memory.Common.Storage;

/// <summary>A single result from IMemoryStore.SearchAsync.</summary>
public sealed record SearchHit(Record Record, double Similarity);
```

```csharp
// File: src/Memory/Agency.Memory.Common/Storage/IMemoryStore.cs
using Agency.Memory.Common.Records;
using Agency.Memory.Common.Storage;

/// <summary>
/// Vector-backed, user-partitioned store for all durable Record items.
/// Single source of truth for the memory subsystem.
/// </summary>
public interface IMemoryStore
{
    /// <summary>Insert or update via upsert key (UserId, SessionId, Domain, Key).</summary>
    Task<Record> UpsertAsync(Record record, CancellationToken ct = default);

    /// <summary>Vector similarity search with optional ContentType / Domain filters.</summary>
    Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken ct = default);

    /// <summary>Point lookup by the upsert key; null if not found.</summary>
    Task<Record?> GetByKeyAsync(string userId, string? sessionId, string domain, string key, CancellationToken ct = default);

    /// <summary>Hard-delete by (userId, domain, key); returns true if a row was removed.</summary>
    Task<bool> ForgetAsync(string userId, string domain, string key, CancellationToken ct = default);

    /// <summary>GDPR-style wipe for a single user; returns deleted count.</summary>
    Task<int> ForgetMeAsync(string userId, CancellationToken ct = default);

    /// <summary>Most-recent mutation timestamp for a user; drives the retrieval gate.</summary>
    Task<DateTimeOffset?> LastWrittenAtAsync(string userId, CancellationToken ct = default);

    /// <summary>Returns every record for a user (used by the Consolidator).</summary>
    Task<IReadOnlyList<Record>> GetAllForUserAsync(string userId, CancellationToken ct = default);

    /// <summary>Bulk TTL sweep — deletes records of the given ContentType that are stale.</summary>
    Task<int> DeleteWhereTtlExceededAsync(
        ContentType contentType, TimeSpan ttl, DateTimeOffset now,
        CancellationToken ct = default);

    /// <summary>Importance-pruning sweep — deletes low-importance stale records.</summary>
    Task<int> DeleteWhereLowImportanceStaleAsync(
        double importanceThreshold, TimeSpan staleAge, DateTimeOffset now,
        CancellationToken ct = default);

    /// <summary>
    /// Atomic delete-ids + insert-new in one transaction (Consolidator Memory_Merge tool).
    /// </summary>
    Task<Record> MergeAsync(IReadOnlyList<string> idsToDelete, Record newRecord, CancellationToken ct = default);

    /// <summary>Partial update of Value and/or Importance by record id; refreshes UpdatedAt.</summary>
    Task<Record?> UpdateRecordAsync(
        string recordId, string userId,
        string? newValue, double? newImportance,
        CancellationToken ct = default);

    /// <summary>Hard-delete a single record by surrogate id; bumps LastWrittenAt.</summary>
    Task<bool> DeleteByIdAsync(string recordId, string userId, CancellationToken ct = default);
}
```

---

### Ranking

```csharp
// File: src/Memory/Agency.Memory.Common/Ranking/RankingWeights.cs
using Agency.Memory.Common.Ranking;

/// <summary>
/// Linear combination weights for the composite ranking formula (Spec §8.3).
/// </summary>
public sealed record RankingWeights(
    double Similarity,    // wₛ
    double Recency,       // wᵣ
    double Importance,    // wᵢ
    double SessionMatch)  // wₘ (additive bonus)
{
    /// <summary>Default weights: wₛ=0.5, wᵣ=0.3, wᵢ=0.2, wₘ=0.1.</summary>
    public static RankingWeights Default { get; } = new(0.5, 0.3, 0.2, 0.1);
}
```

`RankingFormula` is `internal` (visible to `Agency.Memory.Retrieval` via `InternalsVisibleTo`). It implements the scoring equation described in the **How It Works** section below.

---

### Events

```csharp
// File: src/Memory/Agency.Memory.Common/Events/IAsyncEventBus.cs
using Agency.Memory.Common.Events;
using Agency.Harness;

/// <summary>
/// In-process event bus for publishing AgentEvents from background services.
/// Dispatch is polymorphic: a subscriber to a base type receives derived published events.
/// </summary>
public interface IAsyncEventBus
{
    Task PublishAsync<T>(T evt, CancellationToken ct = default)
        where T : AgentEvent;

    /// <summary>Returns a disposable that removes the subscription when disposed.</summary>
    IDisposable Subscribe<T>(Func<T, CancellationToken, Task> handler)
        where T : AgentEvent;
}
```

```csharp
// File: src/Memory/Agency.Memory.Common/Events/MemoryEvents.cs
using Agency.Harness;
using Agency.Memory.Common.Events;

/// <summary>
/// Abstract base for distillation outcomes. Subscribe to this type to observe any
/// settlement (success or failure) without racing the two concrete subtypes (TI-8.1).
/// </summary>
public abstract record DistillationSettledEvent(string UserId, string SessionId) : AgentEvent;

public sealed record DistillationCompletedEvent(
    string UserId, string SessionId,
    int RecordsWritten, int NewWatermark)
    : DistillationSettledEvent(UserId, SessionId);

public sealed record DistillationFailedEvent(
    string UserId, string SessionId,
    string Reason, bool DeadLettered)
    : DistillationSettledEvent(UserId, SessionId);

public sealed record ConsolidationCompletedEvent(
    string UserId,
    int Merges, int Updates, int Deletes)
    : AgentEvent;

/// <summary>
/// Emitted per Consolidator tool call (Merge/Update/Delete). First-class observable for
/// autonomous memory changes — hosts surface these to the user (TI-8.3).
/// </summary>
public sealed record MemoryMutatedEvent(
    string UserId, string Operation, string Detail)
    : AgentEvent;
```

`InMemoryEventBus` is the `internal` implementation of `IAsyncEventBus` (visible to `Agency.Memory.Distiller`, `Agency.Memory.Functional.Test`).

---

### Jobs

```csharp
// File: src/Memory/Agency.Memory.Common/Jobs/DistillationTrigger.cs
using Agency.Memory.Common.Jobs;

/// <summary>Identifies what caused a DistillationJob to be enqueued.</summary>
public enum DistillationTrigger
{
    GoalCompletion,   // agent called MarkGoalComplete
    Inactivity,       // inactivity timer expired
    SessionDisposed,  // ChatSession was disposed
}
```

```csharp
// File: src/Memory/Agency.Memory.Common/Jobs/DistillationJob.cs
using Agency.Harness.Contexts;
using Agency.Memory.Common.Jobs;

/// <summary>Payload enqueued when one of the three distillation triggers fires.</summary>
public sealed record DistillationJob(
    string UserId,
    string SessionId,
    DistillationTrigger Trigger,
    int UpToTurnIndex,
    string? TriggerSummary = null,
    FocusContext? Focus = null);  // snapshot of Context.Focus at trigger time
```

```csharp
// File: src/Memory/Agency.Memory.Common/Jobs/ConsolidationJob.cs
using Agency.Memory.Common.Jobs;

/// <summary>
/// Payload enqueued to the consolidation channel after a distillation completes.
/// </summary>
public sealed record ConsolidationJob(
    string UserId,
    string TriggeredBySessionId);
```

---

### Options

```csharp
// File: src/Memory/Agency.Memory.Common/Options/BackpressurePolicy.cs
using Agency.Memory.Common.Options;

public enum BackpressurePolicy { DropOldest, DropNewest, Wait }
```

```csharp
// File: src/Memory/Agency.Memory.Common/Options/ConsolidationTrigger.cs
using Agency.Memory.Common.Options;

public enum ConsolidationTrigger { OnSessionEnd, Manual }
```

```csharp
// File: src/Memory/Agency.Memory.Common/Options/MemoryOptions.cs
using Agency.Memory.Common.Options;
using Agency.Memory.Common.Ranking;
using Agency.Memory.Common.Records;

public sealed class MemoryOptions
{
    public string CollectionName { get; set; } = "agency_memory";
    public RankingWeights Ranking { get; set; } = RankingWeights.Default;
    public double RecencyHalfLifeDays { get; set; } = 7.0;
    public int RetrievalTopK { get; set; } = 10;
    public int OverFetchFactor { get; set; } = 3;
    public Dictionary<ContentType, double> ConsolidationSimilarityThreshold { get; set; } = [];
    public Dictionary<ContentType, TimeSpan> Ttl { get; set; } = [];
    public double ImportancePruneThreshold { get; set; } = 0.2;
    public TimeSpan StalePruneAge { get; set; } = TimeSpan.FromDays(30);
    public TimeSpan HygieneSchedule { get; set; } = TimeSpan.FromHours(24);
}
```

```csharp
// File: src/Memory/Agency.Memory.Common/Options/DistillerOptions.cs
using Agency.Memory.Common.Options;

public sealed class DistillerOptions
{
    public TimeSpan InactivityTimeout { get; set; } = TimeSpan.FromMinutes(5);
    public int MaxRetries { get; set; } = 3;
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(2);
    public int PerSessionQueueCapacity { get; set; } = 32;
    public BackpressurePolicy Backpressure { get; set; } = BackpressurePolicy.DropOldest;
    public TimeSpan ShutdownDrainTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
```

```csharp
// File: src/Memory/Agency.Memory.Common/Options/ConsolidatorOptions.cs
using Agency.Memory.Common.Options;

public sealed class ConsolidatorOptions
{
    public ConsolidationTrigger Trigger { get; set; } = ConsolidationTrigger.OnSessionEnd;
    public int MaxIterations { get; set; } = 20;
    public decimal MaxCostUsd { get; set; } = 0.50m;
    public string? Model { get; set; }
}
```

---

### Hooks

```csharp
// File: src/Memory/Agency.Memory.Common/Hooks/MemoryHookFactory.cs
using Agency.Harness.Contexts;
using Agency.Harness.Hooks;
using Agency.Memory.Common.Hooks;

/// <summary>
/// Produces the baseline AgentHooks that wire retrieval and the inactivity timer
/// into the agent loop without requiring the caller to depend on implementation projects.
/// </summary>
public static class MemoryHookFactory
{
    public static AgentHooks Build(
        Func<Context, CancellationToken, Task> retrievalCallback,
        Func<AssistantTurnHookContext, CancellationToken, Task> timerRestartCallback,
        Func<SessionStartedHookContext, CancellationToken, Task>? sessionStartedCallback = null,
        Func<SessionEndedHookContext, CancellationToken, Task>? sessionEndCallback = null);

    /// <summary>Stub-wired hooks for unit testing the composition shape.</summary>
    public static AgentHooks BuildEmpty();
}
```

---

## How It Works

The types in this library form the backbone of a capture-store-retrieve-consolidate loop. The flow is:

1. **Write path** — When a distillation trigger fires (goal complete / inactivity / session dispose), the harness enqueues a `DistillationJob` into a per-session bounded channel. The job carries a snapshot of `UpToTurnIndex` and the `FocusContext` at trigger time. The `DistillerBackgroundService` (in `Agency.Memory.Distiller`) dequeues it, extracts `Record` instances via an LLM, embeds each one, and calls `IMemoryStore.UpsertAsync`. After a session's final distillation, a `ConsolidationJob` is enqueued so the Consolidator can merge or prune the user's corpus.

2. **Read path** — On every agent iteration, `MemoryHookFactory`'s baseline `OnPreIteration` hook calls the Retrieval Engine (in `Agency.Memory.Retrieval`). The engine compares `Context.MemoryLastRetrievedAt` against `IMemoryStore.LastWrittenAtAsync` to decide whether to skip (gate hit) or run a full vector search (gate miss). When it runs, the engine over-fetches (`RetrievalTopK × OverFetchFactor`) from the store, then re-ranks the wider pool using `RankingFormula.Score` before trimming to `RetrievalTopK`.

3. **Events** — Background services publish `AgentEvent`-derived events via `IAsyncEventBus`. The abstract `DistillationSettledEvent` base lets any consumer observe job completion on either success or failure without subscribing to both concrete subtypes separately. `MemoryMutatedEvent` is emitted once per Consolidator tool call so hosts can surface autonomous memory changes to the user.

### Ranking Formula (Spec §8.3)

```
score = wₛ · clip(similarity, 0, 1)
      + wᵣ · exp(−ageDays / halfLifeDays)
      + wᵢ · record.Importance
      + wₘ · sessionMatch          // 1 if record.SessionId == currentSessionId, else 0
```

**Default weights:** wₛ = 0.5, wᵣ = 0.3, wᵢ = 0.2, wₘ = 0.1, halfLifeDays = 7.

The weights intentionally do not normalise to 1.0 — the session-match term is an additive bonus on top of the core score, not part of the probability simplex.

**Worked example** — current-session record, similarity 0.8, age 2 days, importance 0.6:

```
score = 0.5·0.8 + 0.3·exp(−2/7) + 0.2·0.6 + 0.1·1
      = 0.40    + 0.225           + 0.12     + 0.10
      = 0.845
```

Same record from a different session: `0.40 + 0.225 + 0.12 + 0 = 0.745`.

---

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.Harness]] | Upstream dependency. `Record` events inherit from `AgentEvent`; `MemoryHookFactory` produces `AgentHooks`; `DistillationJob` carries a `FocusContext` from `Agency.Harness.Contexts`. |
| [[Agency.Embeddings.Common]] | Upstream dependency. `IMemoryStore.UpsertAsync` generates embeddings via `IEmbeddingGenerator` when `Record.Embedding` is empty. |
| Agency.Memory.Retrieval | Downstream consumer. Implements the retrieval gate and ranking using `IMemoryStore`, `SearchQuery`, `SearchHit`, `RankingFormula` (internal), and `RankingWeights`. Has `InternalsVisibleTo` access. |
| Agency.Memory.Distiller | Downstream consumer. Implements `DistillerBackgroundService`; enqueues and processes `DistillationJob`; publishes `DistillationSettledEvent` variants. Has `InternalsVisibleTo` access. Hosts `AddAgencyMemory` DI entry point. |
| Agency.Memory.Consolidator | Downstream consumer. Implements `ConsolidatorBackgroundService`; dequeues `ConsolidationJob`; calls `IMemoryStore.MergeAsync`, `UpdateRecordAsync`, `DeleteByIdAsync`; publishes `ConsolidationCompletedEvent` and `MemoryMutatedEvent`. |
| Agency.Memory.Hygiene | Downstream consumer. Implements `HygieneSweeperBackgroundService`; calls `IMemoryStore.DeleteWhereTtlExceededAsync` and `DeleteWhereLowImportanceStaleAsync`; reads `MemoryOptions` for TTL and importance thresholds. |
| Agency.Memory.Sql.Postgres | Downstream implementation. Provides the PostgreSQL + pgvector implementation of `IMemoryStore`. |
| Agency.Memory.Common.Test | Test project. Has `InternalsVisibleTo` access for unit testing internal types. |
| Agency.Memory.Functional.Test | Functional test project. Has `InternalsVisibleTo` access to instantiate `InMemoryEventBus` directly and access internal infrastructure for crash-recovery tests. |

---

## Design Notes

- **Single `Record` type with a `ContentType` discriminator, not separate classes.** Both `Fact` and `Memory` go to the same vector store, share the same ranking formula, and have identical storage operations. Splitting them into distinct C# types would double the surface area (constructors, mappers, validators, tests) for what is purely a categorisation difference. A discriminator column costs compile-time exhaustiveness checks; the spec accepts that tradeoff because `ContentType` has only two values and is unlikely to grow (Spec §14.2).

- **`SessionId` is a ranking signal, not a partition key.** Records tagged with a session remain visible from other sessions; they just receive a lower composite score (wₘ = 0.1 by default). Hard isolation was rejected because cross-session continuity is often desirable — the agent should be able to recall "we hit this same problem last Tuesday" — and soft-signal behaviour is reversible at retrieval time by raising wₘ or adding a post-hoc filter, whereas removing hard isolation requires a schema change (Spec §14.3 / P3).

- **`MemoryHookFactory` accepts pre-built callbacks instead of depending on implementation projects directly.** `Agency.Memory.Common` cannot reference `Agency.Memory.Retrieval` or `Agency.Memory.Distiller` without creating a circular dependency (both already reference `Common`). `MemoryHookFactory.Build` therefore takes `Func<>` parameters; the `AddAgencyMemory` DI extension in `Agency.Memory.Distiller` supplies the real `RetrievalEngine` and `InactivityTimerService` callbacks at registration time. This keeps the contract library free of implementation references (IQ-1, documented in `MemoryHookFactory.cs` remarks).

- **`IMemoryStore.DeleteWhereTtlExceededAsync` and `DeleteWhereLowImportanceStaleAsync` take an explicit `now` parameter rather than using the database clock.** The Hygiene Sweeper passes its injected `TimeProvider.GetUtcNow()` so that tests using a `FakeTimeProvider` can drive expiry deterministically without waiting for real wall-clock time. Production always uses `TimeProvider.System`, which equals the DB wall clock in practice (TI-4, Spec §8.5).

- **`DistillationSettledEvent` is an abstract base, not an interface.** Consumers that need to observe any distillation outcome (success or failure) can `Subscribe<DistillationSettledEvent>` on `IAsyncEventBus`; the bus dispatches polymorphically via `Type.IsAssignableFrom`. This avoids the subtle race condition of independently subscribing to `DistillationCompletedEvent` and `DistillationFailedEvent` and guarantees a waiter never hangs when a job fails (TI-8.1).
