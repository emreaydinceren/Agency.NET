# Agency.Memory.Hygiene
#memory #hygiene #pruning

## What It Is

`Agency.Memory.Hygiene` is the maintenance background service for the Agency long-term memory subsystem. It runs periodic sweeps against [[Agency.Memory.Common]]'s `IMemoryStore`, hard-deleting records that have aged past their per-`ContentType` TTL and records whose importance score falls below a configured threshold and have not been accessed within a staleness window. The sweeper operates entirely off the hot path — it never participates in a user-facing agent turn — and is the only subsystem in the memory stack that is LLM-free.

**Namespace:** `Agency.Memory.Hygiene`

## API Surface

```csharp
// File: src/Memory/Agency.Memory.Hygiene/HygieneSweeperBackgroundService.cs
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Agency.Memory.Common.Options;
using Agency.Memory.Common.Records;
using Agency.Memory.Common.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agency.Memory.Hygiene;

// internal sealed — exposed to Agency.Memory.Hygiene.Test and Agency.Memory.Functional.Test
// via InternalsVisibleTo in AssemblyInfo.cs.
internal sealed class HygieneSweeperBackgroundService : BackgroundService
{
    internal const string ActivitySourceName = "Agency.Memory.Hygiene";
    internal const string MeterName          = "Agency.Memory.Hygiene";

    // Executes the sweep loop until the host signals shutdown.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken);

    // Applies ±15-minute jitter to the configured base interval.
    public static TimeSpan ApplyJitter(TimeSpan baseInterval);

    // Runs a single TTL pass + importance pass and returns deletion counts.
    internal async Task<SweepResult> RunOnceAsync(CancellationToken ct);
}
```

```csharp
// File: src/Memory/Agency.Memory.Hygiene/SweepResult.cs
namespace Agency.Memory.Hygiene;

// internal sealed record — not exposed outside the assembly.
internal sealed record SweepResult(int TtlDeleted, int ImportanceDeleted)
{
    public int TotalDeleted => TtlDeleted + ImportanceDeleted;
}
```

```csharp
// File: src/Memory/Agency.Memory.Hygiene/DependencyInjection/HygieneServiceCollectionExtensions.cs
using Agency.Memory.Common.Options;
using Agency.Memory.Common.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Agency.Memory.Hygiene.DependencyInjection;

public static class HygieneServiceCollectionExtensions
{
    // Registers HygieneSweeperBackgroundService as a hosted service.
    // Requires IMemoryStore to already be registered.
    public static IServiceCollection AddAgencyHygiene(
        this IServiceCollection services,
        Action<MemoryOptions>? configure = null);
}
```

## Registration

Call `AddAgencyHygiene()` after an `IMemoryStore` implementation has been registered. The sweeper resolves `TimeProvider` from the container, so tests can substitute a `FakeTimeProvider` to advance virtual time without wall-clock delays. When no `TimeProvider` is registered, `TimeProvider.System` is used.

```csharp
using Agency.Memory.Common.Records;
using Agency.Memory.Hygiene.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

services
    .AddAgencyMemorySqlPostgres(connectionString)  // registers IMemoryStore
    .AddAgencyHygiene(opts =>
    {
        opts.Ttl[ContentType.Memory] = TimeSpan.FromDays(90);
        opts.Ttl[ContentType.Fact]   = TimeSpan.FromDays(365);
        opts.ImportancePruneThreshold = 0.2;
        opts.StalePruneAge            = TimeSpan.FromDays(30);
        opts.HygieneSchedule          = TimeSpan.FromHours(24);
    });
```

## How It Works

### Sweep schedule

`ExecuteAsync` runs a `while` loop that delays for `HygieneSchedule ± 15 minutes` before each pass. The ±15-minute jitter is applied by `ApplyJitter` using `Random.Shared.Next(-15, 16)`, preventing thundering-herd behaviour when multiple process instances start at the same time. The delay is driven by `Task.Delay(interval, timeProvider, stoppingToken)` so the injected `TimeProvider` governs both the clock and the wait, making the schedule deterministic under a virtual clock in tests.

On cancellation, the service exits the loop cleanly. Unexpected exceptions during a sweep are caught and logged at `Error` level; the loop continues rather than crashing the host.

### TTL pass

