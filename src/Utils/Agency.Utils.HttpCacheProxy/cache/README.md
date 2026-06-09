# Functional-test response cache — hand-off

This directory holds the **persistent HTTP response cache** for `Agency.Utils.HttpCacheProxy`.
The `*.json` blobs are checked into git on purpose: they let the proxy serve every functional
LLM / embedding request **offline**, so the CI functional-test step (which has no LM Studio
upstream) passes from cache instead of failing on an unreachable model server.

Each blob is one cached upstream response. The filename is
`SHA256("{Method}|{PathAndQuery}|{SHA256(requestBody)}")`; the body is stored base64-encoded
(correct for compressed / SSE / binary payloads — the proxy does not decompress). Entries
loaded from disk never expire (`FileCache` ⇒ infinite TTL).

## How a cache hit is decided

The cache key is an **exact match** on `Method + PathAndQuery + SHA256(request body)`. There is
no fuzzy / prefix matching (unlike a provider-side prompt cache). A single differing byte in the
request body produces a different key and therefore a **miss**. So a request is cacheable **iff
its bytes are identical on every run**.

## Why cache misses happen

A miss during a cache-only replay means the request the test produced does **not** match any
stored blob. Two distinct causes were seen building this cache — keep both in mind when adding
or regenerating entries.

### 1. Non-deterministic request bodies — *permanent, cannot be cached*

If any per-run-variable data is embedded in the request body, its SHA256 changes every run, so
it never matches a stored blob. Known sources:

- **Random GUIDs.** The consolidator's reconciliation prompt
  (`ConsolidatorReconciliationPrompt.Render`) embeds the per-run `userId` and **each record's
  `Id`** (`### [...] "{Title}" (id: {r.Id})`). Tests seed records with `Guid.NewGuid()`, and the
  `Memory_Merge` tool mints a fresh GUID for the merged record that feeds later agent turns — so
  even pinning seed IDs would not fully stabilise the prompt.
- **Timestamps / dates / temp paths** interpolated into a prompt.
- **Non-deterministic ordering** — dictionary iteration order, parallel-result concatenation,
  unsorted DB query results rendered into a prompt.

The fix is determinism, not more caching. Tests whose LLM input is inherently GUID-bearing (the
Group 3 consolidation tests, `Group3ConsolidationTests`) **cannot** be cache-replayed. They
degrade gracefully: when the LLM-driven action does not occur, they `Assert.Skip` with an
advisory rather than fail (see the comment on `Consolidator_MergesDuplicateFacts_IntoSingleRecord`
/ E3.2, mirroring its sibling E3.4). The rest of the functional suite uses constant prompts and
caches cleanly.

### 2. Stale in-memory proxy state while regenerating — *operational pitfall*

The proxy keeps a hot in-memory tier on top of the disk blobs. The disk write happens in `Set`,
which **only runs on a miss**. If you delete the blobs in `cache/` **while a proxy is still
running**, the proxy still holds those entries in memory: the next matching request is served as
an in-memory **HIT** and is therefore **never re-written to disk**. The regenerated cache then
silently lacks those entries, and they miss on the next fresh start.

> **Always restart the proxy after clearing `cache/`** so its in-memory tier starts empty and
> every request is a true miss that re-persists to disk.

This is exactly what produced a partial cache during development: the embeddings-suite responses
were masked as in-memory hits and never persisted, so a later cold start missed all of them.

## Regenerating the cache

1. Start LM Studio (and Postgres for the Memory suite).
2. Clear `cache/*.json` **and start a fresh proxy** (empty memory) against the real upstream:
   `dotnet run --project Utils/Agency.Utils.HttpCacheProxy` (FileCache is on by default).
3. Run the functional suite through it (mirrors CI):
   `dotnet test --filter "Category=Functional" -- RunConfiguration.MaxCpuCount=1`
   Every request should be a `[MISS]` that gets persisted; the proxy console prints HIT/MISS.

## Validating completeness (the acceptance test)

Point the proxy's upstream at an **unreachable** address and replay the suite — any real request
then 502s, so a miss is loud:

```
$env:Proxy__Routes__0__TargetUrl = "http://127.0.0.1:9"   # dead upstream
# start proxy, then:
dotnet test --filter "Category=Functional" --no-build -- RunConfiguration.MaxCpuCount=1
```

The cache is complete when this passes with **zero `[MISS]`** in the proxy log (consolidation
tests may `Skip` per cause #1, which is expected — not a gap).
