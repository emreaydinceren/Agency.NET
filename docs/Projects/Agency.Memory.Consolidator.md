# Agency.Memory.Consolidator
#memory #consolidation

## What It Is

`Agency.Memory.Consolidator` is the maintenance-path component of the long-term memory subsystem responsible for keeping a user's record store accurate and concise over time. It runs as an `IHostedService` background service that listens for `ConsolidationJob` messages and, for each affected user, spins up a short-lived LLM sub-agent (built on `Agency.Harness`) that inspects every record in the store and decides which ones to merge, update, or delete. The sub-agent emits a `MemoryMutatedEvent` for every successful mutation so that host applications can surface autonomous memory edits to the user, and publishes a terminal `ConsolidationCompletedEvent` when the pass ends. Consolidation never runs on the agent's hot path — it is triggered only after a distillation job completes (or manually by the host).

**Namespace:** `Agency.Memory.Consolidator`

## API Surface

### `IConsolidationTrigger`

Public interface for hosts that configure `ConsolidationTrigger.Manual`. Resolved from the DI container; the concrete implementation is `ConsolidatorBackgroundService`.

```csharp
// File: src/Memory/Agency.Memory.Consolidator/Services/IConsolidationTrigger.cs
using Agency.Memory.Consolidator.Services;

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
using Agency.Memory.Consolidator.DependencyInjection;
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

```csharp
// File: src/Memory/Agency.Memory.Consolidator/Services/ConsolidatorBackgroundService.cs
using Agency.Memory.Common.Storage;
using Agency.Memory.Common.Records;
using Microsoft.Extensions.Hosting;

namespace Agency.Memory.Consolidator.Services;

internal sealed class ConsolidatorBackgroundService : BackgroundService, IConsolidationTrigger
{
    internal const string ActivitySourceName = "Agency.Memory.Consolidator";
    internal const string MeterName          = "Agency.Memory.Consolidator";
    internal const int    MaxRecordsPerPass  = 500;

    // Counters: memory.consolidator.jobs, memory.consolidator.errors
}
```

### Consolidator Tools (internal)

Four `ITool` implementations registered exclusively on the sub-agent's `ToolRegistry`. None of these are available to the primary user-facing agent.

```csharp
// File: src/Memory/Agency.Memory.Consolidator/Tools/MemoryMergeTool.cs
// File: src/Memory/Agency.Memory.Consolidator/Tools/MemoryUpdateTool.cs
// File: src/Memory/Agency.Memory.Consolidator/Tools/MemoryDeleteTool.cs
// File: src/Memory/Agency.Memory.Consolidator/Tools/MemoryDoneTool.cs
using Agency.Llm.Common.Tools;
using Agency.Memory.Common.Storage;

namespace Agency.Memory.Consolidator.Tools;

// Memory_Merge — atomically deletes recordIds[] and inserts a new combined Record.
//   Calls IMemoryStore.MergeAsync (single PostgreSQL transaction).
internal sealed class MemoryMergeTool : ITool { }

// Memory_Update — updates Value and/or Importance of a single Record in place.
//   Calls IMemoryStore.UpdateRecordAsync; only non-null parameters are applied.
internal sealed class MemoryUpdateTool : ITool { }

// Memory_Delete — hard-deletes a single Record by its surrogate id.
//   Calls IMemoryStore.DeleteByIdAsync; bumps LastWrittenAt.
internal sealed class MemoryDeleteTool : ITool { }

// Memory_Done — signals the consolidation pass is complete.
//   Sets a captured bool flag that the StopCondition delegate reads.
internal sealed class MemoryDoneTool : ITool { }
```

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

Static factory that wires the `Agent`, tools, stop conditions, and prompt into a `Func<string, IReadOnlyList<Record>, CancellationToken, Task<(int Merges, int Updates, int Deletes)>>` delegate injected into `ConsolidatorBackgroundService`.

```csharp
// File: src/Memory/Agency.Memory.Consolidator/Services/ConsolidatorSubAgentFactory.cs
using Agency.Harness;
using Agency.Memory.Common.Storage;
using Microsoft.Extensions.AI;
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
            ILogger<Agent>? logger = null);
}
```

## Registration

Call `AddAgencyConsolidator()` on the host's `IServiceCollection` after `AddAgencyMemory()` and after both `IMemoryStore` and `IChatClient` are registered:

```csharp
using Agency.Memory.Consolidator.DependencyInjection;

