# Agency.Memory.Consolidator
#memory #consolidation #agent #background-service

## What It Is

`Agency.Memory.Consolidator` is the maintenance-path component of the long-term memory subsystem that keeps a user's record store accurate and concise over time. It runs as an `IHostedService` background service that listens for `ConsolidationJob` messages and, for each affected user, spins up a short-lived LLM sub-agent (built on `Agency.Harness`) that inspects every record in the store and decides which ones to merge, update, or delete. The sub-agent emits a `MemoryMutatedEvent` for every successful mutation so host applications can surface autonomous memory edits to the user, and publishes a terminal `ConsolidationCompletedEvent` when the pass ends. Consolidation never runs on the agent's hot path — it is triggered only after a distillation job completes (or manually by the host).

**Namespace:** `Agency.Memory.Consolidator`

## Prerequisites

- An `IChatClient` (Microsoft.Extensions.AI) must be registered: the consolidation pass is driven by an LLM sub-agent, so a working LLM provider is required. The model is selected via `ConsolidatorOptions.Model` (falls back to `"default"`).
- An `IMemoryStore` implementation must be registered (e.g. `PostgresMemoryStore`); the tools call `MergeAsync`, `UpdateRecordAsync`, and `DeleteByIdAsync` on it.
- An `IAsyncEventBus` must be registered for trigger subscription and mutation/completion event publishing.

## API Surface

### `IConsolidationTrigger`

Public interface for hosts that configure `ConsolidationTrigger.Manual`. Resolved from the DI container; the concrete implementation is `ConsolidatorBackgroundService`.

```csharp
// File: src/Memory/Agency.Memory.Consolidator/Services/IConsolidationTrigger.cs
namespace Agency.Memory.Consolidator.Services;

public interface IConsolidationTrigger
{
    /// <summary>Enqueues a consolidation pass for the specified user.</summary>
    Task RequestAsync(string userId, CancellationToken ct = default);
}
```

### `ConsolidatorServiceCollectionExtensions`

The sole public type in this assembly. Registers `ConsolidatorBackgroundService` as both an `IHostedService` and an `IConsolidationTrigger` singleton.

```csharp
// File: src/Memory/Agency.Memory.Consolidator/DependencyInjection/ConsolidatorServiceCollectionExtensions.cs
using Agency.Memory.Common.Options;
using Microsoft.Extensions.DependencyInjection;

namespace Agency.Memory.Consolidator.DependencyInjection;

public static class ConsolidatorServiceCollectionExtensions
{
    /// <summary>
    /// Registers ConsolidatorBackgroundService and the sub-agent runner factory.
    /// Call after AddAgencyMemory() and after IMemoryStore + IChatClient are registered.
    /// </summary>
    public static IServiceCollection AddAgencyConsolidator(
        this IServiceCollection services,
        Action<ConsolidatorOptions>? configure = null);
}
```

### `ConsolidatorBackgroundService` (internal)

Core `BackgroundService` that owns the job channel, per-user in-flight coalescing, and the sub-agent execution loop. Exposed internally for test access.

| Member | Kind | Notes |
|---|---|---|
| `ActivitySourceName` | `const string` | `"Agency.Memory.Consolidator"` |
| `MeterName` | `const string` | `"Agency.Memory.Consolidator"` |
| `MaxRecordsPerPass` | `const int` | `500` — V1 scale guard; warns above this, still proceeds |
| `RequestAsync(string, CancellationToken)` | method | `IConsolidationTrigger` implementation; enqueues a job |
| `ProcessJobAsync(ConsolidationJob, CancellationToken)` | method | Loads records, runs the sub-agent, publishes completion |

### `ConsolidatorReconciliationPrompt` (internal)

Builds the system prompt passed to the sub-agent. Current prompt version is **V3** (adds same-Domain/Key merge priority rule).

