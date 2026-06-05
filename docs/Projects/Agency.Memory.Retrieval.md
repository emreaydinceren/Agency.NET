# Agency.Memory.Retrieval
#memory #retrieval #ranking #rag

## What It Is

`Agency.Memory.Retrieval` is the read-path component of the long-term memory subsystem that, on each agent iteration, decides whether a vector search is needed, builds and embeds a query from the current conversation turn and focus context, over-fetches candidate records from the store, re-ranks them with a composite scoring formula, partitions the results by `ContentType`, and writes the top-K results into `Context.Knowledge` and `Context.Memory` so the system-prompt builder can inject them into the next LLM call.

**Namespace:** `Agency.Memory.Retrieval`

## How It Works

### 1. Gate check (`RetrievalGate`)

Before any I/O, `RetrievalGate.ShouldRetrieveAsync` compares `ctx.MemoryLastRetrievedAt` against the store's `LastWrittenAt` timestamp for the user. The store maintains an in-memory `ConcurrentDictionary` keyed by `userId`, so this comparison is O(1) — no database round-trip. Retrieval is skipped when the store has not been mutated since the last pass; this keeps multi-iteration turns (where the store is static) essentially free.

The gate returns `true` (run the search) in three situations: no prior writes exist for the user, this is the first retrieval in the session, or the store was mutated after the previous retrieval.

### 2. Query construction (`RetrievalEngine.BuildQueryText`)

The engine concatenates the text of the most recent user message with any active `FocusContext` fields (`Title`, `Domain`, and `Tags` joined by spaces). Focus terms bias the embedding toward the current task domain without forcing exact-match filtering at the SQL level.

### 3. Embedding

The combined query string is passed to `Agency.Embeddings.Common.IEmbeddingGenerator.GenerateEmbeddingAsync` to produce a dense vector.

### 4. Over-fetch

The engine calls `IMemoryStore.SearchAsync` with `topK = RetrievalTopK × OverFetchFactor` (default: `10 × 3 = 30`). The store honours this value verbatim and orders results by cosine similarity only; widening the candidate pool before re-ranking is the engine's exclusive responsibility.

### 5. Composite re-ranking

Each `SearchHit` is scored by `RankingFormula.Score` (Spec §8.3):

```
score = wₛ · clip(similarity, 0, 1)
      + wᵣ · exp(-ageDays / halfLifeDays)
      + wᵢ · record.Importance
      + wₘ · sessionMatch
```

Default weights: `wₛ=0.5`, `wᵣ=0.3`, `wᵢ=0.2`, `wₘ=0.1`; `halfLifeDays=7`. The scored list is sorted descending and trimmed to `RetrievalTopK`.

### 6. `ContentType` partition

The top-K results are partitioned into two buckets using a switch expression:

```csharp
(r.ContentType switch
{
    ContentType.Fact   => facts,
    ContentType.Memory => memories,
    _ => throw new InvalidOperationException($"Unhandled ContentType {r.ContentType}."),
}).Add(projected);
```

`ContentType.Fact` records become `MemoryRecord` projections (title, value, updated-at) placed in `ctx.Knowledge.Records`. `ContentType.Memory` records go into `ctx.Memory.Records`. Both context properties are then replaced with `with` expressions on the existing record value, so the system-prompt builder sees the fresh lists on the next iteration.

### 7. Context injection

After partitioning, `ctx.MemoryLastRetrievedAt` is stamped with `DateTimeOffset.UtcNow`. The system-prompt builder reads `ctx.Knowledge.Records` and `ctx.Memory.Records` to render `## Facts` and `## Memories` sections; the LLM never sees raw scores or timestamps — only title, markdown body, and a human-readable recency string.

### Code example

