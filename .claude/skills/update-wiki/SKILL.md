---
name: update-wiki
description: Update the Agency solution Wiki pages to reflect code changes. Pass project names to update specific pages, or leave empty to detect changes from git and update all affected pages.
argument-hint: "[ProjectName1] [ProjectName2] ..."
allowed-tools: Glob Grep Read Write Bash
---

Update the Agency solution wiki in `E:\Repos\Agency\Wiki\`.

## Your Task

$ARGUMENTS was provided as the list of project names to update. If it is empty, auto-detect which projects changed using git.

### Step 1 — Determine which projects need updating

If `$ARGUMENTS` is non-empty, parse it as a space-separated list of project names (e.g. `Agency.Agentic Agency.Llm.Claude`).

If `$ARGUMENTS` is empty, run:
```
git -C E:/Repos/Agency diff --name-only HEAD~1 HEAD
```
and also check uncommitted changes:
```
git -C E:/Repos/Agency diff --name-only
git -C E:/Repos/Agency diff --cached --name-only
```

Map changed file paths to project names by extracting the top-level folder under `src/` (e.g. `src/Agency.Agentic/Agent.cs` → `Agency.Agentic`). Skip test projects (names ending in `.Test`).

### Step 2 — For each project to update

Step 2 must always be delegated to a subagent. Do not perform Step 2 directly in the parent agent.

For each project, invoke one subagent and have that subagent do all of the following:

1. Read all `.cs` files in `E:\Repos\Agency\src\<ProjectName>\` (excluding `obj/` subfolders).
2. Read the existing wiki page at `E:\Repos\Agency\Wiki\<ProjectName>.md` if it exists.
3. Identify what has changed: new types, renamed types, removed types, changed method signatures, new dependencies, new configuration options, or new observability signals.
4. Rewrite the wiki page to reflect the current state of the code. Preserve the existing page structure (What It Is, How It Works, How It Relates, etc.) but update all content to match reality.

After each subagent completes, verify the written wiki file exists and continue to the next project.

### Step 3 — Check for new projects

If any project under `src/` has no corresponding wiki page (and is not a test project), create a new wiki page for it following the same format as existing pages:
- `# <ProjectName>` heading
- Obsidian `#tags` on the second line
- Sections: **What It Is**, **Key Types** (with code examples), **How It Relates to Other Projects** (table with `[[WikiLinks]]`), **Design Notes** or **Observability** if applicable

### Step 4 — Check for removed projects

If any wiki page references a project that no longer exists under `src/`, note this to the user but do not delete the page automatically — ask for confirmation first.

### Step 5 — Update Home.md if needed

If any projects were added or removed, or if the architecture diagram changed, update `E:\Repos\Agency\Wiki\Home.md`:
- Keep the ASCII architecture diagram accurate
- Keep the **Project Pages** index list complete and sorted by layer

---

## Wiki Format Rules

Follow these rules exactly when writing or updating wiki pages:

- **Obsidian WikiLinks**: Use `[[ProjectName]]` (not `[text](url)`) for all cross-references to other wiki pages.
- **Tags**: Second line of every page must be Obsidian hashtags: `#tag1 #tag2 ...`
- **Tables**: All "How It Relates" sections use a two-column Markdown table `| Project | Relationship |`.
- **Code examples**: Every page must have at least one practical `csharp` code block showing real usage.
- **No hallucination**: Only document types, methods, and behaviors that actually exist in the source code you read. Do not invent APIs.
- **Tense**: Write in present tense ("provides", "implements", "returns" — not "will provide").
- **No test details**: Do not describe test project internals; skip test projects entirely.

---

## Example: what a good update looks like

If `Agency.Agentic/Agent.cs` added a new `PauseAsync` method and a new `AgentPausedEvent`, the updated `Agency.Agentic.md` wiki page should:
1. Mention `PauseAsync` in the **How It Works** section with a code example.
2. Add `AgentPausedEvent` to the **Agent Events** table.
3. Not change anything else that hasn't changed.

Keep edits minimal and precise — do not rewrite sections that are still accurate.
