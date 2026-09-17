# Agency.Acp — Handoff

Written 2026-09-16, one task into execution. **Audience: an agent or human picking this up with no
prior context.** Everything here is traceable to a file in the repo or to a command you can re-run —
where the two conflict, the code wins.

> ## ⚠️ Superseded — all 13 deliverables are complete
>
> This document describes the state **one task in**. It is kept for the reasoning in §5, §7 and §8,
> which still holds. Everything in §3 ("current state"), §6 ("what is next") and §9 is now stale.
>
> **Read [`Agency.Acp-ProjectPlan.md` → *Execution record*](Agency.Acp-ProjectPlan.md#execution-record--completed-2026-09-16) instead** for what was actually built, the eleven places
> the plan was wrong, and the defects that no spec reading would have surfaced.
>
> **Current state:** D0–D12 implemented; **entire suite — unit and functional — 24 assemblies,
> 1,896 tests, 0 failures, 0 build warnings.** **Still uncommitted on `main`.**
>
> **Run the whole suite, not just `Category!=Functional`.** The unit suite was green after every
> deliverable and still hid a `NullReferenceException` in the agent loop, because `Mock<IChatClient>`
> returns `null` from an un-stubbed `GetStreamingResponseAsync` while hand-written fakes do not.
>
> §5's prediction — *"expect more of these"* — was correct: **eleven** plan tasks turned out to be
> wrong or unimplementable as written, including one that could not compile at all (CS1626).

---

## 1. What this is, in one paragraph

Agency.NET is growing an **ACP (Agent Client Protocol) agent**: a process that speaks
newline-delimited JSON-RPC 2.0 over stdio so any ACP client can drive an Agency agent without
linking against it. The immediate consumer is **Agency.Huddle** (`E:\Repos\Huddle`), a multi-agent
Persona chat app that today drives Anthropic's `claude-agent-acp` Node adapter and wants to point
the same unchanged client at Agency.NET. The design is fully negotiated and agreed with that team.

**Nothing is committed.** All work is uncommitted on `main`.

## 2. Read these, in this order

| # | File | Why |
|---|---|---|
| 1 | `docs/specs/Agency.Acp-Specifications.md` | The design. 16 sections, ~6.4k words. **Authoritative** — when anything disagrees with it, raise the conflict rather than guessing (CLAUDE.md §1). |
| 2 | `docs/specs/Agency.Acp-ProjectPlan.md` | The spec decomposed into 55 test-first tasks across 13 deliverables. This is the work queue. |
| 3 | `PRIVATE/Huddle/01-` … `10-` | The negotiation that produced both. Gitignored. Read only if you need the *why* behind a decision — the spec carries the conclusions. |

`PRIVATE/Huddle-Handoff.md` describes the **shelved** in-process Huddle (branch
`feat/huddle-phase1`, PR #216). That branch is **superseded and being retired** — this ACP work
replaces it. Do not build on it; do mine it for code worth re-landing (see §6).

## 3. Current state of the working tree

```text
 M src/Harness/Agency.Harness/PublicAPI.Unshipped.txt
 M src/Harness/Agency.Harness/Tools/McpClientOptions.cs
 M src/Harness/Agency.Harness/Tools/McpClientPool.cs
?? src/Harness/Agency.Harness.Test/McpServerConfigHeadersTests.cs
?? docs/specs/Agency.Acp-ProjectPlan.md
?? docs/specs/Agency.Acp-Specifications.md
```

**Task 0.1 (t + i) is complete.** It adds `McpServerConfig.Headers` and forwards it to the MCP
client's `AdditionalHeaders` — the single item that blocked the whole integration, because Huddle's
tool server authenticates with a per-session bearer token and returns `401` without it.

Also in that change, and **not** in the original plan: `McpClientPool`'s transport-options
construction is extracted into two `internal static` pure builders,
`BuildHttpTransportOptions` and `BuildStdioTransportOptions`. See §5 for why.

### ⚠️ One thing to verify before trusting the above

The post-change regression run exited **0 with zero failures**, but the command piped through
`head -20` and the captured log contains **no line for `Agency.Harness.Test.dll`**. The suite almost
certainly ran and passed — but that is inference, not evidence.

**First action for whoever picks this up:**

```bash
cd E:/Repos/Agency/src
dotnet test Agency.slnx -c Release --filter "Category!=Functional" --nologo
```

Expect **`Agency.Harness.Test` 637 passed** and **`Agency.Harness.Console.Test` 144 passed**. Those
are the pre-change baseline numbers, measured this session, and they match the tripwire counts in
`PRIVATE/Huddle-Handoff.md` — so nothing had drifted before this work started. Do not pipe the
output through `head`.

## 4. Working agreements in force

Decided with the repo owner this session. These override defaults, not CLAUDE.md.

| Decision | Value |
|---|---|
| **Branch** | Work directly on `main`. Not a worktree, despite four existing ones under `.claude/worktrees/`. |
| **Commits** | **Do not commit unless explicitly asked.** Nothing is committed yet. |
| **Starting point** | D0 first, to avoid leaving the half-finished `Headers` change behind. |
| **Provider neutrality** | **Target any local server speaking OpenAI-style or Claude-style HTTP.** Not LM Studio specifically. This is Spec **P2** and it is load-bearing — see §7. |

## 5. The pattern that will repeat: the plan has flaws that surface on contact

Task 0.1.t as written said to assert that `CreateTransport` "produces an `HttpClientTransport` whose
options carry that header". **That is impossible** — `HttpClientTransport` exposes only `Name`, so
the options it was constructed with cannot be read back.

The fix was to extract the options construction into pure functions and assert on those, which is a
better seam anyway. **Task 0.1.t in the plan has been corrected to match.**

**Expect more of these.** The plan was written from the spec, not from compiling against the SDK.
When a task turns out to be unimplementable as written:

1. Find the seam that *is* testable — usually a pure function.
2. Implement it.
3. **Correct the task in the plan**, so the next reader does not rediscover it.

### A second pattern worth copying

The `Headers` implementation landed **before** its test, which is a TDD violation. A test that has
never been red proves nothing. The recovery was:

1. Write the test → it passed immediately (proving nothing).
2. **Temporarily delete the `AdditionalHeaders = server.Headers` line** → confirm exactly **one**
   test went red, and the right one. The other three correctly stayed green, because they assert
   `Name`, `Endpoint`, the null case and the Stdio path, none of which that line affects.
3. Restore → confirm green.

Do this whenever a test is written after its implementation. A green bar on a test that has never
failed is not evidence.

## 6. What is next

**Immediately:** Task 0.2.t / 0.2.i — the last of D0. Today a **missing bearer token reports as
`404`, not `401`**, because the MCP SDK's `AutoDetect` reads the `401` as "this server does not
speak Streamable HTTP", falls back to SSE, issues `GET /mcp`, hits a POST-only route, and surfaces
*that* failure. Verified live against a replica of Huddle's server. Spec **§12 (E-5)**, **§6.9
(D-2)**.

**After that**, six deliverables have no dependencies and 22 of 55 tasks need no LLM endpoint at
all. The critical path is **D2 → D7 → D8 → D9 → D10 → D12**.

**D2 (streaming) is the long pole and the main regression gate.** It rewires `Agent.cs:712` — the
single call the tool loop, usage extraction, `FinishReason` check and every stop condition hang off.
Its acceptance criterion is a *negative*: the entire existing suite must stay green. It is the only
task in the plan like that, which is why §3's baseline matters.

**To re-land from the shelved branch** (`feat/huddle-phase1`), per Spec §6.9 and §16:
`QueryContext.IdentityPrompt` (Task 3.1.i), `Agency.Harness.Markdown.FrontmatterParser`,
`Agency.Harness.IO.DebouncedFileWatcher`, and `InferenceGate` / `GatedChatClient` (§6.10).

## 7. Things that will bite you

**Provider neutrality is not a preference.** Spec **P2** forbids any feature being load-bearing on a
vendor-specific endpoint. Richer model metadata — kind, residency, context length — is *enrichment*
where a server answers and `null` everywhere else, and `null` must render as **absent, never as a
claim** (**P3**). An earlier draft sourced the model catalogue from LM Studio's native
`/api/v0/models`; that was corrected because Ollama and generic OpenAI-compatible servers do not
have it.

**`session/close` never arrives.** Huddle's client never sends it — verified in their source
(`DotAcpAgentSession.DisposeAsync` completes a local channel and makes no wire call). Disposal must
be driven by **`session/close` *or* transport disconnect *or* process shutdown, whichever comes
first**, and must be idempotent. Spec **§6.3**, **§12 (E-15)**, plan Task 7.1.t(f).

**The five locks are a guarantee with a test, not a default.** Spec §6.5 and plan Task 10.1.t. The
fifth — *no hook can return `Ask`* — is **behavioural, not structural**, because you cannot
statically prove a delegate never returns `Ask`, and an evaluator `Allow` does not clear a hook
`Ask`. Do not "simplify" it into a structural check; it will pass while the path stays open.

**Public API changes fail the build unless tracked.** `PublicApiAnalyzers` is a
`GlobalPackageReference` on every non-test project. New public members need an entry in that
project's `PublicAPI.Unshipped.txt`. Three tasks in the plan carry explicit back-compat traps —
`AgentResultStatus.Truncated` must be *appended*, `ToolInvokedEvent.CallId` must be an appended
member, and `Model`'s new fields must be optional.

**`TotalCostUsd` is always `0m`.** No price table exists anywhere in the solution — the only
assignment is in a unit test. Every USD budget guard is inert, including the memory consolidator's
`MaxCostUsd = 0.50`. Do not build on it.

## 8. The machine

This box hosts the LLM endpoint. Quirks that have already cost time this session:

- **Concurrency ceiling is 2.** More than two concurrent inferences crashes the AMD 8060S iGPU.
  Never run two LLM-touching jobs at once. This cost several failed probes.
- **Models are never evicted.** `jitModelTTL.enabled = False` with auto-loading **on**, so requested
  models accumulate in VRAM until exhaustion. The failure mode is *loading*, not inferring — no
  concurrency gate prevents it.
- **`alwaysAllowLoadAnyway = True`** means the one guardrail that could refuse an oversized load is
  configured to be overruled. The owner agreed to change this; **it has not been applied**.
- **Ask before running inference.** The owner asked for this explicitly after a saturation incident.
  Two measurements are cleared but queued, waiting on a resident model — see §9.

Functional tests need `-- RunConfiguration.MaxCpuCount=1`, or concurrent test assemblies crash the GPU.

## 9. Open items and who owns them

| # | Item | Owner | Blocks |
|---|---|---|---|
| 0.1 | `jitModelTTL.enabled = True` | box owner | Huddle's model-picker decision, which is **reversed conditional on this and not yet in force** |
| 0.2 | `alwaysAllowLoadAnyway = False` | box owner | the remaining crash path; agreed two rounds ago, not applied |
| 0.3 | `numParallelSessions = 2` catalogue-wide | box owner | `qwen/qwen3-coder-next` is at **3**, above the safe ceiling |
| 0.4 | Load any model → run **O1b** (queue-vs-reject) + loaded-context answer | box owner | the gate's queue-vs-stall design |
| 0.5 | Load a reasoning model → run **O3** (`enable_thinking`) | box owner | the effort ladder's shape (Phase 4) |
| — | `session/close` on dispose; tool-name prefix; host profiles; per-host catalogue | Huddle | the joint run only |

**The O1b probe is written and parameterised** at
`…/scratchpad/o1b-queue-or-reject.sh` (session-scoped; recreate from Spec §6.10 if gone). It
deliberately **refuses to run when no model is resident** rather than triggering a load, because
causing a load is the expensive act.

**None of this blocks Phase 1 or Phase 2.**

## 10. The milestone is joint, and neither plan contains it

Plan Task 12.1 proves four properties at the protocol boundary — deltas before the terminal
response, an authenticated MCP call, clean cancel, no filesystem or shell tools. The other three —
live chunk rendering in the Room view, two Personas in one Room, a Stop click leaving both
resumable — live in Huddle's repository.

**If Task 12.1 goes green, the ACP surface is proven and the product is not.** See *The joint run*
in the project plan for the split and the four client-side prerequisites.
