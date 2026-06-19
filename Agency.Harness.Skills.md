# Agency.Harness — Skills

#skills #progressive-disclosure #tools #system-prompt #design

## What It Is

Skills bring the [Claude Code Skills](https://code.claude.com/docs/en/skills) model
(an instance of the [Agent Skills](https://agentskills.io) open standard) to
`Agency.Harness`. A **skill** is a directory containing a `SKILL.md` file: YAML
frontmatter (`name`, `description`, …) plus a markdown body of instructions. Skills
are discovered from disk at startup; only their `name` + `description` cost context
up-front, and the full body loads into the conversation **on demand** when the agent
(or the user) invokes the skill.

This is the same *progressive disclosure* principle already implemented for tool
schemas by `ProgressiveDiscoveryToolRegistry` + `ToolHelpTool`. Skills reuse that
philosophy at the prompt level.

**Namespace:** `Agency.Harness.Skills`

## Design Principle — Skills Are a Composition, Not a New Loop Concept

A skill decomposes into two mechanisms the harness already has:

| Skill aspect            | Existing mechanism it reuses                                                            |
| :---------------------- | :-------------------------------------------------------------------------------------- |
| Description in context  | `SystemPromptBuilder` section (like `## Knowledge`, like the `IProgressiveDiscovery` hint) |
| Body loaded on invoke   | A meta-tool returning a `ToolResult` (exactly like `ToolHelpTool` / `tool_help`)        |
| "Enters conversation as a single message" | The `Tool`-role result message appended in `Agent.RunIterationsAsync`         |
| `!`-shell injection      | `ExecutePowershellTool`                                                                  |
| `context: fork`          | `AgentTool` (subagent delegation)                                                        |
| `allowed-tools`          | The `Permissions` evaluator + park/resume                                                |

**Consequence: Phase 1 (MVP) needs only a minimal, additive `Agent.cs` touch.** Skills are
delivered by `SystemPromptBuilder` (catalog injection) + a new `SkillTool : ITool`
(body loading). The one unavoidable `Agent.cs` change is an optional `skills` parameter on
`Agent.CreateContext` (the single place `init`-only contexts like `Tools`/`Environment`/`User`
are composed) — it mirrors the existing parameters exactly and defaults to `SkillContext.Empty`,
so it is backward-compatible. Everything else is filesystem parsing and Console wiring.

## Architecture

```
                    startup (Console / host)
                           │
                  SkillLoader.Load(roots)        ── scans .agency/skills/<name>/SKILL.md
                           │                         parses frontmatter + body
                           ▼
                     SkillCatalog  ──────────────►  Context.Skills (SkillContext)
                    (ISkillCatalog)                       │
                           │                              ▼
                           │                  SystemPromptBuilder.Build
                           │                  renders "## Skills" (name + description,
                           │                  model-invocable only)
                           ▼
                      SkillTool ("skill")  ── registered in ToolRegistry
                           │                  input: { name, arguments? }
                           ▼
                     SkillRenderer  ── argument substitution (+ Phase 2: ! shell injection)
                           │
                           ▼
                     ToolResult(body)  ── appended as Tool message → in conversation
```

### Discovery roots (precedence high → low)

it is configurable from `appsettings.json` (`Skills:Directories`), but defaults to two standard locations:

| Scope     | Path                                       |
| :-------- | :----------------------------------------- |
| Project   | `<cwd>/.agency/skills/<name>/SKILL.md`     |
| Personal  | `~/.agency/skills/<name>/SKILL.md`         |

When two skills share a name, **project overrides personal** (more-specific wins).
Enterprise scope, when added, slots in as the highest-precedence root
(enterprise > project > personal). Precedence is enforced by the *order the caller
passes roots* — `SkillLoader` simply keeps the first occurrence of each name. The
directory name is the **canonical skill name and invocation key**; a frontmatter
`name`, if present, is cosmetic only and is not used for resolution.

### Frontmatter fields (MVP subset)

| Field                      | MVP? | Notes                                                                 |
| :------------------------- | :--- | :-------------------------------------------------------------------- |
| `description`              | ✅   | Drives model selection; falls back to first body paragraph if absent. |
| `name`                     | ✅   | Display label; defaults to directory name.                            |
| `when_to_use`              | ✅   | Appended to description in the catalog listing.                       |
| `disable-model-invocation` | ✅   | When `true`: omitted from the system-prompt catalog and refused by `SkillTool`. |
| `user-invocable`           | ✅   | When `false`: hidden from the Console `/` menu (Phase 2), still listed to model. |
| `arguments`                | ✅   | Named positional args for `$name` substitution.                       |
| `argument-hint`            | ⛔️   | Console autocomplete only (Phase 2).                                  |
| `allowed-tools`            | ⛔️   | Permission pre-approval (Phase 2, Task 9).                            |
| `context: fork` / `agent`  | ⛔️   | Subagent execution (Phase 2, Task 10).                               |
| `shell`                    | ⛔️   | `!`-injection shell selection (Phase 2, Task 7).                      |

### String substitutions (Task 4)

`$ARGUMENTS`, `$ARGUMENTS[N]`, `$N`, `$name` (from `arguments`), `${CLAUDE_SKILL_DIR}`,
`${CLAUDE_SESSION_ID}`. Backslash-escaping of `$` per the spec. When a skill body has
no `$ARGUMENTS` placeholder but arguments were passed, append `ARGUMENTS: <value>`.

## Decisions / Tradeoffs to Confirm

1. **YAML parsing — minimal hand-rolled parser vs. YamlDotNet.** Skill frontmatter is
   shallow (scalars + simple lists). A purpose-built ~60-line parser avoids a new NuGet
   dependency and keeps the surface testable, at the cost of not supporting exotic YAML.
   **Recommendation: hand-rolled frontmatter parser** (Simplicity First). Revisit if a
   real skill needs nested YAML.
2. **`SkillTool` lists names where?** The catalog (name + description) goes in the
   **system prompt** (matches the spec: "descriptions are loaded into context"). The
   `skill` tool itself only needs `{ name, arguments? }`; on an unknown name it returns
   an error listing available skills (mirrors `ToolHelpTool`).
3. **No agent-loop changes in Phase 1.** Confirmed above — keep `Agent.cs` untouched.
4. **Shell injection is opt-in and isolated.** `SkillRenderer` stays a pure string
   transform; `!`-injection is layered on in Phase 2 behind an `ISkillShellRunner` so
   the renderer remains trivially unit-testable and the security surface is gated.

---

## Tasks

Each task is independently assignable. Phase 1 ships a working MVP; Phase 2 layers on
advanced features. Verification is TDD-style (`InternalsVisibleTo` the test project per
repo convention — implementation members `internal`, not `public`/`private`).

### Phase 1 — MVP (core, no `Agent.cs` changes)

#### Task 1 — Skill domain model + frontmatter parser
- **New files:** `Skills/Skill.cs`, `Skills/SkillParser.cs`.
- **Do:** `Skill` record (`Name`, `Description`, `WhenToUse`, `Body`, `SkillDir`,
  `DisableModelInvocation`, `UserInvocable`, `Arguments`). `SkillParser.Parse(string text, string skillDir, string dirName)` → `Skill`: split frontmatter (`---` … `---`),
  parse shallow YAML (scalars + space/comma/YAML-list), fall back to first body paragraph
  when `description` is missing. `Name` is always set to `dirName` (the canonical
  invocation key); a frontmatter `name`, if present, is ignored for resolution.
- **Verify:** `SkillParserTests` — frontmatter present/absent, list field in all three
  syntaxes, missing description → first paragraph, boolean defaults.
- **Depends on:** nothing.

#### Task 2 — `ISkillCatalog`, `SkillCatalog`, `SkillLoader` (discovery)
- **New files:** `Skills/ISkillCatalog.cs`, `Skills/SkillCatalog.cs`, `Skills/SkillLoader.cs`.
- **Do:** `ISkillCatalog` { `IReadOnlyList<Skill> List()`, `Skill? Find(string name)` }.
  `SkillLoader.Load(IEnumerable<string> roots)` scans each root for `*/SKILL.md`, parses
  via Task 1, applies name precedence (first occurrence wins; the *caller* orders roots
  project-first per the precedence policy), ignores dirs with no `SKILL.md`.
- **Verify:** `SkillLoaderTests` over a temp directory tree — discovery, precedence
  (project overrides personal when project root is passed first), skips malformed/empty dirs.
- **Depends on:** Task 1.

#### Task 3 — `SkillContext` + `SystemPromptBuilder` catalog rendering
- **New files:** `Contexts/SkillContext.cs`. **Edit:** `Contexts/Context.cs` (add
  `SkillContext Skills { get; init; } = SkillContext.Empty;`), `SystemPromptBuilder.cs`.
- **Do:** `SkillContext` **wraps a live `ISkillCatalog` reference** (defaulting to an
  empty catalog for `SkillContext.Empty`) and surfaces `List()`/`Find(name)` through to
  it — a single source of truth with `SkillTool`, and it makes Phase-2 live reload (Task
  11) free since the catalog can mutate behind the reference. Render a `## Skills` section
  listing `- **<dir-name>** — description (when_to_use)` (the rendered name is the
  canonical directory name = the invocation key) for model-invocable skills only. Add one
  instruction line: *"To use a skill, call the `skill` tool with its name."* No section
  when the catalog is empty.
- **Verify:** `SystemPromptBuilderTests` additions — section present with skills, absent
  when empty, `disable-model-invocation` skills excluded.
- **Depends on:** Task 2 (uses `ISkillCatalog`; can stub the interface to parallelize).

#### Task 4 — `SkillRenderer` (argument substitution)
- **New file:** `Skills/SkillRenderer.cs`.
- **Do:** Pure `Render(Skill skill, string? arguments, string sessionId)` applying the
  substitutions above + escaping + shell-style quoting for indexed args + the
  `ARGUMENTS:` append fallback.
- **Verify:** `SkillRendererTests` — each placeholder, `\$` escaping, quoted multi-word
  indexed args, no-placeholder append.
- **Depends on:** Task 1.

#### Task 5 — `SkillTool` (the `skill` meta-tool)
- **New file:** `Skills/SkillTool.cs` (`ITool`, name `"skill"`).
- **Do:** Input schema `{ name (required), arguments? }`. Resolve via `ISkillCatalog`;
  refuse `disable-model-invocation` skills; render via `SkillRenderer`; return the body as
  a non-error `ToolResult`. Unknown name → error listing available skills (mirror
  `ToolHelpTool`).
- **Verify:** `SkillToolTests` — happy path returns rendered body, unknown name errors,
  disabled skill refused, arguments forwarded to renderer.
- **Depends on:** Tasks 1, 2, 4.

#### Task 6 — Console wiring
- **Edit:** `Agency.Harness.Console/Program.cs`, `appsettings.json`. Plus the minimal
  `skills`-parameter threading through `Agent.cs` (`CreateContext`), `ChatSession.cs`, and
  `Agency.Harness.Console/ConsoleChatSession.cs` (since `Context.Skills` is `init`-only and
  composed in `CreateContext`).
- **Do:** Read `Skills:Directories` (default to `./.agency/skills` + `~/.agency/skills`,
  in that order so **project overrides personal**), `SkillLoader.Load` at startup, build
  `SkillContext` (wrapping the resulting `ISkillCatalog`), register `SkillTool` in the inner
  `ToolRegistry`, and attach `SkillContext` to the `Context`/`ToolContext` composition
  (alongside the existing `ProgressiveDiscoveryToolRegistry` decoration).
- **Verify:** Manual — drop a sample skill under `.claude/skills/`, confirm it appears in
  the `## Skills` section and the agent can invoke it via the `skill` tool. Add a small
  functional test if a deterministic cassette is feasible.
- **Depends on:** Tasks 2, 3, 5.

### Phase 2 — Advanced (each builds on Phase 1, independently assignable)

#### Task 7 — Dynamic `!`-shell injection
- `ISkillShellRunner` + a PowerShell implementation reusing `ExecutePowershellTool`.
  Extend `SkillRenderer` to expand inline `` !`cmd` `` and `` ```! `` fenced blocks
  (one pass, output not re-scanned). Honor `shell: powershell`. Gate behind a
  `disableSkillShellExecution` setting. **Verify:** renderer tests with a fake runner.

#### Task 8 — Console `/skill-name` user-invocable commands
- Surface `user-invocable` skills as `/name` commands in `CommandRegistry` /
  `ConsoleInputReader`, with `argument-hint` autocomplete. Direct invocation renders the
  skill and submits it as the user turn. **Verify:** command-registry tests.

#### Task 9 — `allowed-tools` permission pre-approval
- While a skill is "active" (its body is the most recent skill message), pre-approve its
  `allowed-tools` in the `Permissions` evaluator. Defines skill-active lifetime + clearing
  on next user message. **Verify:** permission-evaluation tests.

#### Task 10 — `context: fork` subagent execution
- When an invoked skill declares `context: fork`, `SkillTool` delegates to `AgentTool`
  plumbing: spawn a subagent whose prompt is the rendered body, optionally using the
  `agent` type. Return the subagent's result. **Verify:** tool test with a fake agent factory.

#### Task 11 — Live reload
- `FileSystemWatcher` over skill roots; refresh `SkillCatalog` on add/edit/remove so the
  next iteration's system prompt reflects changes without restart. **Verify:** watcher test.

## Suggested Execution Order for Subagents

```
Task 1 ──┬─► Task 2 ──┬─► Task 5 ──► Task 6  (MVP complete)
         ├─► Task 4 ──┘
         └─► Task 3 (parallel, stub ISkillCatalog)
Phase 2: Tasks 7–11 in any order after Task 6.
```
