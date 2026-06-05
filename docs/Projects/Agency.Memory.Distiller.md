# Agency.Memory.Distiller
#memory #distillation #async

## What It Is

`Agency.Memory.Distiller` is the write-path component of the Agency long-term memory system. It converts conversation turns into durable [[Agency.Memory.Common]] `Record` objects entirely off the agent's hot path. After every distillation trigger fires — a goal-completion signal, an inactivity timeout, or session disposal — a `DistillationJob` is placed on a bounded per-session channel. A single `DistillerBackgroundService` drains those channels, reads the unprocessed turns from the session's `IConversationManager`, calls an LLM to extract zero or more Fact or Memory records, embeds each record, and upserts the results into [[Agency.Memory.Common]]'s `IMemoryStore`. The agent's hot path sees none of this work; the only hot-path side effect is that `OnAssistantTurn` restarts an inactivity timer.

**Namespace:** `Agency.Memory.Distiller`

---

## API Surface

### Public registration extension — DI entry points

```csharp
// File: src/Memory/Agency.Memory.Distiller/DependencyInjection/MemoryServiceCollectionExtensions.cs
using Agency.Memory.Distiller.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

public static class MemoryServiceCollectionExtensions
{
    // Registers: ChannelSessionRegistry, IConversationManagerRegistry,
    // InactivityTimerService (IHostedService), DistillerBackgroundService (IHostedService),
    // IAsyncEventBus, and baseline AgentHooks via IPostConfigureOptions<AgentOptions>.
    public static IServiceCollection AddAgencyMemory(
        this IServiceCollection services,
        Action<MemoryOptions>? configureMemory = null,
        Action<DistillerOptions>? configureDistiller = null);
}
```

```csharp
// File: src/Memory/Agency.Memory.Distiller/DistillerLlmServiceCollectionExtensions.cs
using Agency.Memory.Distiller;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

public static class DistillerLlmServiceCollectionExtensions
{
    // Wraps an IChatClient as the distiller's LLM adapter.
    // Must be called in addition to AddAgencyMemory — the distiller
    // has no default LLM binding.
    public static IServiceCollection AddAgencyDistillerLlm(
        this IServiceCollection services,
        IChatClient client,
        string model);
}
```

### Internal services (accessible to test projects via `InternalsVisibleTo`)

```csharp
// File: src/Memory/Agency.Memory.Distiller/Services/DistillerBackgroundService.cs
using Agency.Memory.Distiller.Services;

// BackgroundService. Single consumer; drains all per-session channels then
// suspends on ChannelSessionRegistry.WaitForWorkAsync (SemaphoreSlim wake).
// ActivitySource name : "Agency.Memory.Distiller"
// Meter name          : "Agency.Memory.Distiller"
// Counters            : memory.distiller.jobs, memory.distiller.errors
// Histogram           : memory.distiller.duration (ms)
internal sealed class DistillerBackgroundService : BackgroundService
{
    internal DistillerBackgroundService(
        ChannelSessionRegistry channelRegistry,
        IConversationManagerRegistry conversationRegistry,
        ILlmClientAdapter llm,
        IEmbeddingGenerator embedder,
        IMemoryStore store,
        IWatermarkStore watermarks,
        IDeadLetterStore deadLetter,
        IAsyncEventBus eventBus,
        IOptions<DistillerOptions> options,
        TimeProvider timeProvider,
        ILogger<DistillerBackgroundService> logger);

    // Drains all queued jobs up to timeout; dead-letters any still pending.
    internal Task DrainAsync(TimeSpan timeout);
}
```

```csharp
// File: src/Memory/Agency.Memory.Distiller/Services/ChannelSessionRegistry.cs
using Agency.Memory.Distiller.Services;
using System.Threading.Channels;

// Manages one bounded Channel<DistillationJob> per session.
// Capacity: DistillerOptions.PerSessionQueueCapacity (default 32), DropOldest.
// NotifyingChannelWriter releases SemaphoreSlim on every successful write.
internal sealed class ChannelSessionRegistry
{
    internal Channel<DistillationJob> GetOrCreate(string userId, string sessionId);
    internal ChannelWriter<DistillationJob> GetOrCreateWriter(string userId, string sessionId);
    internal IReadOnlyDictionary<string, Channel<DistillationJob>> GetAll();
    internal Task WaitForWorkAsync(CancellationToken cancellationToken);
    internal void Remove(string sessionId);
}
```

```csharp
// File: src/Memory/Agency.Memory.Distiller/Services/InactivityTimerService.cs
using Agency.Memory.Distiller.Services;

// IHostedService + IDisposable. Holds a ConcurrentDictionary<sessionId, SessionTimerState>.
// Each Restart() call disposes the prior ITimer and starts a new one via TimeProvider.
// Timer expiry enqueues DistillationJob(Trigger=Inactivity) to ChannelSessionRegistry.
internal sealed class InactivityTimerService : IHostedService, IDisposable
{
    internal void Restart(string userId, string sessionId, int currentTurnIndex, FocusContext? focus = null);
    internal void Stop(string sessionId);
}
```

