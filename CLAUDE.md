# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 1. Think Before Coding

**Don't assume. Don't hide confusion. Surface tradeoffs.**

Before implementing:

- State your assumptions explicitly. If uncertain, ask.
- If multiple interpretations exist, present them - don't pick silently.
- If a simpler approach exists, say so. Push back when warranted.
- If something is unclear, stop. Name what's confusing. Ask.

## 2. Simplicity First

**Minimum code that solves the problem. Nothing speculative.**

- No features beyond what was asked.
- No abstractions for single-use code.
- No "flexibility" or "configurability" that wasn't requested.
- No error handling for impossible scenarios.
- If you write 200 lines and it could be 50, rewrite it.

Ask yourself: "Would a senior engineer say this is overcomplicated?" If yes, simplify.

## 3. Surgical Changes

**Touch only what you must. Clean up only your own mess.**

When editing existing code:

- Don't "improve" adjacent code, comments, or formatting.
- Don't refactor things that aren't broken.
- Match existing style, even if you'd do it differently.
- If you notice unrelated dead code, mention it - don't delete it.

When your changes create orphans:

- Remove imports/variables/functions that YOUR changes made unused.
- Don't remove pre-existing dead code unless asked.

The test: Every changed line should trace directly to the user's request.

## 4. Goal-Driven Execution

**Define success criteria. Loop until verified.**

Transform tasks into verifiable goals:

- "Add validation" → "Write tests for invalid inputs, then make them pass"
- "Fix the bug" → "Write a test that reproduces it, then make it pass"
- "Refactor X" → "Ensure tests pass before and after"

For multi-step tasks, state a brief plan:

```Text
1. [Step] → verify: [check]
2. [Step] → verify: [check]
3. [Step] → verify: [check]
```

Strong success criteria let you loop independently. Weak criteria ("make it work") require constant clarification.

---

### Infrastructure

- PostgreSQL 18 + pgvector extension via Docker (`src/docker-compose.yml`)
- Credentials: `dev_user` / `dev_password`, database: `dev_db`, port `5432`
- Functional LLM tests target LM Studio at `http://llm-host.example:1234`

## Reference Docs — Load When Relevant

Keep this file lean. Read the linked doc only when its trigger applies:

- **Editor configuration** → read `@.editorconfig` (coding style and formatting rules) (look at parent folder for `.editorconfig` files).
- **Writing or reviewing C# code** → read `Agents/CSharpPrinciples.md` (conventions, design principles, build config & centralized package management).
- **Understanding or changing the system** → read `Agents/Architecture.md` (project dependency graph, `ILlmClient`, observability pattern, `SQLQueryEmbedder`, infrastructure).
- **Building, testing, or running infrastructure** → read `Agents/BuildAndTest.md` (dotnet/docker commands, test configuration).
- **Investigating a CI failure or changing the pipeline** → read `Agents/CIPipeline.md` (Gitea Actions workflows, offline functional-test cache proxy, known failure modes, debugging playbook).
- **Discussing or updating bugs or tasks** → read `Agents/Trackers.md` (Obsidian boards + task template locations).

## Git & Auth

- Git push may fail due to credential/auth issues on this machine. If push fails, inform the user rather than retrying endlessly.
- This repo uses scoped Git credentials via includeIf directives
- **Do not commit changes unless explicitly asked.** Make changes and ask before committing to ensure the user can review.

## Debugging Approach

- When diagnosing bugs, read the actual error and failing code carefully before suggesting fixes. Do NOT guess at the root cause — verify with evidence first.
- When user says an approach is wrong, pivot immediately rather than doubling down.
- **After each debugging session, offer to run a reflection** and capture the learnings (root cause, the decisive proof technique, and the fix) into the relevant `Agents/*.md` doc — e.g. CI failures go in `Agents/CIPipeline.md` — so future agents skip the trial-and-error loop.