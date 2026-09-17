# 3. ACP as the integration seam

## Status

Accepted

## Context

Agency.Huddle — a multi-agent Persona chat app — needed to drive Agency.NET
agents. It already drives Anthropic's `claude-agent-acp` Node adapter and
wanted to point the same unchanged client at Agency.

Two shapes were available:

- **An in-process library seam.** Huddle references `Agency.Harness` directly
  and calls `ChatSession` in-process. Cheapest to build; no protocol server to
  write.
- **A protocol server.** A separate process speaking the
  [Agent Client Protocol](https://agentclientprotocol.com) — newline-delimited
  JSON-RPC 2.0 over stdio.

The argument against the protocol server was concrete: *there is no C# ACP
library*, so it meant hand-rolling wire types and converters. That objection
was retired by the `dotacp` packages, which are side-neutral (they carry both
sides' method tables) and whose converters already solve the union
discrimination and enum spellings that default serializer settings get wrong.

Two arguments for it proved decisive:

- **Fault isolation.** These agents run against a *local* inference server on
  a box with a hard concurrency ceiling, where a bad request can hang or crash
  the GPU. In-process, a crashed local model can take the client's web app
  down with it. Out-of-process, it cannot.
- **Reach.** ACP lets Agency be driven by anything that speaks it — Zed, buzz,
  a future tool — not just by one client. For a project whose goal is adoption,
  a standard protocol surface is worth more than a bespoke library seam.

## Decision

Expose the harness as an ACP agent: a single executable, `Agency.Acp`,
launched by path, speaking JSON-RPC 2.0 over stdio.

It is a **protocol translator, not a second agent loop**. Every behaviour the
harness already has is reached through `ChatSession`; the adapter adds no agent
logic. Where ACP needs something the harness cannot express, the fix lands in
the harness as a first-class feature, never as a shadow implementation in the
adapter.

The adapter takes its wire types from `dotacp.protocol` rather than
hand-rolling DTOs, which makes wire compatibility *structural* rather than a
matter of reading the client's source correctly.

## Consequences

- A crashed or wedged local model cannot take a client process down.
- Any ACP client can drive Agency, not only the one that motivated this.
- A protocol server now exists that did not before: transport, dispatcher,
  session registry, turn driver, event translator — plus the failure modes that
  come with stdio framing. The most likely defect class is a stray write to
  stdout corrupting the stream, which is mitigated by routing every log sink
  to stderr and nulling `Console.Out`, and asserted by a test.
- Wire compatibility is pinned to a third-party package version
  (`dotacp.protocol 2026.7.19`). Its shape is authoritative over the design
  spec where the two disagree — several such divergences were found during
  implementation and are recorded in
  [`docs/Projects/Agency.Acp.md`](../Projects/Agency.Acp.md).
- Cross-process means serialization cost per event and no shared object
  identity. Acceptable: the dominant cost is inference, and event translation
  is allocation-only.
- Because it is a separate process, harness features reach ACP clients only
  when the adapter translates them. New `AgentEvent` types need an explicit
  mapping or they are silently invisible on the wire.