```csharp
// File: src/Memory/Agency.Memory.Consolidator/Prompts/ConsolidatorReconciliationPrompt.cs
using Agency.Memory.Common.Records;

namespace Agency.Memory.Consolidator.Prompts;

internal static class ConsolidatorReconciliationPrompt
{
    internal const int Version = 3;

    internal static string Render(
        string userId,
        IReadOnlyList<Record> records,
        int maxIterations,
        double factThreshold,
        double memoryThreshold);
}
```

### `ConsolidatorSubAgentFactory` (internal)

Static factory that wires the `Agent`, tools, stop conditions, and prompt into a runner delegate injected into `ConsolidatorBackgroundService`. The optional `timeProvider` and `mergeIdFactory` parameters exist so tests can make the LLM request bodies byte-stable and replayable from the HTTP response cache; both default to production behaviour (`TimeProvider.System` and a random GUID).

```csharp
// File: src/Memory/Agency.Memory.Consolidator/Services/ConsolidatorSubAgentFactory.cs
using Agency.Harness;
using Agency.Memory.Common.Events;
using Agency.Memory.Common.Options;
using Agency.Memory.Common.Records;
using Agency.Memory.Common.Storage;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agency.Memory.Consolidator.Services;

internal static class ConsolidatorSubAgentFactory
{
    internal static Func<string, IReadOnlyList<Record>, CancellationToken, Task<(int Merges, int Updates, int Deletes)>>
        CreateRunner(
            IChatClient llm,
            string model,
            IMemoryStore store,
            IOptions<ConsolidatorOptions> options,
            IAsyncEventBus eventBus,
            ILogger<Agent>? logger = null,
            TimeProvider? timeProvider = null,
            Func<string>? mergeIdFactory = null);
}
```

## Registration

Call `AddAgencyConsolidator()` on the host's `IServiceCollection` after `AddAgencyMemory()` and after both `IMemoryStore` and `IChatClient` are registered:

```csharp
using Agency.Memory.Common.Options;
using Agency.Memory.Consolidator.DependencyInjection;

services.AddAgencyConsolidator(opts =>
{
    opts.Model         = "strong-model-id";   // defaults to "default"
    opts.MaxIterations = 20;
    opts.MaxCostUsd    = 0.50;
    opts.Trigger       = ConsolidationTrigger.OnSessionEnd; // default
});
```

When `Trigger` is `ConsolidationTrigger.Manual`, the background service does not subscribe to `DistillationCompletedEvent`. The host must resolve `IConsolidationTrigger` and call `RequestAsync` explicitly.

The extension registers:

- `ConsolidatorBackgroundService` as a **singleton** (so `IConsolidationTrigger` and `IHostedService` share the same instance).
- `IHostedService` forwarded to the same singleton.
- `IConsolidationTrigger` forwarded to the same singleton.
- `ConsolidatorOptions` via `IOptions<T>` (configured if `configure` is supplied, otherwise default-bound).

## How It Works

### Trigger

In `OnSessionEnd` mode (the default), `ConsolidatorBackgroundService` subscribes to `DistillationCompletedEvent` on startup via `IAsyncEventBus`. Each event enqueues a `ConsolidationJob { UserId, TriggeredBySessionId }` into an unbounded `System.Threading.Channels.Channel`. In `Manual` mode, the host calls `IConsolidationTrigger.RequestAsync`, which writes the same job type directly onto the channel.

### Per-User Serialization and Coalescing

The background loop reads jobs from the channel sequentially. A `ConcurrentDictionary<string, bool>` (`_inFlight`) enforces per-user serial execution: if a pass for a user is already running when a second job arrives, the second job is not re-queued — instead the pending flag for that user is set to `true`. When the in-flight pass finishes, the service checks the flag and automatically re-enqueues one more pass before clearing the entry. N simultaneous triggers for the same user therefore collapse into at most two passes: the current one and one pending re-run.

### Consolidation Pass (`ProcessJobAsync`)

