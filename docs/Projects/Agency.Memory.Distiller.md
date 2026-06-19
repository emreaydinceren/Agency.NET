# Agency.Memory.Distiller
#memory #distillation #async #write-path

## What It Is

`Agency.Memory.Distiller` is the write-path component of the Agency long-term memory system. It converts conversation turns into durable [[Agency.Memory.Common]] `Record` objects entirely off the agent's hot path. After every distillation trigger fires — a goal-completion signal, an inactivity timeout, or session disposal — a `DistillationJob` is placed on a bounded per-session channel. A single `DistillerBackgroundService` drains those channels, reads the unprocessed turns from the session's `IConversationManager`, calls an LLM to extract zero or more Fact or Memory records, embeds each record, and upserts the results into [[Agency.Memory.Common]]'s `IMemoryStore`. The agent's hot path sees none of this work; the only hot-path side effect is that `OnAssistantTurn` restarts an inactivity timer.

**Namespace:** `Agency.Memory.Distiller`

## Prerequisites

- An **LLM** is required for episode extraction. The distiller has no default LLM binding — the host must call `AddAgencyDistillerLlm` with an `Microsoft.Extensions.AI.IChatClient` in addition to `AddAgencyMemory`.
- A storage backend supplying [[Agency.Memory.Common]]'s `IMemoryStore`, `IWatermarkStore`, and `IDeadLetterStore` (e.g. a Postgres + pgvector provider, or in-memory implementations for tests).
- An `IEmbeddingGenerator` from [[Agency.Embeddings.Common]] to vectorise each record before upsert.

## API Surface

### Public registration extensions — DI entry points

```csharp
// File: src/Memory/Agency.Memory.Distiller/DependencyInjection/MemoryServiceCollectionExtensions.cs
using Agency.Memory.Common.Options;
using Agency.Memory.Distiller.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

public static class MemoryServiceCollectionExtensions
{
    // Registers: IAsyncEventBus (InMemoryEventBus), ChannelSessionRegistry,
    // IConversationManagerRegistry, InactivityTimerService (IHostedService),
    // DistillerBackgroundService (IHostedService), and baseline AgentHooks via
    // IPostConfigureOptions<AgentOptions>.
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
    // Wraps an IChatClient as the distiller's LLM adapter (ChatClientLlmAdapter).
    // Must be called in addition to AddAgencyMemory — the distiller has no
    // default LLM binding.
    public static IServiceCollection AddAgencyDistillerLlm(
        this IServiceCollection services,
        IChatClient client,
        string model);
}
```

### Internal services (accessible to test projects via `InternalsVisibleTo`)

```csharp
// File: src/Memory/Agency.Memory.Distiller/Services/DistillerBackgroundService.cs
using Agency.Memory.Common.Storage;
using Agency.Memory.Distiller.Services;

// BackgroundService. Single consumer; drains all per-session channels then
// suspends on ChannelSessionRegistry.WaitForWorkAsync (SemaphoreSlim wake).
// Depends on [[Agency.Memory.Common]]'s storage contracts IMemoryStore,
// IWatermarkStore, and IDeadLetterStore directly — there are no local adapters.
// ActivitySource name : "Agency.Memory.Distiller"
// Meter name          : "Agency.Memory.Distiller"
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
using Agency.Memory.Common.Jobs;
using Agency.Memory.Distiller.Services;
using System.Threading.Channels;

// Manages one bounded Channel<DistillationJob> per session.
// Capacity: DistillerOptions.PerSessionQueueCapacity (default 32), DropOldest.
// An internal NotifyingChannelWriter releases a SemaphoreSlim on every successful write.
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
using Agency.Harness.Contexts;
using Agency.Memory.Distiller.Services;

// IHostedService + IDisposable. Holds a ConcurrentDictionary<sessionId, ...> of timer state.
// Each Restart() call disposes the prior ITimer and starts a new one via TimeProvider.
// Timer expiry enqueues DistillationJob(Trigger=Inactivity) via ChannelSessionRegistry.
internal sealed class InactivityTimerService : IHostedService, IDisposable
{
    internal void Restart(string userId, string sessionId, int currentTurnIndex, FocusContext? focus = null);
    internal void Stop(string sessionId);
}
```

```csharp
// File: src/Memory/Agency.Memory.Distiller/Services/IConversationManagerRegistry.cs
using Agency.Harness;
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
// unit-testable via stub implementations. Concrete impl: ChatClientLlmAdapter
// (wraps Microsoft.Extensions.AI.IChatClient, MaxOutputTokens = 2048).
internal interface ILlmClientAdapter
{
    Task<string> SendAsync(string prompt, CancellationToken ct = default);
}
```