For each `(ContentType, TimeSpan)` entry in `MemoryOptions.Ttl`, the sweeper calls `IMemoryStore.DeleteWhereTtlExceededAsync(contentType, ttl, now, ct)`. The store deletes records of that content type where `updated_at < now - ttl` AND `last_accessed_at IS NULL OR last_accessed_at < now - ttl`. The current UTC time is read once from the injected `TimeProvider` at the start of each sweep and passed to both store methods, ensuring the entire sweep is measured from the same reference point (Spec §TI-4).

### Importance-pruning pass

After the TTL pass, the sweeper calls `IMemoryStore.DeleteWhereLowImportanceStaleAsync(importanceThreshold, staleAge, now, ct)`. The store deletes records where `importance < importanceThreshold` AND `last_accessed_at IS NULL OR last_accessed_at < now - staleAge`. Default values from `MemoryOptions` are `ImportancePruneThreshold = 0.2` and `StalePruneAge = TimeSpan.FromDays(30)`.

### `SweepResult`

`RunOnceAsync` returns a `SweepResult(TtlDeleted, ImportanceDeleted)` containing per-pass deletion counts and a derived `TotalDeleted`. This type is `internal` and is used directly by the unit and functional test suites via `InternalsVisibleTo`.

## Observability

`HygieneSweeperBackgroundService` creates a static `ActivitySource` and `Meter` at class-load time.

**ActivitySource name:** `Agency.Memory.Hygiene`
**Meter name:** `Agency.Memory.Hygiene`

| Instrument | Name | Kind | Tags | Description |
|---|---|---|---|---|
| Counter | `memory.swept.ttl` | `Counter<long>` | `content_type` | Records deleted per content type by the TTL pass |
| Counter | `memory.swept.importance` | `Counter<long>` | — | Records deleted by the importance-pruning pass |

Each call to `RunOnceAsync` opens a single `Activity` named `memory.sweep` with `ActivityKind.Internal`. Configure your OpenTelemetry pipeline to listen on `Agency.Memory.Hygiene` to receive both traces and metrics from every sweep.

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.Memory.Common]] | Provides `IMemoryStore` (the two deletion methods the sweeper calls), `ContentType`, `MemoryOptions` (TTL map, importance threshold, stale age, schedule), and `Record` |
| [[Agency.Memory.Sql.Postgres]] | Ships the production `PostgresMemoryStore` that implements `DeleteWhereTtlExceededAsync` and `DeleteWhereLowImportanceStaleAsync` via bulk SQL with `BTREE (updated_at)` and `BTREE (last_accessed_at)` index scans |
| [[Agency.Memory.Retrieval]] | Updates `last_accessed_at` on every retrieval hit, which resets the staleness window and prevents recently-used records from being pruned by either pass |
| [[Agency.Memory.Distiller]] | Writes `Record` items via `IMemoryStore.UpsertAsync`; these newly written records are the primary source of data that the hygiene sweeper later prunes when they age out |
| [[Agency.Memory.Consolidator]] | Merges and replaces records via `IMemoryStore.MergeAsync`; merged records carry refreshed timestamps that restart their TTL window |

## Design Notes

- **All staleness windows are measured from the injected `TimeProvider` clock, not the database wall clock.** Both `DeleteWhereTtlExceededAsync` and `DeleteWhereLowImportanceStaleAsync` accept a `DateTimeOffset now` parameter. The sweeper captures `timeProvider.GetUtcNow()` once per sweep and passes the same value to both store calls. This ensures the TTL predicate is deterministic under a `FakeTimeProvider` in tests (Spec §TI-4) and avoids subtle drift between the application clock and the database `NOW()` function.

- **The sweeper is single-threaded; bulk SQL handles the heavy lifting.** Both pruning passes delegate to a single `DELETE … WHERE` statement inside the store implementation, with no per-record iteration in managed code. This keeps the sweeper's managed-heap footprint near zero regardless of how many records are deleted, and avoids the need for batching or concurrency controls inside the background service itself.

- **Jitter prevents thundering herd without a distributed lock.** When multiple instances of the host are deployed, each independently randomises its first sleep within ±15 minutes of `HygieneSchedule`. Because the underlying SQL deletes are idempotent (a record that has already been deleted by one instance simply returns 0 rows affected), races between concurrent sweepers are harmless, and no coordination mechanism is required.