```csharp
// File: src/Memory/Agency.Memory.Distiller/Services/IConversationManagerRegistry.cs
using Agency.Memory.Distiller.Services;

internal interface IConversationManagerRegistry
{
    IConversationManager? Get(string sessionId);
    void Register(string sessionId, IConversationManager manager);
    void Unregister(string sessionId);
}
```

```csharp
// File: src/Memory/Agency.Memory.Distiller/Services/ILlmClientAdapter.cs
using Agency.Memory.Distiller.Services;

// Thin wrapper over a single-turn LLM call; makes DistillerBackgroundService
// unit-testable via stub implementations.
internal interface ILlmClientAdapter
{
    Task<string> SendAsync(string prompt, CancellationToken ct = default);
}
```

```csharp
// File: src/Memory/Agency.Memory.Distiller/Services/IWatermarkStore.cs
using Agency.Memory.Distiller.Services;

// Narrow interface over the watermarks table; backed by WatermarkStoreAdapter
// -> WatermarkRepository (Agency.Memory.Sql.Postgres).
internal interface IWatermarkStore
{
    Task<int> GetAsync(string userId, string sessionId, CancellationToken ct = default);
    // Monotone: only advances forward (MAX semantics).
    Task<int> AdvanceAsync(string userId, string sessionId, int candidate, CancellationToken ct = default);
}
```

```csharp
// File: src/Memory/Agency.Memory.Distiller/Services/IDeadLetterStore.cs
using Agency.Memory.Distiller.Services;

// Writes failed jobs to the dead_letter table for operational inspection.
// Never read by the live system.
internal interface IDeadLetterStore
{
    Task WriteAsync(
        string userId,
        string? sessionId,
        string jobKind,
        object payload,
        Exception error,
        CancellationToken ct = default);
}
```

### Agent tools (registered per session via MemorySessionTools)

```csharp
// File: src/Memory/Agency.Memory.Distiller/Tools/MarkGoalCompleteTool.cs
using Agency.Memory.Distiller.Tools;

// ITool. Enqueues DistillationJob(Trigger=GoalCompletion) to the session channel.
// Does NOT stop the agent loop. Watermark prevents reprocessing.
// Tool name: "MarkGoalComplete"
// Parameter: summary (string, optional)
internal sealed class MarkGoalCompleteTool : ITool { }
```

```csharp
// File: src/Memory/Agency.Memory.Distiller/Tools/SetFocusTool.cs
using Agency.Memory.Distiller.Tools;

// ITool. Updates Context.Focus to bias retrieval. Implements ITool.GetDefinitionAsync
// to dynamically list known domain values in the description.
// Idempotent: setting the same values twice is a no-op.
// Tool name: "SetFocus"
// Parameters: title (string?), domain (string?), tags (string[]?)
internal sealed class SetFocusTool : ITool { }
```

### Prompt and parse layer

```csharp
// File: src/Memory/Agency.Memory.Distiller/Prompts/EpisodeExtractionPrompt.cs
using Agency.Memory.Distiller.Prompts;

// Static renderer for the episode-extraction system prompt (template v2).
// Includes /no_think directive to suppress chain-of-thought (TI-8.2).
// Returns a single prompt string combining system instructions, context,
// and the conversation excerpt.
internal static class EpisodeExtractionPrompt
{
    internal const int Version = 2;

    internal static string Render(
        DistillationJob job,
        IReadOnlyList<ChatMessage> turns,
        FocusContext focus,
        IReadOnlyList<string> knownDomains,
        IReadOnlyList<Record> recentFacts);
}
```

```csharp
// File: src/Memory/Agency.Memory.Distiller/Prompts/EpisodeExtractionParser.cs
using Agency.Memory.Distiller.Prompts;

// Parses the LLM JSON response into Record instances.
// Tolerates ```json / ``` code fences. Unknown fields are ignored.
// Throws ExtractionParseException on structural failures (permanent error class).
internal static class EpisodeExtractionParser
{
    internal static IReadOnlyList<Record> Parse(
        string llmResponse,
        string userId,
        string? sessionId);
}
```

```csharp
// File: src/Memory/Agency.Memory.Distiller/Prompts/ExtractionParseException.cs
using Agency.Memory.Distiller.Prompts;

// Distinguishes permanent parse failures from transient HTTP errors in the retry loop.
// One parse retry is allowed (stricter re-prompt); second failure → dead-letter.
internal sealed class ExtractionParseException : Exception { }
```

