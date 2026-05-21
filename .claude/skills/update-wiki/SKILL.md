---
name: update-wiki
description: Update the Agency solution Wiki pages to reflect code changes. Pass project names to update specific pages, or leave empty to detect changes from git and update all affected pages.
argument-hint: "[ProjectName1] [ProjectName2] ..."
allowed-tools: Glob Grep Read Write Bash
---

Update the Agency solution wiki in `[repo-root]\docs\`.

## Your Task

$ARGUMENTS was provided as the list of project names to update. If it is empty, auto-detect which projects changed using git.

### Step 1 — Determine which projects need updating

If `$ARGUMENTS` is non-empty, parse it as a space-separated list of project names (e.g. `Agency.Agentic Agency.Llm.Claude`).

If `$ARGUMENTS` is empty, run:

Read `[repo-root]/docs/Home.md` and extract the commit hash from the `<!-- last-wiki-commit: <HASH> -->` HTML comment at the top of the file. Then run:

```
git diff --name-status <HASH>
```
Map changed file paths to project names by extracting the top-level folder under `src/` (e.g. `src/Agency.Agentic/Agent.cs` → `Agency.Agentic`). Skip test projects (names ending in `.Test`).

Act as orchestrator, do not perform Step 2 directly. Instead, delegate Step 2 to a subagent for each project that needs updating.

### Step 2 — For each project to update

Step 2 must always be delegated to a subagent. Do not perform Step 2 directly in the parent agent.

For each project, invoke one subagent and have that subagent do all of the following:

1. Read all `.cs` files in `[repo-root]/src/<ProjectName>/` (excluding `obj/` subfolders).
2. Read the existing wiki page at `[repo-root]/Docs/<ProjectName>.md` if it exists.
3. Identify what has changed: new types, renamed types, removed types, changed method signatures, new dependencies, new configuration options, or new observability signals.
4. Rewrite the wiki page following the **Required Page Structure** below. Preserve any sections that are still accurate; update only what has changed.

After each subagent completes, verify the written wiki file exists and continue to the next project.

### Step 3 — Check for new projects

If any project under `src/` has no corresponding wiki page (and is not a test project), create a new wiki page for it following the **Required Page Structure** below.

### Step 4 — Check for removed projects

If any wiki page references a project that no longer exists under `src/`, note this to the user but do not delete the page automatically — ask for confirmation first.

### Step 5 — Update Home.md if needed

Always update `[repo-root]/docs/Home.md` at the end of every run:

1. **Update the commit hash** — overwrite the `<!-- last-wiki-commit: <HASH> -->` comment at the top of the file with the result of `git rev-parse HEAD`.
2. **Update the Mermaid diagram** — if any projects were added or removed, or if dependencies between projects changed, update the `mermaid` fenced code block to reflect the current architecture. Use a `graph TD` (top-down) layout. Each node is a project name; each arrow represents a compile-time dependency (the dependent project points to the dependency).
3. **Update the Project Pages index** — if any projects were added or removed, keep the index list complete and sorted by layer.

---

## Required Page Structure

Every wiki page must contain these sections in this order. Omit a section only when its content genuinely does not apply (noted per-section below).

```
# <ProjectName>
#tag1 #tag2 ...

## What It Is
One concise paragraph.

**Namespace:** `<root C# namespace>`

## Prerequisites
(omit entirely if no infrastructure, native binaries, or environment setup is needed)

## API Surface
(always present — the public contract of this module)

## Registration
(omit if the project exposes no IServiceCollection extension methods)

## How It Works
(behavioral description, flow pseudocode, or step list)

## Agent Tools
(omit if the project exposes no ITool implementations or MCP tools)

## Observability
(omit if the project defines no ActivitySource or Meter)

## How It Relates to Other Projects
(always present)

## Design Notes
(always present — minimum two bullet points)
```

---

## Section-by-Section Rules

### `## What It Is`
One paragraph. State what the project does and its role in the pipeline. Include the sentence pattern:
> "`<ProjectName>` is the … that …"

Immediately after the paragraph, add:

```
**Namespace:** `Agency.Foo.Bar`
```

Determine the namespace by reading the `namespace` declaration at the top of the primary source files.

---

### `## Prerequisites`
List only hard requirements that a developer must satisfy *before* the project will work:
- External infrastructure (PostgreSQL with pgvector, SQLite with sqlite-vec, Docker)
- Native binaries or Node sidecars
- Environment variables or user-secrets that must be set

Omit this section entirely if the project has no external dependencies beyond NuGet packages.

---

### `## API Surface`

**This section documents only the exported public contract.** Apply these rules strictly:

**Include:**
- Every `public interface` or `internal interface` — show its full declaration as a `csharp` code block (method signatures, property declarations, event declarations).
- Every `public abstract class` or `internal abstract class` — show only `public`, `internal` and `protected` constructors, `abstract` members, and `virtual` members. Label each virtual member with a `// virtual` comment.
- Every `public` or `internal` concrete class, record, struct, or enum that is part of the module's external contract — show public constructors and public members.
- `protected` members on non-sealed classes (they are part of the inheritable contract).
- Extension methods intended for external callers.