1. An OpenTelemetry `Activity` is started (`memory.consolidate`) and the `memory.consolidator.jobs` counter is incremented.
2. `IMemoryStore.GetAllForUserAsync` loads a snapshot of every record for the user. If no records exist, `ConsolidationCompletedEvent(Merges:0, Updates:0, Deletes:0)` is published immediately.
3. If `records.Count > MaxRecordsPerPass` (500), a warning is logged but the pass proceeds on the full corpus. Per-domain batching is deferred to V2.
4. `ConsolidatorSubAgentFactory.CreateRunner` produces the agent runner delegate, which the service calls with `(userId, records, ct)`.

### Sub-Agent Execution

Inside the runner delegate:

1. Four tool instances are created — `MemoryMergeTool`, `MemoryUpdateTool`, `MemoryDeleteTool`, `MemoryDoneTool` — each scoped to the current `userId`, and registered on a `ToolRegistry`.
2. A `StopCondition` is composed as `Any(StepCountIs(MaxIterations), BudgetExceeded(MaxCostUsd), doneFlag)`.
3. `ConsolidatorReconciliationPrompt.Render` builds the system prompt containing all records in Markdown (id, ContentType, Domain/Key, Tags, Importance, human-readable age, and a 200-character value preview).
4. The `Agent` streams `AgentEvent`s via `ChatSession.SendAsync`. For each `ToolInvokedEvent` where `Result.IsError` is `false` and the tool name maps to `Merge`, `Update`, or `Delete`, the runner increments the corresponding tally and publishes a `MemoryMutatedEvent` on the bus.
5. When `Memory_Done` is called, the `done` flag flips and the stop condition fires, ending the stream.
6. The runner returns `(merges, updates, deletes)`.

```csharp
using Agency.Memory.Common.Options;
using Agency.Memory.Consolidator.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

var runner = ConsolidatorSubAgentFactory.CreateRunner(
    llm: chatClient,
    model: "strong-model-id",
    store: memoryStore,
    options: Options.Create(new ConsolidatorOptions { MaxIterations = 20, MaxCostUsd = 0.50 }),
    eventBus: eventBus);

(int merges, int updates, int deletes) =
    await runner(userId, records, CancellationToken.None);
```

### Events Published

| Event | When |
|---|---|
| `MemoryMutatedEvent(userId, operation, detail)` | After each successful `Memory_Merge`, `Memory_Update`, or `Memory_Delete` tool call |
| `ConsolidationCompletedEvent(userId, merges, updates, deletes)` | After the sub-agent terminates (including zero-record and no-runner early exits) |

## Agent Tools

The sub-agent's `ToolRegistry` holds four `ITool` implementations, registered exclusively on the sub-agent and never exposed to the primary user-facing agent. The tool `Name` strings (with the `Memory_` prefix) are exactly what is sent to the LLM.

| Tool Name (exact string) | Description shown to LLM | Key Parameters |
|---|---|---|
| `Memory_Merge` | Atomically deletes the listed records and inserts a new combined record that merges their content. | `recordIds: string[]` (required), `newRecord: object` (required) with `contentType` (`Fact`/`Memory`), `domain`, `key`, `title`, `value`, `tags`, `importance` (0–1) required, optional `scope` |
| `Memory_Update` | Updates the Value and/or Importance of an existing record. Only supplied parameters are changed. | `recordId: string` (required), `newValue?: string`, `newImportance?: number` (0–1) — at least one of `newValue`/`newImportance` must be supplied |
| `Memory_Delete` | Hard-deletes a single record by its ID. Irreversible. | `recordId: string` (required) |
| `Memory_Done` | Signal that you are finished consolidating. ALWAYS call this last. The consolidation pass ends as soon as you call this tool. | (none) |

- `Memory_Merge` calls `IMemoryStore.MergeAsync`, which executes DELETE + INSERT in a single PostgreSQL transaction. The merged record's id comes from an injected `idFactory` (random GUID in production).
- `Memory_Update` calls `IMemoryStore.UpdateRecordAsync`; `UpdatedAt` is refreshed and `LastWrittenAt` bumped.
- `Memory_Delete` calls `IMemoryStore.DeleteByIdAsync`, constrained to the owning `userId`.
- `Memory_Done` invokes an `onDone` callback that sets the captured `done` flag the `StopCondition` reads.