---

## Registration

Call both extension methods during host startup:

```csharp
using Agency.Memory.Distiller;
using Agency.Memory.Distiller.DependencyInjection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Prerequisites that must already be registered:
//   IMemoryStore          (Agency.Memory.Sql.Postgres or custom)
//   IEmbeddingGenerator   (Agency.Embeddings.Common)
//   WatermarkRepository   (Agency.Memory.Sql.Postgres)
//   DeadLetterRepository  (Agency.Memory.Sql.Postgres)
//   IAsyncEventBus        (registered by AddAgencyMemory itself)

services.AddAgencyMemory(
    configureMemory: opts =>
    {
        opts.RetrievalTopK = 10;
        opts.OverFetchFactor = 3;
    },
    configureDistiller: opts =>
    {
        opts.InactivityTimeout = TimeSpan.FromMinutes(5);
        opts.MaxRetries = 3;
        opts.RetryBaseDelay = TimeSpan.FromSeconds(2);
        opts.PerSessionQueueCapacity = 32;
        opts.ShutdownDrainTimeout = TimeSpan.FromSeconds(30);
    });

// Provide the LLM the distiller uses for episode extraction.
IChatClient chatClient = /* resolve from host */;
services.AddAgencyDistillerLlm(chatClient, model: "your-model-id");
```

`AddAgencyMemory` sets `AgentOptions.BaselineHooks` via `IPostConfigureOptions<AgentOptions>`, so the retrieval callback, timer-restart, session registration, and session-end distillation job are all wired without any additional configuration. `AgentFactory` composes `BaselineHooks` first, followed by any `UserHooks`.

---

## How It Works

### Trigger conditions

Three events enqueue a `DistillationJob` into the session's bounded channel:

| Trigger | Source | `DistillationTrigger` value |
|---|---|---|
| Agent calls `MarkGoalComplete` tool | `MarkGoalCompleteTool.InvokeAsync` | `GoalCompletion` |
| Session idle beyond `InactivityTimeout` | `InactivityTimerService.OnTimerExpired` | `Inactivity` |
| `ChatSession` is disposed | `SessionEndHook` via `OnSessionEnded` baseline hook | `SessionDisposed` |

`OnAssistantTurn` only restarts the inactivity timer — it never enqueues a job directly (hot-path discipline, spec P1).

### Channel model

`ChannelSessionRegistry` maintains one `BoundedChannel<DistillationJob>` per session (`DropOldest`, capacity 32). A `NotifyingChannelWriter` wrapper releases a `SemaphoreSlim` on every successful write. `DistillerBackgroundService` sweeps all session channels with `TryRead` in a tight loop; when every channel is empty it suspends on `WaitForWorkAsync`, which waits on that semaphore. This event-driven wake means a newly-enqueued job is picked up in the next scheduler tick with no polling delay.

### Processing a job

1. **Watermark guard** — reads the stored `LastDistilledTurnIndex` for the session. If `job.UpToTurnIndex <= watermark`, the job is a no-op and a `DistillationCompletedEvent(RecordsWritten=0)` is published.
2. **Turn slice** — retrieves `conversation.Messages.Skip(watermark).Take(upTo - watermark)`. If the slice is empty, skips the LLM call.
3. **Context assembly** — loads known domain labels and the 10 most-recent Fact records for the user to provide deduplication context.
4. **LLM call** — renders the episode-extraction prompt via `EpisodeExtractionPrompt.Render` (template v2, includes `/no_think`) and sends it via `ILlmClientAdapter.SendAsync` with `MaxOutputTokens = 2048`.
5. **Parse** — `EpisodeExtractionParser.Parse` deserializes the JSON response into `Record` objects. Code fences are stripped. One parse retry is allowed; a second failure dead-letters the job.
6. **Embed and upsert** — for each record, embeds `Title + "\n\n" + Value` via `IEmbeddingGenerator`, then calls `IMemoryStore.UpsertAsync`. Upsert key is `(UserId, SessionId, Domain, Key)`; a matching existing record is overwritten.
7. **Watermark advance** — calls `IWatermarkStore.AdvanceAsync` (monotone MAX). Emits `DistillationCompletedEvent`.
8. **Session cleanup** — if `Trigger == SessionDisposed`, unregisters the conversation manager and removes the session channel after the successful write.

### Retry and error handling

| Error class | Detection | Action |
|---|---|---|
| Transient | HTTP 429 / 503; connection-level `HttpRequestException` with `StatusCode == null`; `TaskCanceledException` wrapping `TimeoutException`; `NpgsqlException.IsTransient` | Exponential backoff, up to `MaxRetries`; dead-letter on exhaustion |
| Parse failure | `ExtractionParseException` | One retry (implicit re-prompt); second failure → dead-letter |
| Permanent | HTTP 4xx (not 429); any other unrecognised exception | Dead-letter immediately |
| Cancellation | `OperationCanceledException` | Propagate; never dead-letter |

