# Agency.Agentic Hooks — Implementation Tracker

**Spec:** `docs/Agency.Agentic.Hooks.md`
**Plan:** See plan output in conversation (21 tasks across 5 deliverables)
**Final gate:** `dotnet test src/Agency.slnx --filter "Category!=Functional"` — all green, zero warnings

---

## Kanban Board

| **Deliverable / Task** | **Active** | **In Progress** | **Done** |
|---|:---:|:---:|:---:|
| **D1. Primitive Types** | | | |
| Task 1 — Tests: `PreToolUseDecision` semantics (5 cases) | | | ✔ |
| Task 2 — Impl: `PreToolUseDecision` discriminated union | | | ✔ |
| Task 3 — Tests: Hook context record construction (5 cases) | | | ✔ |
| Task 4 — Impl: Hook context records (`HookContexts.cs`) | | | ✔ |
| **D2. `AgentHooks` Container** | | | |
| Task 5 — Tests: `AgentHooks` defaults and nullable contract (5 cases) | | | ✔ |
| Task 6 — Impl: `AgentHooks` record | | | ✔ |
| **D3. Hook Composition** | | | |
| Task 7 — Tests: `Compose()` merge semantics (10 cases) | | | ✔ |
| Task 8 — Impl: `AgentHooksExtensions.Compose()` | | | ✔ |
| **D4. Agent Integration** | | | |
| Task 9 — Tests: `Agent` constructor backward compatibility (4 cases) | | | ✔ |
| Task 10 — Impl: Add `AgentHooks?` parameter to `Agent` ctor | | | ✔ |
| Task 11 — Tests: `OnSessionStarted` fires exactly once (5 cases) | | | ✔ |
| Task 12 — Impl: Wire `OnSessionStarted` in `RunAsync` | | | ✔ |
| Task 13 — Tests: `OnPreToolUse` Allow/Deny/Rewrite + `OnPostToolUse` (10 cases) | | | ✔ |
| Task 14 — Tests: `OnAssistantTurn` fires after each LLM response (5 cases) | | | ✔ |
| Task 15 — Impl: Wire `OnPreToolUse`, `OnPostToolUse`, `OnAssistantTurn` | | | ✔ |
| Task 16 — Tests: `OnStop` fires before `AgentResultEvent` (6 cases) | | | ✔ |
| Task 17 — Impl: Wire `OnStop` in `RunAsync` | | | ✔ |
| **D5. Built-in Hook Recipes** | | | |
| Task 18 — Tests: `BlockListHooks` dangerous patterns (6 cases) | | | ✔ |
| Task 19 — Impl: `BlockListHooks` | | | ✔ |
| Task 20 — Tests: `AuditHooks` capture behaviour (5 cases) | | | ✔ |
| Task 21 — Impl: `AuditHooks` | | | ✔ |

---

## Progress Summary

| Deliverable | Tasks | Done | Remaining |
|---|:---:|:---:|:---:|
| D1. Primitive Types | 4 | 4 | 0 |
| D2. `AgentHooks` Container | 2 | 2 | 0 |
| D3. Hook Composition | 2 | 2 | 0 |
| D4. Agent Integration | 9 | 9 | 0 |
| D5. Built-in Hook Recipes | 4 | 4 | 0 |
| **Total** | **21** | **21** | **0** |

---

## TDD Dependency Chain

```
Task 1 (Test) ──► Task 2 (Impl)
Task 3 (Test) ──► Task 4 (Impl)
Task 5 (Test) ──► Task 6 (Impl)
Task 7 (Test) ──► Task 8 (Impl)
Task 9 (Test) ──► Task 10 (Impl)
Task 11 (Test) ─┐
Task 12 (Impl)  ◄─ requires Task 10 done
Task 13 (Test) ─┐
Task 14 (Test) ─┤
Task 15 (Impl)  ◄─ requires Tasks 13 + 14 done, and Task 10 done
Task 16 (Test) ─┐
Task 17 (Impl)  ◄─ requires Task 16 done, and Task 10 done
Task 18 (Test) ──► Task 19 (Impl) ◄─ requires Task 6 done
Task 20 (Test) ──► Task 21 (Impl) ◄─ requires Task 6 done
```

All D4 and D5 tasks require D1 + D2 to be complete before any implementation task can be marked **Done**.

---

## New Files Produced

| File | Task | Status |
|---|---|:---:|
| `src/Agentic/Agency.Agentic.Test/Hooks/PreToolUseDecisionTests.cs` | Task 1 | — |
| `src/Agentic/Agency.Agentic/Hooks/PreToolUseDecision.cs` | Task 2 | — |
| `src/Agentic/Agency.Agentic.Test/Hooks/HookContextTests.cs` | Task 3 | — |
| `src/Agentic/Agency.Agentic/Hooks/HookContexts.cs` | Task 4 | — |
| `src/Agentic/Agency.Agentic.Test/Hooks/AgentHooksTests.cs` | Task 5 | — |
| `src/Agentic/Agency.Agentic/Hooks/AgentHooks.cs` | Task 6 | — |
| `src/Agentic/Agency.Agentic.Test/Hooks/AgentHooksExtensionsTests.cs` | Task 7 | — |
| `src/Agentic/Agency.Agentic/Hooks/AgentHooksExtensions.cs` | Task 8 | — |
| `src/Agentic/Agency.Agentic.Test/Hooks/AgentHooksConstructorTests.cs` | Task 9 | — |
| *(edit)* `src/Agentic/Agency.Agentic/Agent.cs` | Tasks 10, 12, 15, 17 | — |
| `src/Agentic/Agency.Agentic.Test/Hooks/AgentOnSessionStartedHookTests.cs` | Task 11 | — |
| `src/Agentic/Agency.Agentic.Test/Hooks/AgentToolHookTests.cs` | Task 13 | — |
| `src/Agentic/Agency.Agentic.Test/Hooks/AgentAssistantTurnHookTests.cs` | Task 14 | — |
| `src/Agentic/Agency.Agentic.Test/Hooks/AgentStopHookTests.cs` | Task 16 | — |
| `src/Agentic/Agency.Agentic.Test/Hooks/BlockListHooksTests.cs` | Task 18 | — |
| `src/Agentic/Agency.Agentic/Hooks/BlockListHooks.cs` | Task 19 | — |
| `src/Agentic/Agency.Agentic.Test/Hooks/AuditHooksTests.cs` | Task 20 | — |
| `src/Agentic/Agency.Agentic/Hooks/AuditHooks.cs` | Task 21 | — |
