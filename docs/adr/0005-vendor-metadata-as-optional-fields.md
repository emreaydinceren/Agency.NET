# 5. Vendor model metadata as optional fields

## Status

Accepted

## Context

An ACP client wants to show a useful model picker: what kind of model this is,
how large its context window is, whether it is currently loaded. Some local
inference servers answer all of that. A generic OpenAI-compatible `/v1/models`
answers none of it — it returns little more than ids.

An earlier draft sourced the catalogue from LM Studio's native
`/api/v0/models`. That was wrong: Ollama and generic OpenAI-compatible servers
do not have that endpoint, so the feature would have been **load-bearing on a
vendor-specific path** and Agency would have silently become an LM Studio
client.

Three shapes were considered for carrying metadata that only *some* servers
provide:

- **An `IModelCatalogEnricher` interface** with one real implementation and a
  no-op default. This is an abstraction for single-use code: a type, a
  registration and an indirection, to express something two nullable fields
  already express.
- **Operator configuration** — have the user declare context lengths per model.
  Pushes a problem the server can often answer onto a human, and goes stale.
- **Carry nothing** — ids and names only. Honest, but discards information that
  is genuinely available and genuinely useful.

## Decision

Add **optional nullable fields** to `Model`: `ModelKind? Kind`,
`int? ContextLength`, `bool? IsLoaded`, as `init`-only members rather than
positional parameters, so `new Model(id, name)` keeps compiling.

Providers fill what their server answers and leave the rest `null`. A richer
catalogue fetch happens *after* the standard `/v1/models` call, inside a
`try/catch` that swallows everything and merges by id — **enrichment must never
fail a catalogue fetch**.

This establishes the governing principle for every future provider:

> **P2 — Vendor knowledge lives at the provider edge, never on the required
> path.** The contract is OpenAI-style or Claude-style HTTP. No feature may be
> load-bearing on a vendor-specific endpoint.

And its corollary:

> **P3 — Degrade honestly.** Absent data renders as absent, never as a claim.
> **`null` means *unknown*, never *false*.** A model with unknown residency
> shows as a plain row, not as "not loaded".

## Consequences

- Works against any OpenAI-compatible server; richer servers simply show more.
- No new type, no registration, no indirection. The degradation shape is
  identical to the `Description: null` the protocol already uses.
- **`null` is load-bearing and easy to get wrong.** Filtering "embedding models"
  must exclude only models *known* to be embeddings and retain `Kind == null`
  ones. The consequence is that where `Kind` is unknown, a non-chat model cannot
  be filtered out, so selecting one fails at **first inference** rather than at
  selection — and the error must then name the model and the likely cause,
  because it arrives after the user has asked the Persona to speak.
- The one vendor path that exists is confined to a single `private const` with
  a comment naming the server family it targets, so it is visibly optional.
- Adding a fourth metadata field is a one-line change, but every consumer must
  re-reason about what `null` means for it.