services.AddAgencyConsolidator(opts =>
{
    opts.Model        = "strong-model-id";   // defaults to "default"
    opts.MaxIterations = 20;
    opts.MaxCostUsd    = 0.50;
    opts.Trigger       = ConsolidationTrigger.OnSessionEnd; // default
});
```

When `Trigger` is `ConsolidationTrigger.Manual`, the background service does not subscribe to `DistillationCompletedEvent`. The host must resolve `IConsolidationTrigger` and call `RequestAsync` explicitly.

The extension registers:

- `ConsolidatorBackgroundService` as a **singleton** (so `IConsolidationTrigger` and `IHostedService` share the same instance).
- `IConsolidationTrigger` forwarded to the same singleton.
- `ConsolidatorOptions` via `IOptions<T>`.

## How It Works

### Trigger

In `OnSessionEnd` mode (the default), `ConsolidatorBackgroundService` subscribes to `DistillationCompletedEvent` on startup via `IAsyncEventBus`. Each event enqueues a `ConsolidationJob { UserId, TriggeredBySessionId }` into an unbounded `System.Threading.Channels.Channel`. In `Manual` mode, the host calls `IConsolidationTrigger.RequestAsync`, which writes the same job type directly onto the channel.

### Per-User Serialization and Coalescing

The background loop reads jobs from the channel sequentially. A `ConcurrentDictionary<string, bool>` (`_inFlight`) enforces per-user serial execution: if a pass for a user is already running when a second job arrives, the second job is not re-queued — instead the pending flag for that user is set to `true`. When the in-flight pass finishes, the service checks the flag and automatically re-enqueues one more pass before clearing the entry. This means N simultaneous triggers for the same user collapse into at most two passes: the current one and one pending re-run.

### Consolidation Pass (`ProcessJobAsync`)

1. An OpenTelemetry `Activity` is started (`memory.consolidate`) and the `memory.consolidator.jobs` counter is incremented.
2. `IMemoryStore.GetAllForUserAsync` loads a snapshot of every record for the user. If no records exist, `ConsolidationCompletedEvent(Merges:0, Updates:0, Deletes:0)` is published immediately.
3. If `records.Count > MaxRecordsPerPass` (500), a warning is logged but the pass proceeds on the full corpus. Per-domain batching is deferred to V2.
4. `ConsolidatorSubAgentFactory.CreateRunner` produces the agent runner delegate, which the service calls with `(userId, records, ct)`.

### Sub-Agent Execution

Inside the runner delegate:

1. Four tool instances are created — `MemoryMergeTool`, `MemoryUpdateTool`, `MemoryDeleteTool`, `MemoryDoneTool` — each scoped to the current `userId`.
2. A `StopCondition` is composed as `Any(StepCountIs(MaxIterations), BudgetExceeded(MaxCostUsd), doneFlag)`.
3. `ConsolidatorReconciliationPrompt.Render` builds the system prompt containing all records rendered in Markdown (id, ContentType, Domain/Key, Tags, Importance, human-readable age, and a 200-character value preview).
4. The `Agent` streams `AgentEvent`s via `ChatSession.SendAsync`. For each `ToolInvokedEvent` where `Result.IsError` is `false` and the tool name maps to `Merge`, `Update`, or `Delete`, the runner increments the corresponding tally and publishes a `MemoryMutatedEvent` on the bus.
5. When `Memory_Done` is called, the `done` flag flips and the stop condition fires, ending the stream.
6. The runner returns `(merges, updates, deletes)`.

### LLM Decision Rules (Prompt V3)

The reconciliation prompt instructs the sub-agent to categorize each cluster of related records as:

- **MERGE** — two or more records overlap in content or share the same Domain + Key. Produce one comprehensive record preserving the highest importance.
- **UPDATE (contradiction)** — a newer record contradicts an older one; overwrite with the newer content.
- **UPDATE (expansion)** — a newer record adds detail to an older sparse one; expand in place.
- **DELETE** — a record is trivial, contradicted-and-superseded, or self-described as obsolete with Importance < 0.1 and Age > 30 days (structural rule, not a judgement call).
- **SKIP** — the record is fine as-is.

The agent is instructed to be conservative: false merges and false deletes lose information. Similarity threshold hints for `Fact` (0.85) and `Memory` (0.75) are provided as informational hints, not hard rules.

### Events Published

| Event | When |
|---|---|
| `MemoryMutatedEvent(userId, operation, detail)` | After each successful `Memory_Merge`, `Memory_Update`, or `Memory_Delete` tool call |
| `ConsolidationCompletedEvent(userId, merges, updates, deletes)` | After the sub-agent terminates (including zero-record and no-runner early exits) |

### Observability

| Signal | Name |
|---|---|
| `ActivitySource` | `Agency.Memory.Consolidator` |
| `Meter` | `Agency.Memory.Consolidator` |
| Counter | `memory.consolidator.jobs` |
| Counter | `memory.consolidator.errors` |
| Activity | `memory.consolidate` (tags: `memory.user_id`, `memory.session_id`) |

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.Memory.Common]] | Provides `IMemoryStore`, `Record`, `ContentType`, `ConsolidationJob`, `ConsolidatorOptions`, `ConsolidationTrigger`, `IAsyncEventBus`, `DistillationCompletedEvent`, `ConsolidationCompletedEvent`, `MemoryMutatedEvent` |
| [[Agency.Harness]] | Supplies the `Agent`, `ChatSession`, `AgentOptions`, `ToolContext`, `StopConditions`, and `AgentEvent` / `ToolInvokedEvent` types the sub-agent is built from |
| [[Agency.Llm.Common]] | Defines `ITool`, `ToolDefinition`, `ToolResult`, and `ToolRegistry` that the four consolidation tools implement and register against |
| [[Agency.Memory.Distiller]] | Publishes `DistillationCompletedEvent` which is the default upstream trigger for a consolidation pass |
| [[Agency.Memory.Sql.Postgres]] | Ships the `PostgresMemoryStore` implementation of `IMemoryStore`; `MergeAsync` and `DeleteByIdAsync` execute as single PostgreSQL transactions |
| [[Agency.Mcp.Memory]] | Exposes `MarkGoalComplete` and `SetFocus` agent tools that drive distillation; distillation completion cascades into consolidation |

## Design Notes

- **The consolidator is itself an `Agency.Harness` agent.** It reuses the same `Agent`, `ToolRegistry`, `StopConditions`, and `ChatSession` primitives as any user-facing agent, with a different tool set and system prompt. No bespoke agentic loop is needed; the harness's iteration model is the consolidation loop.

- **Per-user serial execution is enforced at the channel level, not the database level.** The `ConcurrentDictionary`-based in-flight flag ensures at most one pass runs per user at any instant and that concurrent triggers coalesce into a single pending re-run. This avoids read-modify-write races where two simultaneous passes could each read the same snapshot and produce conflicting mutations. A partial run (crash mid-pass) is safe because each tool call is its own store transaction; the next session-end re-triggers the pass with a fresh snapshot.

- **`MemoryMutatedEvent` is a transparency feature, not a side-channel.** The spec treats autonomous memory edits as user-visible events: the user never typed the merge, update, or delete, so the host should surface it. Hosts subscribe to `MemoryMutatedEvent` to display something like "Your memory was reorganised: merged 2 duplicate Preferences records." This was a deliberate product decision (TI-8.3), not an internal metric.

- **V1 loads the full corpus.** There is no per-domain batching or token-budget truncation in the current prompt. A warning is logged when `records.Count > 500`, and the spec notes per-domain batching as a V2 upgrade. The similarity threshold hints in the prompt (0.85 for Facts, 0.75 for Memories) are informational only; the LLM applies its own judgment.
