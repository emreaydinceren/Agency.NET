# 4. The inference gate sits on the inference port

## Status

Accepted

## Context

The development box hosting the local inference server has a hard concurrency
ceiling: more than two concurrent inferences crashes the AMD 8060S iGPU. With
six Personas in a Room, exceeding it is the normal case, not an edge case.

The incident that motivated this was a client hitting the inference port
**directly, from outside any adapter**. That rules out the obvious fix: a
client-side semaphore inside `Agency.Acp` would bound only the requests that
happen to travel through `Agency.Acp`. A guard the offending client can route
around is a convention, not a guarantee.

Two other options were considered:

- **A mode of the existing `Agency.Utils.HttpCacheProxy`.** It already sits in
  the request path for functional tests. But a cache in front of a live
  inference endpoint is exactly wrong, and "caching is provably off in this
  mode" is a claim that has to be re-proved on every change to shared code.
- **Per-client limits in each adapter.** Multiplies the same logic across every
  caller and still misses `curl`.

There is also a compounding factor. The harness retries three times on empty
choices with 250 ms linear backoff, and three times on a degenerate response
with **no delay at all**. Passing an upstream rejection back to a client with
no backoff converts one rejection into a storm — so rejections must be absorbed
and retried *behind* the limiter, not forwarded.

## Decision

Enforce the ceiling in a **separate component on the inference port**, where
every client passes — including `curl`.

- `Agency.Utils.InferenceGate` listens on `0.0.0.0:1234`; the inference server
  moves to `127.0.0.1:1235`.
- A `SemaphoreSlim(Capacity)` wraps the forward, released in a `finally` so an
  aborted connection cannot leak a slot.
- `/healthz` reports capacity, in-flight, queue depth and upstream
  reachability, and distinguishes gate-up/upstream-down from
  gate-up/upstream-up.
- Upstream rejections are absorbed and retried behind the semaphore rather than
  passed through.

It is a **separate component, not a proxy mode**, because "caching provably
off" is cheapest to prove by the cache code not being compiled in — asserted by
reflecting over `GetReferencedAssemblies()`.

## Consequences

- The ceiling holds for every client, including ones that bypass every adapter.
- One more process to run. It autostarts with the box.
- **Reversibility is preserved deliberately:** `:1235` is documented as the
  on-box bypass, and **no client config anywhere encodes `1235`**, so rollback
  is a single edit.
- **The gate bounds concurrency, not model residency.** Loading a model can
  exhaust the GPU with no concurrent inference at all — and on this box models
  are never evicted, so requested models accumulate until VRAM is exhausted. A
  model-aware gate that drains pending requests for the resident model before
  admitting a switch would close this. Deferred, not impossible.
- Retry-absorption is scoped to `429`/`503` rather than all 4xx: blanket-retrying
  a permanent `400` or `404` would loop forever on a request that can never
  succeed.
- Queue time is now part of end-to-end latency, so `LlmClientOptions.Timeout`
  must exceed queue time plus generation. With capacity 2 and six Personas, the
  sixth waits roughly three inference rounds.
