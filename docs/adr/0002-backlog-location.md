# 2. Backlog location: git as state bus, Azure DevOps as projection

## Status

Accepted

## Context

The agent-driven SDLC (see `.claude/AGENT-SDLC.md` and the design handoff)
routes work through role-owned subagents whose only durable state lives on disk
and in git. `/next` reads `state:` frontmatter across `docs/specs/**` and
`backlog/**` to pick the next transition, enforces a `git diff`-based TDD
integrity gate, and records every transition as a commit. The handoff's
load-bearing claim: *"Git is the state bus. That is what makes this resumable
across context resets, which is the actual hard problem."*

The handoff left backlog location open (its §8.2): file-based `backlog/**`,
GitHub Issues, or the existing tracker. We additionally considered **Azure
DevOps Boards** as the system of record, since the repo already has an
`azure-devops-boards` skill and the PM wants a real board.

Making ADO the system of record was rejected for three reasons, all grounded in
the methodology's own design:

- **The RED-SHA integrity gate is intrinsically git.** `git diff --exit-code
  <red_sha> -- <tests>` cannot move to ADO. If ADO held PBI state, we would run
  two state systems — git for the SHA gate and the code, ADO for work-item
  status — with `red_sha` demoted to a custom field requiring reconciliation.
- **Atomicity.** A file transition advances `state:` and, where relevant, the
  code in one revertable commit. Splitting state into ADO makes each transition
  two writes that can half-fail (a PBI "DONE" in ADO whose commit never landed).
- **Subagent surface.** `code-reviewer` runs on a deliberately starved context.
  ADO-as-record forces every subagent to carry a PAT and `az` config just to
  read state — more to fail, no benefit to the work.

A separate concern regardless of direction: the methodology's state machines are
custom and granular (`DRAFT→QA_1→ARCH→…`, `READY→RED_PROVEN→GREEN_DONE→DONE`).
ADO's built-in process states are coarse and won't hold them without a custom
process template. Files hold arbitrary frontmatter for free.

## Decision

**Git is the system of record. Azure DevOps Boards is a one-way, display-only
projection.**

- PBIs remain `backlog/<slug>/PBI-NNN.md`; specs remain `docs/specs/<slug>/`.
  `/next` reads and commits git for every transition.
- `/sync-ado` (`.claude/scripts/sync-ado.sh`) pushes current state to Boards for
  the human PM's visibility: spec→Feature, PBI→User Story, methodology state →
  coarse ADO state via `.claude/ado.config.json`, with the exact state preserved
  as a `sdlc:<STATE>` tag. It is dry-run by default and never gates `/next`.
- The projection is strictly git→ADO. Board edits do not flow back.

## Consequences

- Resumability, the SHA gate, and commit-atomic transitions are all preserved;
  the SDLC runs with ADO offline.
- The PM gets a real board, at the cost of it being a mirror, not a control
  surface — driving the process from ADO is intentionally unsupported.
- Boards setup is required before first `--apply` (org/project, state maps, a
  Work-Items-scoped PAT). Parent/child linking and per-PBI child Tasks for the
  RED/GREEN/REVIEW triple are deferred extensions, not built in the first pass.
- Building the scaffolding required three interpretation calls where the handoff
  was ambiguous (state names around sign-off, the `BLOCKED_ON_PM` node, and the
  throughput rule in `/next`); these are documented in `.claude/AGENT-SDLC.md`
  and are the first things to confirm or correct.