### Agent tools (registered per session via MemorySessionTools)

```csharp
// File: src/Memory/Agency.Memory.Distiller/Tools/MarkGoalCompleteTool.cs
using Agency.Llm.Common.Tools;
using Agency.Memory.Distiller.Tools;

// ITool. Enqueues DistillationJob(Trigger=GoalCompletion) to the session channel.
// Does NOT stop the agent loop. Watermark prevents reprocessing.
// Tool name: "MarkGoalComplete"; parameter: summary (string, optional).
internal sealed class MarkGoalCompleteTool : ITool { }
```

```csharp
// File: src/Memory/Agency.Memory.Distiller/Tools/SetFocusTool.cs
using Agency.Llm.Common.Tools;
using Agency.Memory.Distiller.Tools;

// ITool. Updates Context.Focus to bias retrieval. Overrides GetDefinitionAsync to
// dynamically list the user's known domain values in the description.
// Idempotent: setting the same values twice is a no-op.
// Tool name: "SetFocus"; parameters: title (string?), domain (string?), tags (string[]?).
internal sealed class SetFocusTool : ITool { }
```

### Prompt and parse layer

```csharp
// File: src/Memory/Agency.Memory.Distiller/Prompts/EpisodeExtractionPrompt.cs
using Agency.Harness.Contexts;
using Agency.Memory.Common.Jobs;
using Agency.Memory.Common.Records;
using Agency.Memory.Distiller.Prompts;
using Microsoft.Extensions.AI;

// Static renderer for the episode-extraction prompt (template v2).
// Includes a /no_think directive to suppress chain-of-thought (TI-8.2).
// Covers both Fact and Memory extraction in a single prompt.
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
using Agency.Memory.Common.Records;
using Agency.Memory.Distiller.Prompts;

// Parses the LLM JSON response into Record instances via Record.Create.
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
// One parse retry is allowed (stricter re-prompt); a second failure dead-letters the job.
internal sealed class ExtractionParseException : Exception { }
```

## Registration

Call both extension methods during host startup:

```csharp
using Agency.Memory.Common.Options;
using Agency.Memory.Distiller;
using Agency.Memory.Distiller.DependencyInjection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

// Prerequisites that must already be registered:
//   IMemoryStore          (a storage provider, e.g. Postgres + pgvector)
//   IWatermarkStore       ([[Agency.Memory.Common]] contract; provider-supplied)
//   IDeadLetterStore      ([[Agency.Memory.Common]] contract; provider-supplied)
//   IEmbeddingGenerator   ([[Agency.Embeddings.Common]])
//   (IAsyncEventBus is registered by AddAgencyMemory itself.)

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

`AddAgencyMemory` registers:

- `IAsyncEventBus` → `InMemoryEventBus` (singleton).
- `ChannelSessionRegistry` (singleton; holds all per-session channels).
- `IConversationManagerRegistry` → `InMemoryConversationManagerRegistry` (singleton).
- `InactivityTimerService` (singleton, also registered as `IHostedService`).
- `DistillerBackgroundService` as an `IHostedService` — its constructor resolves [[Agency.Memory.Common]]'s `IMemoryStore`, `IWatermarkStore`, and `IDeadLetterStore` from the container (no local storage adapters exist; the host wires up the implementations of those Common interfaces).
- Baseline `AgentHooks` via `IPostConfigureOptions<AgentOptions>` — sets `AgentOptions.BaselineHooks` so the retrieval callback, timer-restart, session registration / tool registration, and session-end distillation job are wired without further configuration.

`AddAgencyDistillerLlm` registers `ILlmClientAdapter` → `ChatClientLlmAdapter`. `AgentFactory` composes `BaselineHooks` first, followed by any `UserHooks`.

## How It Works

### Trigger conditions

Three events enqueue a `DistillationJob` into the session's bounded channel:

| Trigger | Source | `DistillationTrigger` value |
|---|---|---|
| Agent calls `MarkGoalComplete` tool | `MarkGoalCompleteTool.InvokeAsync` | `GoalCompletion` |
| Session idle beyond `InactivityTimeout` | `InactivityTimerService.OnTimerExpired` | `Inactivity` |
| `ChatSession` is disposed | `SessionEndHook` via the `OnSessionEnded` baseline hook | `SessionDisposed` |

`OnAssistantTurn` only restarts the inactivity timer — it never enqueues a job directly (hot-path discipline).

### Channel model

`ChannelSessionRegistry` maintains one bounded `Channel<DistillationJob>` per session (`DropOldest`, default capacity 32). An internal `NotifyingChannelWriter` wrapper releases a `SemaphoreSlim` on every successful write. `DistillerBackgroundService` sweeps all session channels with `TryRead` in a tight loop; when every channel is empty it suspends on `WaitForWorkAsync`, which waits on that semaphore. This event-driven wake means a newly-enqueued job is picked up with no polling delay.

### Processing a job

1. **Watermark guard** — reads the stored watermark for the session via `IWatermarkStore.GetAsync`. If `job.UpToTurnIndex <= watermark`, the job is a no-op and a `DistillationCompletedEvent(RecordsWritten=0)` is published.
2. **Turn slice** — resolves the session's `IConversationManager` from the registry, then takes `Messages.Skip(watermark).Take(upTo - watermark)`. If no conversation is registered or the slice is empty, it skips the LLM call and completes with zero records.
3. **Context assembly** — loads the user's known domain labels and the 10 most-recent Fact records to provide deduplication context to the prompt.
4. **LLM call** — renders the episode-extraction prompt via `EpisodeExtractionPrompt.Render` (template v2, includes `/no_think`) and sends it via `ILlmClientAdapter.SendAsync` (`ChatClientLlmAdapter` sets `MaxOutputTokens = 2048`).
5. **Parse** — `EpisodeExtractionParser.Parse` deserializes the JSON response into `Record` instances. Code fences are stripped. One parse retry is allowed; a second failure dead-letters the job.
6. **Embed and upsert** — for each record, embeds `Title + "\n\n" + Value` via `IEmbeddingGenerator`, then calls `IMemoryStore.UpsertAsync`.
7. **Watermark advance** — calls `IWatermarkStore.AdvanceAsync` (monotone MAX). Emits `DistillationCompletedEvent`.
8. **Session cleanup** — if `Trigger == SessionDisposed`, unregisters the conversation manager and removes the session channel after the successful write.

```csharp
using Agency.Memory.Common.Jobs;
using Agency.Memory.Common.Storage;
using Microsoft.Extensions.AI;