## Observability

| Signal | Name |
|---|---|
| `ActivitySource` | `Agency.Memory.Consolidator` |
| `Meter` | `Agency.Memory.Consolidator` |
| Counter | `memory.consolidator.jobs` — total consolidation jobs processed |
| Counter | `memory.consolidator.errors` — consolidation failures |
| Activity | `memory.consolidate` (tags: `memory.user_id`, `memory.session_id`) |

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.Memory.Common]] | Provides `IMemoryStore`, `Record`, `ContentType`, `ConsolidationJob`, `ConsolidatorOptions`, `ConsolidationTrigger`, `IAsyncEventBus`, `DistillationCompletedEvent`, `ConsolidationCompletedEvent`, `MemoryMutatedEvent` |
| [[Agency.Harness]] | Supplies the `Agent`, `ChatSession`, `AgentOptions`, `ToolContext`, `ToolRegistry`, `StopConditions`, and `AgentEvent` / `ToolInvokedEvent` types the sub-agent is built from |
| [[Agency.Llm.Common]] | Defines `ITool`, `ToolDefinition`, and `ToolResult` that the four consolidation tools implement |
| [[Agency.Memory.Distiller]] | Publishes `DistillationCompletedEvent`, the default upstream trigger for a consolidation pass |
| [[Agency.Memory.Sql.Postgres]] | Ships the `PostgresMemoryStore` implementation of `IMemoryStore`; `MergeAsync` and `DeleteByIdAsync` execute as single PostgreSQL transactions |
| [[Agency.Mcp.Memory]] | Exposes agent tools that drive distillation; distillation completion cascades into consolidation |

## Design Notes

- **The consolidator is itself an `Agency.Harness` agent.** It reuses the same `Agent`, `ToolRegistry`, `StopConditions`, and `ChatSession` primitives as any user-facing agent, with a different tool set and system prompt. No bespoke agentic loop is needed; the harness's iteration model *is* the consolidation loop. An LLM sub-agent is used instead of a heuristic merge because reconciling overlapping records requires semantic judgment (which records describe the same fact, what content to preserve) that a similarity threshold alone cannot make — the embedding-similarity numbers are passed only as informational hints.

- **Per-user serial execution is enforced at the channel level, not the database level.** The `ConcurrentDictionary`-based in-flight flag ensures at most one pass runs per user at any instant and that concurrent triggers coalesce into a single pending re-run. This avoids read-modify-write races where two simultaneous passes could each read the same snapshot and produce conflicting mutations. Consolidation is per-user because the record store is partitioned by user and an LLM reasoning over one user's full corpus must not see another's. A partial run (crash mid-pass) is safe because each tool call is its own store transaction; the next session-end re-triggers the pass with a fresh snapshot.

- **`MemoryMutatedEvent` is a transparency feature, not a side-channel.** Autonomous memory edits are treated as user-visible events: the user never typed the merge, update, or delete, so the host should surface it (e.g. "Your memory was reorganised: merged 2 duplicate Preferences records"). This was a deliberate product decision (TI-8.3), not an internal metric.

- **Consolidation is triggered by mutations, never on the hot path.** It runs only after a distillation completes (or on manual request), so the latency-sensitive user-facing turn is never blocked by a full-corpus reconciliation pass.

- **V1 loads the full corpus.** There is no per-domain batching or token-budget truncation in the current prompt. A warning is logged when `records.Count > 500`, with per-domain batching noted as a V2 upgrade. The optional `timeProvider`/`mergeIdFactory` factory parameters exist purely to make multi-turn LLM request bodies deterministic and replayable from the HTTP cache in functional tests; production uses the system clock and random GUIDs.