**Exclude absolutely:**
- `private` members.
- `private protected` members.
- Implementation methods that are only accessible through a public interface (document the interface, not the hidden implementation).
- Backing fields, compiler-generated members, and `[EditorBrowsable(Never)]` members.

**Format:** Show each interface or notable abstract type as a fenced `csharp` code block containing the *declaration* (not an instantiation example). Add `// File: src/<ProjectName>/<Path>.cs` as the first line of each block so readers can navigate to the source.

Example of correct format:

```csharp
// File: src/Agency.Llm.Common/ILlmClient.cs
public interface ILlmClient
{
    string ClientType { get; }
    Task<LlmResponse> SendAsync(string model, string systemPrompt, string userMessage, CancellationToken ct = default);
    IAsyncEnumerable<LlmStreamChunk> StreamAsync(string model, string systemPrompt, string userMessage, CancellationToken ct = default);
    Task<AgentLlmResponse> SendAgentAsync(string model, string systemPrompt, IReadOnlyList<LlmMessage> messages, IReadOnlyList<ToolDefinition> tools, CancellationToken ct = default);
    IAsyncEnumerable<AgentStreamChunk> StreamAgentAsync(string model, string systemPrompt, IReadOnlyList<LlmMessage> messages, IReadOnlyList<ToolDefinition> tools, CancellationToken ct = default);
}
```

For concrete types with many members, use a table instead of a full declaration:

```
| Member | Signature / Type | Notes |
|--------|-----------------|-------|
| `ctor` | `Agent(ILlmClient llm, string model, ...)` | |
| `RunAsync` | `IAsyncEnumerable<AgentEvent> RunAsync(Context ctx, CancellationToken ct)` | |
| `ChatAsync` | `IAsyncEnumerable<AgentEvent> ChatAsync(string input, Context ctx, ...)` | |
```

Group members under sub-headings when the project has more than one significant public type (e.g., `### Interfaces`, `### Base Classes`, `### Value Types`).

---

### `## Registration`

Show the exact `IServiceCollection` extension method call(s) with all relevant overloads or option types. Every code block must open with the required `using` statements.

```csharp
using Agency.GraphRAG.Code.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

services.AddCodeIndex(options =>
{
    options.Store = CodeIndexStore.Sqlite;
    options.SqlitePath = @"E:\data\graphrag-code.db";
});
```

Describe what the method registers (interfaces → implementations) in a brief bullet list below the code block.

---

### `## How It Works`

Describe runtime behavior. Use a numbered flow list for pipeline-style projects. Use prose + code for library-style projects. Include at least one practical usage `csharp` code block that shows realistic end-to-end consumption of the public API. Every code block must start with `using` declarations.

---

### `## Agent Tools`

When the project exposes tools to the agent loop (implements `ITool` or uses `[McpServerTool]`), document each tool in a table:

| Tool Name (exact string) | Description shown to LLM | Key Parameters |
|--------------------------|--------------------------|----------------|
| `code_index_query`       | "Query the code index…"  | `question: string` |

The tool name must be the exact string the LLM will see (i.e., the value of `ITool.Name` or the `[McpServerTool]` name attribute).

---

### `## Observability`

List the `ActivitySource` name and `Meter` name as string literals, plus a table of all metrics:

| Metric | Kind | Tags |
|--------|------|------|
| `llm.client.requests` | Counter | `gen_ai.system`, `gen_ai.request.model`, `llm.method` |

---

### `## How It Relates to Other Projects`

Two-column table. Use `[[WikiLink]]` for all project references.

| Project | Relationship |
|---------|-------------|
| [[Agency.Llm.Common]] | Implements `ILlmClient` |

---

### `## Design Notes`

Minimum two bullet points. Explain *why* non-obvious decisions were made — hidden constraints, deliberate tradeoffs, things that would surprise a reader. Do not describe what is already obvious from the code.

---

## Wiki Format Rules

- **Obsidian WikiLinks**: Use `[[ProjectName]]` (not `[text](url)`) for all cross-references to other wiki pages.
- **Tags**: Second line of every page must be Obsidian hashtags: `#tag1 #tag2 ...`
- **No hallucination**: Only document types, methods, and behaviors that actually exist in the source code you read. Do not invent APIs.
- **Tense**: Write in present tense ("provides", "implements", "returns" — not "will provide").
- **No test details**: Do not describe test project internals; skip test projects entirely.
- **`using` in every code block**: Every `csharp` fenced block must begin with the `using` declarations needed to compile the shown snippet.
- **File paths in API Surface**: Every code block in `## API Surface` must begin with `// File: src/<ProjectName>/...` so readers can navigate directly to the source.
- **Public contract only**: The `## API Surface` section must never mention `private` or `internal` members.

---

## Example: what a good update looks like

If `Agency.Agentic/Agent.cs` added a new `PauseAsync` method and a new `AgentPausedEvent`, the updated `Agency.Agentic.md` wiki page should:
1. Add `PauseAsync` to the **API Surface** member table with its full signature.
2. Add `AgentPausedEvent` to the **Agent Events** table in **How It Works**.
3. Show `PauseAsync` in the usage code block.
4. Not change anything else that hasn't changed.

Keep edits minimal and precise — do not rewrite sections that are still accurate.
