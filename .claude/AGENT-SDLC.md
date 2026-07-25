# Agent-driven SDLC — operator guide

A specification moves through role-owned passes, becomes a backlog of PBIs, and
each PBI moves through a TDD cycle. Roles are Claude Code **subagents**
(`.claude/agents/`). The **main session is the project manager**: it runs
`/next`, which dispatches subagents and records every transition to git. Git is
the state bus — the whole process is resumable across context resets because all
state lives in frontmatter and commits, never in chat.

You (the human PM) author specs, answer questions, and sign off. Everything else
is an agent.

## The pieces
| Path | What it is |
|---|---|
| `.claude/agents/*.md` | the six roles: spec-qa, spec-architect, planner, test-author, implementer, code-reviewer |
| `.claude/commands/next.md` | `/next` — advance exactly one transition |
| `.claude/commands/sync-ado.md` | `/sync-ado` — one-way projection to Azure DevOps Boards |
| `.claude/scripts/red-sha-gate.sh` | the TDD integrity gate (`git diff` against `red_sha`) |
| `.claude/scripts/sync-ado.sh` | the ADO projection (dry-run by default) |
| `.claude/ado.config.json` | org/project + state maps for the projection |
| `.claude/templates/` | `spec.md`, `open-questions.md`, `PBI.md` starting points |
| `docs/specs/<slug>/` | `spec.md` + `open-questions.md` per feature |
| `backlog/<slug>/PBI-NNN.md` | one PBI per unit of user-visible value |

## Lifecycle at a glance
**Spec:** `DRAFT → QA_1 → ARCH → QA_2 → AWAITING_SIGNOFF → SIGNED_OFF → PLANNED`
(with an optional `BLOCKED_ON_PM` off `ARCH` when a spec is wholly non-viable).
Owners: `DRAFT`, `AWAITING_SIGNOFF`, `BLOCKED_ON_PM` are **yours**; `QA_1`/`QA_2`
are spec-qa; `ARCH` is spec-architect; `SIGNED_OFF` is planner.

**PBI:** `READY → RED_PROVEN → GREEN_DONE → DONE`, with `REWORK` looping back to
the implementer and `BLOCKED` waiting on you. Owners: `READY` test-author,
`RED_PROVEN`/`REWORK` implementer, `GREEN_DONE` code-reviewer (behind the RED-SHA
gate).

## How you actually use it
1. **Start a feature.** Copy `.claude/templates/spec.md` to
   `docs/specs/<slug>/spec.md` and `open-questions.md` alongside it. Write the
   `## Requirements`. Set `state: QA_1` and commit.
2. **Run `/next`.** It does one transition and stops. Run it again to do the
   next. QA_1 → ARCH → QA_2 run unattended; questions accumulate in
   `open-questions.md` without halting.
3. **Sign off once.** When `/next` reports `AWAITING_SIGNOFF`, answer every
   question in `open-questions.md`, set `state: SIGNED_OFF`, commit. That single
   sitting is the one interruption per spec.
4. **Let it build.** `/next` runs planner (→ PBIs), then per PBI: test-author
   (RED) → implementer (GREEN) → code-reviewer (DONE/REWORK). Each is one `/next`.
5. **Project to the board (optional).** `bash .claude/scripts/sync-ado.sh` for a
   dry run; `--apply` once `ado.config.json` is filled. Never gates the SDLC.

## The rules with teeth
- **Questions must be letter-answerable** — options + a recommendation + a stated
  default, or the agent shouldn't have emitted it. Reject any that isn't.
- **RED must be a real assertion failure**, not a compile error or
  `NotImplementedException`, and the test commit's SHA is recorded as `red_sha`.
- **The implementer never touches tests.** `/next` proves it with
  `red-sha-gate.sh` before review; any test change after `red_sha` is an
  automatic `REWORK`. If the implementer thinks a test is wrong, the legal move
  is `BLOCKED` + a question — never editing the test.
- **Only code-reviewer's verdict moves a PBI to `DONE`.** This is convention, not
  a hard constraint (see weaknesses below).
- **Length budgets:** spec ≤ ~1,200 words, technical design ≤ ~800. Unread
  Opus prose that gets signed off is worse than no review.

## Interpretations of the handoff — confirm or correct these
The handoff was ambiguous in three spots. The scaffolding picked a reading;
these are the first things to sanity-check.

1. **`AWAITING_SIGNOFF` is a state I added.** The handoff shows `QA_2 →
   SIGNED_OFF`, but a human — not spec-qa — performs sign-off. So spec-qa's QA_2
   pass leaves the spec in `AWAITING_SIGNOFF` (the halt), and you set
   `SIGNED_OFF` by commit. If you'd rather spec-qa leave it directly in a state
   you flip, say so.
2. **`BLOCKED_ON_PM` is optional, off `ARCH` only.** The handoff diagram places
   it before `QA_2`, but §3.3 says QA_1→ARCH→QA_2 run unattended with one human
   sitting. Reconciled: the architect only enters `BLOCKED_ON_PM` when a spec is
   *wholly* non-viable; ordinary ambiguous requirements go to `open-questions.md`
   and don't halt. Normal path never touches `BLOCKED_ON_PM`.
3. **`/next` favors throughput over strict scan order.** The handoff says "first
   pending transition; if PM-owned, print and exit." Taken literally, a human
   blocker early in scan order would stall available agent work. `/next` instead
   does the first *actionable agent-owned* transition and reports human blockers
   only when no agent work remains — consistent with "halts, does not wait; no
   idling agents."

## Known weaknesses — accepted, not solved
- **Vacuous tests.** A weak assertion that the implementer passes honestly yields
  a clean diff and a met criterion. RED_PROVEN (assertion-failure requirement)
  and the reviewer's honesty check help at the margin; the real fix is mutation
  testing (Stryker.NET on changed files, wired into the review gate) — deferred.
- **"Only the reviewer closes a PBI" is enforced by convention.** `/next` is a
  model reading markdown; `git log` catches drift after the fact.
- **No in-flight guardrail.** A runaway implementer burns a full
  implement→review cycle before rejection. Tax is proportional to PBI size — keep
  PBIs small.

## Scope of this build
Tier 2 (full ceremony) only, per the current decision. Tiers 0 (trivial →
straight to implement) and 1 (spec-lite) are not wired yet; add them as a `tier:`
branch in `/next` when needed. ADO parent/child links and per-PBI child Tasks are
deferred extensions to the projection.
