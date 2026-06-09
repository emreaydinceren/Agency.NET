# HTTP Cache Proxy — Functional-Test Cache Hand-off

How the functional-test suite runs **offline in CI**, and — the main thing this document is for —
**why cache misses happen** and how to avoid them when adding or regenerating cached responses.

## Background

`Agency.Utils.HttpCacheProxy` is a caching reverse proxy that sits between the functional tests
and the LLM/embedding server (LM Studio at `llm-host.example:1234`). Functional tests point their
client base URL at `http://localhost:12345`; the proxy forwards on a miss and replays on a hit.

CI runners cannot reach LM Studio, so live functional tests fail there. The fix is a **persistent
filesystem cache**: a complete set of recorded responses is checked into
`src/Utils/Agency.Utils.HttpCacheProxy/cache/*.json`. Both CI workflows already launch the proxy
(`🚀 Start HTTP Cache Proxy`), and it loads those blobs at startup, so every functional
LLM/embedding request is served from disk with **no upstream** — no workflow change required.

## How the cache decides a hit

The cache key is an **exact match**:

```
key = SHA256( "{Method}|{PathAndQuery}|{SHA256(request body)}" )
```

There is no fuzzy or prefix matching (unlike a provider-side prompt cache). **One differing byte
in the request body is a different key, i.e. a miss.** A request is therefore cacheable *iff its
bytes are identical on every run*.

Each blob is one cached response: `{ Method, PathAndQuery, BodyHash, StatusCode, Headers, Body }`,
with `Body` base64-encoded (correct for compressed / SSE / binary payloads — the proxy does not
decompress). Blobs loaded from disk **never expire** (`FileCache` ⇒ infinite TTL).

Configuration (`appsettings.json`, on by default):

```json
"Proxy": { "FileCache": { "Enabled": true, "Directory": "cache" } }
```

The directory resolves against the content root, which `dotnet run --project …` sets to the
project directory — that is why CI finds the checked-in `cache/` without any path configuration.

## Why cache misses happen

A miss on a cache-only replay means the request the test produced does **not** match any stored
blob. Two distinct causes were seen building this cache; keep both in mind.

### 1. Non-deterministic request bodies — *permanent; cannot be cached*

Any per-run-variable data in the request body changes its SHA256 every run, so it never matches a
stored blob. Known sources:

- **Random GUIDs.** The consolidator's reconciliation prompt
  (`ConsolidatorReconciliationPrompt.Render`) embeds the per-run `userId` and **each record's
  `Id`** (`### [...] "{Title}" (id: {r.Id})`). Tests seed records with `Guid.NewGuid()`, and the
  `Memory_Merge` tool mints a fresh GUID for the merged record that feeds later agent turns — so
  even pinning the seed ids would not fully stabilise the prompt.
- **Timestamps / dates / temp paths** interpolated into a prompt.
- **Non-deterministic ordering** — dictionary iteration order, parallel-result concatenation, or
  unsorted DB query results rendered into a prompt.

The fix is **determinism, not more caching**. Tests whose LLM input is inherently GUID-bearing
(the Group 3 consolidation tests in `Group3ConsolidationTests`) cannot be cache-replayed. They
degrade gracefully: when the LLM-driven action does not occur, they `Assert.Skip` with an advisory
rather than fail. `Consolidator_MergesDuplicateFacts_IntoSingleRecord` (E3.2) follows this pattern,
matching its sibling E3.4. The rest of the functional suite uses constant prompts and caches
cleanly.

### 2. Stale in-memory proxy state while regenerating — *operational pitfall*

The proxy keeps a hot in-memory tier on top of the disk blobs. The disk write happens in `Set`,
which **only runs on a miss**. If you delete the blobs in `cache/` **while a proxy is still
running**, the proxy still holds those entries in memory: the next matching request is served as
an in-memory **hit** and is therefore **never re-written to disk**. The regenerated cache then
silently lacks those entries, and they miss on the next cold start.

> **Always restart the proxy after clearing `cache/`** so its in-memory tier starts empty and
> every request is a true miss that re-persists.

This is exactly what produced a partial cache during development — the embeddings-suite responses
were masked as in-memory hits and never persisted, so a later cold start missed all of them.

## Regenerating the cache

1. Start LM Studio (and Postgres for the Memory suite).
2. Clear `cache/*.json` **and start a fresh proxy** (empty memory) against the real upstream:
   `dotnet run --project Utils/Agency.Utils.HttpCacheProxy` (FileCache is on by default).
3. Run the functional suite through it (mirrors CI):
   `dotnet test --filter "Category=Functional" -- RunConfiguration.MaxCpuCount=1`
   Every request should be a `[MISS]` that gets persisted; the proxy console prints `[HIT]`/`[MISS]`.

## Validating completeness — the acceptance test

Point the proxy's upstream at an **unreachable** address and replay the suite, so any real request
502s and a gap is loud:

```powershell
$env:Proxy__Routes__0__TargetUrl = "http://127.0.0.1:9"   # dead upstream
# start the proxy, then:
dotnet test --filter "Category=Functional" --no-build -- RunConfiguration.MaxCpuCount=1
```

The cache is complete when this passes with **zero `[MISS]`** in the proxy log. Consolidation tests
may `Skip` per cause #1 above — that is expected, not a gap.

## Adding a new LLM-calling functional test

1. Keep the request deterministic — no GUIDs / timestamps / unsorted collections in any prompt or
   embedded text. If the production code path injects such data, the request is not cacheable and
   the test must degrade gracefully (skip) under a cache-only run.
2. Regenerate the cache (above) so the new request's blob is recorded.
3. Run the dead-upstream acceptance test and confirm zero misses.
4. Commit the new `cache/*.json` blobs alongside the test.