Dead-letter writes go to `IDeadLetterStore` (backed by the `dead_letter` Postgres table) and are followed by a `DistillationFailedEvent`. Both `DistillationCompletedEvent` and `DistillationFailedEvent` derive from the abstract `DistillationSettledEvent`, so a consumer can subscribe to the base type to observe job settlement on either outcome.

### Session lifecycle hooks

`MemoryServiceCollectionExtensions.AddAgencyMemory` registers four baseline hook callbacks:

| Hook | Callback | Effect |
|---|---|---|
| `OnPreIteration` | `RetrievalGate.ShouldRetrieveAsync` → `RetrievalEngine.RetrieveAsync` | Injects `Context.Knowledge` / `Context.Memory` when store has changed since last retrieval |
| `OnAssistantTurn` | `InactivityTimerService.Restart` | Resets the per-session inactivity timer; no other side effect |
| `OnSessionStarted` | `ConversationRegistrationHook` + `MemorySessionTools.RegisterInto` | Registers `IConversationManager` in registry; adds `MarkGoalComplete` and `SetFocus` tools to `Context.Tools` |
| `OnSessionEnded` | `SessionEndHook` | Enqueues `DistillationJob(Trigger=SessionDisposed)` |

### Shutdown drain

On host shutdown, `ExecuteAsync` exits its main loop when the `stoppingToken` is cancelled, then calls `DrainAsync(ShutdownDrainTimeout)` (default 30 s). Jobs still in channels after the deadline are dead-lettered for watermark-based recovery on the next process start.

---

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.Memory.Common]] | Consumes `IMemoryStore`, `Record`, `DistillationJob`, `DistillerOptions`, `IAsyncEventBus`, `MemoryHookFactory` |
| [[Agency.Memory.Retrieval]] | `AddAgencyMemory` builds a `RetrievalEngine` from this project and binds it to the `OnPreIteration` baseline hook |
| [[Agency.Memory.Sql.Postgres]] | `WatermarkStoreAdapter` and `DeadLetterStoreAdapter` delegate to `WatermarkRepository` and `DeadLetterRepository` from this project |
| [[Agency.Harness]] | Consumes `BackgroundService`, `IConversationManager`, `Context`, `FocusContext`, `AgentOptions.BaselineHooks`, hook context types |
| [[Agency.Embeddings.Common]] | `IEmbeddingGenerator` used to embed `Title + "\n\n" + Value` before upsert |
| [[Agency.Memory.Distiller.Test]] | Unit test project; accesses internals via `InternalsVisibleTo` |
| [[Agency.Memory.Functional.Test]] | Functional test project; accesses internals to wire stub and real LLM clients |

---

## Design Notes

- **The agent never authors a memory.** `MarkGoalComplete` and `SetFocus` are the only memory-adjacent tools the primary agent sees. `MarkGoalComplete` enqueues a distillation trigger; it does not write a record. The actual extraction decision — what is worth remembering, in which domain, at what importance — is made by the distiller's LLM call after the fact. This keeps the hot-path agent focused on its task and produces more consistent, higher-quality memories than agent-authored writes (spec P2, §14.1).

- **Per-session channels with a single global consumer.** The spec describes a single MPSC channel; the implementation intentionally deviates to use one bounded channel per session so `DropOldest` backpressure applies at the session level rather than globally. The `NotifyingChannelWriter` + `SemaphoreSlim` pattern preserves the event-driven wake of the single-consumer design without introducing per-session background tasks or polling loops (spec §10.2 deviation note).

- **Thinking suppression is enforced at two layers.** `ILlmClientAdapter` implementations may set SDK-level thinking suppression (`LlmClientOptions.SuppressThinking`), and the extraction prompt template always opens with a `/no_think` directive. Episode extraction is deterministic JSON authoring that gains nothing from chain-of-thought; suppressing it eliminates unnecessary token latency on a cold-path LLM call (TI-8.2, spec §6.2 implementation notes).

- **Watermark idempotency makes crash recovery free.** The watermark is advanced only after a successful upsert. A process crash mid-distillation leaves the watermark un-advanced; the next run re-reads the same turn slice and re-distills it. Duplicate upserts resolve via the `(UserId, SessionId, Domain, Key)` upsert key, so re-running produces the same stored state.

- **`SessionDisposed` cleanup is deferred until after the write.** When the trigger is `SessionDisposed`, the conversation manager is unregistered and the session channel is removed only after `IMemoryStore.UpsertAsync` succeeds for all extracted records. Early removal would lose the turn data the distiller still needs to read (spec §A3).