// Inside DistillerBackgroundService.ProcessJobAsync (simplified):
int watermark = await watermarks.GetAsync(job.UserId, job.SessionId, ct);
if (job.UpToTurnIndex <= watermark)
{
    return; // already distilled — idempotent no-op
}

IConversationManager? convo = conversationRegistry.Get(job.SessionId);
var turns = convo!.Messages
    .Skip(watermark)
    .Take(job.UpToTurnIndex - watermark)
    .ToList();

int recordsWritten = await ExtractAndUpsertAsync(job, turns, ct);
int newWatermark = await watermarks.AdvanceAsync(
    job.UserId, job.SessionId, job.UpToTurnIndex, ct);
```

### Retry and error handling

| Error class | Detection | Action |
|---|---|---|
| Transient | HTTP 429 / 503; connection-level `HttpRequestException` with `StatusCode == null`; `TaskCanceledException` wrapping `TimeoutException`; `DbException.IsTransient` | Exponential backoff up to `MaxRetries`; dead-letter on exhaustion |
| Parse failure | `ExtractionParseException` | One retry (implicit stricter re-prompt); second failure → dead-letter |
| Permanent | Any other unrecognised exception | Dead-letter immediately |
| Cancellation | `OperationCanceledException` | Re-thrown; never dead-lettered |

Dead-letter writes go to [[Agency.Memory.Common]]'s `IDeadLetterStore.WriteAsync` and are followed by a `DistillationFailedEvent`. Both `DistillationCompletedEvent` and `DistillationFailedEvent` are published on `IAsyncEventBus` after each job settles.

### Session lifecycle hooks

`MemoryServiceCollectionExtensions.AddAgencyMemory` builds four baseline hook callbacks via `MemoryHookFactory.Build`:

| Hook | Callback | Effect |
|---|---|---|
| `OnPreIteration` | `RetrievalGate.ShouldRetrieveAsync` → `RetrievalEngine.RetrieveAsync` | Injects knowledge / memory into `Context` when the store has changed since last retrieval |
| `OnAssistantTurn` | `InactivityTimerService.Restart` | Resets the per-session inactivity timer; no other side effect |
| `OnSessionStarted` | `ConversationRegistrationHook` + `MemorySessionTools.RegisterInto` | Registers `IConversationManager` in the registry; adds `MarkGoalComplete` and `SetFocus` tools to `Context.Tools` |
| `OnSessionEnded` | `SessionEndHook` | Enqueues `DistillationJob(Trigger=SessionDisposed)` |

### Shutdown drain

On host shutdown, `ExecuteAsync` exits its main loop when the `stoppingToken` is cancelled, then calls `DrainAsync(ShutdownDrainTimeout)` (default 30 s). Any in-flight job interrupted by the timeout, plus any jobs still in channels after the deadline, are dead-lettered for watermark-based recovery on the next process start.

## Observability

Defined on `DistillerBackgroundService`:

- **ActivitySource:** `"Agency.Memory.Distiller"` — span `memory.distill` per job, tagged with `memory.user_id`, `memory.session_id`, `memory.trigger`.
- **Meter:** `"Agency.Memory.Distiller"`

| Instrument | Name | Unit | Meaning |
|---|---|---|---|
| Counter `long` | `memory.distiller.jobs` | — | Total distillation jobs processed |
| Counter `long` | `memory.distiller.errors` | — | Permanent distillation failures (dead-lettered) |
| Histogram `double` | `memory.distiller.duration` | ms | Distillation job duration |

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.Memory.Common]] | Depends on its storage contracts `IMemoryStore`, `IWatermarkStore`, and `IDeadLetterStore` directly (the local watermark/dead-letter interfaces and adapters were removed); also consumes `Record`, `DistillationJob`, `DistillerOptions`, `MemoryOptions`, events, `IAsyncEventBus`, and `MemoryHookFactory` |
| [[Agency.Memory.Retrieval]] | `AddAgencyMemory` builds a `RetrievalEngine` / `RetrievalGate` and binds them to the `OnPreIteration` baseline hook |
| [[Agency.Harness]] | Consumes `BackgroundService` wiring, `IConversationManager`, `Context`, `FocusContext`, `AgentOptions.BaselineHooks`, and hook context types |
| [[Agency.Llm.Common]] | `MarkGoalCompleteTool` and `SetFocusTool` implement its `ITool` contract |
| [[Agency.Embeddings.Common]] | `IEmbeddingGenerator` used to embed `Title + "\n\n" + Value` before upsert |
| [[Agency.Memory.Distiller.Test]] | Unit test project; accesses internals via `InternalsVisibleTo` |
| [[Agency.Memory.Functional.Test]] | Functional test project; accesses internals to wire stub and real LLM clients |

## Design Notes

- **Storage contracts live in [[Agency.Memory.Common]], not here.** `IWatermarkStore` and `IDeadLetterStore` (and `IMemorySchemaInitializer`) were moved out of the Distiller into `Agency.Memory.Common.Storage`, and the project's old local interfaces + `WatermarkStoreAdapter` / `DeadLetterStoreAdapter` were deleted along with its reference to any concrete provider. This lets the Distiller depend only on narrow abstractions while the host selects a storage provider at composition time, so the write-path component never references a database package directly.

- **The agent never authors a memory.** `MarkGoalComplete` and `SetFocus` are the only memory-adjacent tools the primary agent sees. `MarkGoalComplete` enqueues a distillation trigger; it does not write a record. The actual extraction decision — what is worth remembering, in which domain, at what importance — is made by the distiller's LLM call after the fact. This keeps the hot-path agent focused on its task and produces more consistent memories than agent-authored writes.

- **Per-session channels with a single global consumer.** One bounded channel per session means `DropOldest` backpressure applies at the session level rather than globally — a chatty session cannot starve others, and a flood of jobs in one session simply drops its own oldest entries. The `NotifyingChannelWriter` + `SemaphoreSlim` pattern preserves the event-driven wake of a single-consumer design without per-session background tasks or polling loops.

- **Watermark idempotency makes crash recovery free.** The watermark is advanced via `IWatermarkStore.AdvanceAsync` only after a successful upsert. A process crash mid-distillation leaves the watermark un-advanced; the next run re-reads the same turn slice and re-distills it. The `(Domain, Key)` collision rule in the prompt and the store's upsert semantics make re-running converge to the same stored state. Jobs that cannot complete before shutdown are dead-lettered so they can be recovered on the next start.

- **Thinking suppression is enforced at the prompt layer.** The extraction prompt template always opens with a `/no_think` directive. Episode extraction is deterministic JSON authoring that gains nothing from chain-of-thought; suppressing it eliminates unnecessary token latency on a cold-path LLM call (TI-8.2).

- **`SessionDisposed` cleanup is deferred until after the write.** When the trigger is `SessionDisposed`, the conversation manager is unregistered and the session channel is removed only after `IMemoryStore.UpsertAsync` succeeds for all extracted records. Early removal would lose the turn data the distiller still needs to read.