```csharp
using Agency.Harness.Contexts;
using Agency.Memory.Common.Options;
using Agency.Memory.Common.Ranking;
using Agency.Memory.Common.Storage;
using Agency.Memory.Retrieval;
using Microsoft.Extensions.Options;

// Injected by the DI container; shown here to illustrate the call signature.
IMemoryStore store = ...;
Agency.Embeddings.Common.IEmbeddingGenerator embedder = ...;
IOptions<MemoryOptions> options = ...;

var engine = new RetrievalEngine(store, embedder, options);
var gate   = RetrievalGate.ShouldRetrieveAsync(ctx, store, ct);

if (await gate)
{
    await engine.RetrieveAsync(ctx, ct);
    // ctx.Knowledge.Records now holds Fact-type MemoryRecords
    // ctx.Memory.Records   now holds Memory-type MemoryRecords
}
```

Both `RetrievalEngine` and `RetrievalGate` are `internal`; host code does not call them directly. They are wired to `OnPreIteration` by `MemoryHookFactory` inside `AddAgencyMemory`. `InternalsVisibleTo` grants access to `Agency.Memory.Retrieval.Test`, `Agency.Memory.Distiller`, and `Agency.Memory.Functional.Test`.

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.Memory.Common]] | Provides `IMemoryStore`, `Record`, `ContentType`, `RankingFormula`, `RankingWeights`, `MemoryOptions`, and `SearchQuery`/`SearchHit` that the engine and gate depend on entirely |
| [[Agency.Harness]] | Owns `Context`, `KnowledgeContext`, `MemoryContext`, `MemoryRecord`, `FocusContext`, and `SessionContext`; the engine reads and writes these types each iteration |
| [[Agency.Embeddings.Common]] | Supplies `IEmbeddingGenerator` used to vectorise the retrieval query |
| [[Agency.Memory.Sql.Postgres]] | Ships the `PostgresMemoryStore` implementation of `IMemoryStore` that the engine searches at runtime |
| [[Agency.Memory.Distiller]] | Writes `Record` items via `IMemoryStore.UpsertAsync`; mutating the store advances `LastWrittenAt`, which is the signal the retrieval gate reads to decide whether to re-run |
| [[Agency.Mcp.Memory]] | Exposes agent-facing tools (`SetFocus`, `MarkGoalComplete`) that update `Context.Focus` and trigger distillation; focus changes feed back into the query built in step 2 |

## Design Notes

- **Why over-fetch before ranking.** Pure cosine similarity is the store's only ordering signal, but the final ranking formula combines four terms (similarity, recency, importance, session-match). A record with moderate similarity but high importance and a current-session tag can legitimately outrank a record with the highest cosine score. Fetching `RetrievalTopK × OverFetchFactor` candidates before applying the formula ensures the re-ranker sees a wide enough pool that the composite winner is not discarded by an upstream similarity-only cut. The store's `SearchAsync` is told the inflated count verbatim and applies no further filtering; the responsibility boundary is explicit (Spec §6.4, §6.1).

- **Why the switch expression with `_ => throw InvalidOperationException` rather than if/else.** The switch with exhaustive arms and a throwing default arm replaces an earlier `if (contentType == Fact) … else …` pattern. Two properties make the switch strictly safer: first, if a future `ContentType` value (for example a `Reflection` variant discussed in Spec §18.4) is added to the enum without updating this code, the C# compiler emits **CS8509** (non-exhaustive switch on a non-`[Flags]` enum), turning the oversight into a compile-time error rather than a runtime surprise. Second, should a value somehow reach the default arm at runtime (e.g., via deserialisation of a value from a newer schema), the `InvalidOperationException` makes the failure loud and traceable rather than silently routing the record to the wrong context bucket.

- **The §18.4 Reflection risk that the switch guards against.** Spec §18.4 describes a deferred v2 `Reflection` synthesis capability that would require a third `ContentType` value. If that value were introduced in `Agency.Memory.Common` and `ContentType` grew from two to three variants, an if/else fallback would silently place `Reflection` records into `ctx.Memory.Records` (the else branch), polluting the episodic memory section with generalised cross-episode patterns that have a different rendering contract. The explicit switch ensures any such addition forces an active decision — either add a `ContentType.Reflection => reflections` arm, or accept the compile error as a blocking reminder — rather than inheriting a wrong-but-plausible default.
