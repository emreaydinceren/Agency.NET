# Ingestion + Harness Integration — Implementation Tracker

## Overview

Integrates Agency's data plane (ingestion pipeline, vector store, embeddings) into the interactive REPL harness. Adds three content scopes (Global, Session, Project), REPL commands for ingestion, and a `semantic_search` LLM tool. The LLM is hinted about available documents on every turn so it knows when to call `semantic_search`.

---

## Scope Semantics

| Scope | `session_id` stored | `project_id` stored |
|---|---|---|
| Global | `"*"` | `"*"` |
| Session | current session ID (pre-generated Guid, stable for REPL session) | `"*"` |
| Project | `"*"` | `"<project-name>"` |

Search always unions all three: Global OR (Session with current ID) OR (Project IN loadedProjects). Scopes are mutually exclusive per document chunk.

---

## Task Waves

Dependencies: Wave 1 must complete before Wave 2 starts; Wave 2 before Wave 3.

---

### Wave 1 — Foundation (parallel)

#### 1A · VectorStore Layer + Ingestion Pipeline
**Status:** `[x] done` — branch `worktree-agent-a446f6d6754d90aef`

Files to change:
- `src/VectorStore/Agency.VectorStore.Common/DocumentInfo.cs` — **new** `record DocumentInfo(string SourceFile, string SessionId, string ProjectId)`
- `src/VectorStore/Agency.VectorStore.Common/IVectorStore.cs` — add `string? projectId = null` param to `UpsertAsync`/`DeleteAsync`; add `ListProjectsAsync` and `ListDocumentsAsync` methods
- `src/VectorStore/Agency.VectorStore.Common/Query.cs` — add `IReadOnlyList<string>? ProjectIds = null` (last positional param)
- `src/VectorStore/Agency.VectorStore.Sql.Postgres/PostgresKVStore.cs` — schema adds `project_id TEXT NOT NULL DEFAULT '*'`; PK becomes `(user_id, session_id, project_id, key)`; SearchAsync WHERE unions all three scopes; ListProjectsAsync + ListDocumentsAsync implemented
- `src/VectorStore/Agency.VectorStore.Sql.Sqlite/SqliteKVStore.cs` — same (Sqlite uses parameterized IN clause, not ANY())
- `src/Ingestion/Agency.Ingestion/DefaultIngestionPipeline.cs` — `ExecuteAsync` adds `string? projectId = null`, passes it to `store.UpsertAsync`
- `src/VectorStore/Agency.VectorStore.Sql.Sqlite.Test/SqliteKVStoreFunctionalTests.cs` — update all `UpsertAsync`/`DeleteAsync`/`new Query(...)` call sites
- `src/VectorStore/Agency.VectorStore.Sql.Postgres.Test/PostgresKVStoreFunctionalTests.cs` — same
- `src/Ingestion/Agency.Ingestion.Test/DefaultIngestionPipelineTests.cs` — add `projectId` arg to all mock setups and `Verify` calls

**Result logged here:** _pending_

---

#### 1B · ChatSession + Agent Context
**Status:** `[x] done` — branch `worktree-agent-aa29d16221f997a96`

Files to change:
- `src/Harness/Agency.Harness/ChatSession.cs` — add optional `SessionContext? session = null` constructor param; add `public void SetKnowledge(KnowledgeContext knowledge)` method; if `_ctx` null when `SetKnowledge` called, store as `_pendingKnowledge` and apply on first `CreateContext`
- `src/Harness/Agency.Harness/Agent.cs` — `CreateContext` accepts optional `SessionContext? session = null` and puts it in the resulting `Context.Session` (so `Agent.RunAsync`'s null-check sees a pre-set ID and skips generation)

**Result logged here:** _pending_

---

#### 1C · Configuration Options
**Status:** `[x] done` — branch `worktree-agent-a5b4790c1dcacc816`

Files to create/change:
- `src/Harness/Agency.Harness.Console/Configuration/VectorStoreOptions.cs` — `Provider` (default `"sqlite"`)
- `src/Harness/Agency.Harness.Console/Configuration/IngestionOptions.cs` — `ChunkSize` (512), `ChunkOverlap` (64), `SearchPattern` (`"*.md"`)
- `src/Harness/Agency.Harness.Console/Configuration/RetrievalOptions.cs` — `TopK` (5)
- `src/Harness/Agency.Harness.Console/appsettings.json` — add `VectorStore`, `Ingestion`, `Retrieval` sections

**Result logged here:** _pending_

---

### Wave 2 — Services + Tools (parallel, after Wave 1 merges)

#### 2A · SemanticSearchTool
**Status:** `[ ] pending`
**Branch:** `feat/ingestion-semantic-search-tool`
**Depends on:** 1A merged

Files to create:
- `src/Harness/Agency.Harness/Tools/SemanticSearchTool.cs` — LLM-facing `ITool`; takes `IVectorStore`, `IOptions<RetrievalOptions>`, `IProjectSessionState`; builds `Query` with userId/sessionId/loadedProjects from state; returns `hits.ToDataset().ToMarkdownTable()`; no scope param (always searches all accessible scopes)

Note: `SemanticSearchTool` lives in `Agency.Harness` (alongside `ReadFileTool`). This adds a new dependency from `Agency.Harness` → `Agency.VectorStore.Common`. Add `<ProjectReference>` to `Agency.Harness.csproj`.

**Result logged here:** _pending_

---

#### 2B · Services Layer
**Status:** `[ ] pending`
**Branch:** `feat/ingestion-services`
**Depends on:** 1A and 1B merged

Files to create:
- `src/Harness/Agency.Harness.Console/Services/IProjectSessionState.cs` + `ProjectSessionState.cs`
  - `UserId` from `AgentOptions.UserId ?? Environment.UserName`
  - `SessionId` = `Guid.NewGuid().ToString("N")` generated at construction (stable for REPL session)
  - `LoadedProjects IReadOnlyList<string>`; `LoadProject(name)` / `UnloadProject(name)`
- `src/Harness/Agency.Harness.Console/Services/IngestionCommandService.cs`
  - Wraps `DefaultIngestionPipeline<string>`
  - `IngestFileAsync(filePath, userId, sessionId, projectId, ct) → Task<int>` (returns chunk count)
  - `IngestDirectoryAsync(dirPath, pattern, userId, sessionId, projectId, ct) → Task<int>`
  - `CountFiles(dirPath, pattern) → int`
- `src/Harness/Agency.Harness.Console/Services/DocumentContextHydrationService.cs`
  - `MarkDirty()` — marks the document list stale
  - `RefreshIfDirtyAsync(ct) → Task<string?>` — calls `store.ListDocumentsAsync(...)` scoped to all accessible scopes; returns formatted fact string or null if no documents
  - Formatted fact: `"The following documents have been ingested and are available for semantic_search:\n- [global] docs/Home.md\n- [session] src/README.md\n- [project:MyDocs] docs/Architecture.md"`

**Status:** `[→] in progress`

---

### Wave 3 — REPL Commands + Wiring (after Wave 2 merges)

#### 3A · REPL Commands
**Status:** `[ ] pending`
**Branch:** `feat/ingestion-repl-commands`
**Depends on:** 2A and 2B merged

Files to create:
- `src/Harness/Agency.Harness.Console/Commands/AddFileCommand.cs`
  - Extracts path from input; prompts if missing
  - Checks if already ingested (any chunk with matching `source_file` metadata exists) → `[y/N]` prompt
  - Runs scope resolution (see below); calls `IngestionCommandService.IngestFileAsync`; prints chunk count
  - After ingest: calls `DocumentContextHydrationService.MarkDirty()`
- `src/Harness/Agency.Harness.Console/Commands/AddFolderCommand.cs`
  - Prompts for pattern if not given (default `*.md`)
  - Counts files via `IngestionCommandService.CountFiles`; if > 50 → `"This will ingest N files, are you sure? [y/N]"`
  - Resolves scope; calls `IngestDirectoryAsync`; marks dirty
- `src/Harness/Agency.Harness.Console/Commands/ProjectsCommand.cs`
  - `/projects-load <name>` → `projectState.LoadProject(name)`; calls `session.SetKnowledge`; marks hydration dirty
  - `/projects-unload <name>` → `projectState.UnloadProject(name)`; same
  - `/projects-list` → calls `store.ListProjectsAsync(userId)`; prints table with `[✓]` for loaded projects
- `src/Harness/Agency.Harness.Console/Commands/CommandRegistry.cs` — register the 5 new commands with argument hints for autocomplete

**Scope resolution helper (shared between Add* commands):**
```
0 loaded projects → prompt: [G]lobal | [S]ession | [P]roject <enter name>
1 loaded project  → auto-select that project (no prompt)
2+ loaded projects → prompt: [G]lobal | [S]ession | [1] ProjA | [2] ProjB | [N] new project name
```

**Result logged here:** _pending_

---

#### 3B · DI Wiring + Package Refs
**Status:** `[ ] pending`
**Branch:** `feat/ingestion-di-wiring`
**Depends on:** all prior waves merged

Files to change:
- `src/Harness/Agency.Harness.Console/Program.cs`
  - After existing memory registration block, register `IVectorStore` (sqlite or postgres per `VectorStore:Provider`)
  - Register `ITextSplitter` as `SemanticKernelTextSplitter` with `IngestionOptions` values
  - Register `IProjectSessionState`, `IngestionCommandService`, `DocumentContextHydrationService` as scoped
  - Pass `new SessionContext { Id = projectState.SessionId }` when constructing `ChatSession`
  - Add `SemanticSearchTool` to the default `ToolRegistry`
  - At start of REPL loop (before first turn), call `hydrationService.RefreshIfDirtyAsync()` and inject fact via `chatSession.SetKnowledge(...)`
  - In the main REPL while loop: check `hydrationService.IsDirty` before reading input; if dirty, refresh and push to chatSession
- `src/Harness/Agency.Harness.Console/Agency.Harness.Console.csproj` — add `<ProjectReference>` for `Agency.Ingestion`, `Agency.Ingestion.FileSystem`, `Agency.Ingestion.SemanticKernel`, `Agency.VectorStore.Sql.Sqlite`, `Agency.VectorStore.Sql.Postgres`
- `src/Harness/Agency.Harness/Agency.Harness.csproj` — add `<ProjectReference>` for `Agency.VectorStore.Common`

**Result logged here:** _pending_

---

## Verification Checklist

- [ ] `dotnet test --filter "Category!=Functional"` passes (unit tests)
- [ ] `/add-file docs/Home.md` → scope prompt → Session → "Ingested N chunks"
- [ ] `/projects-load MyDocs` → `/add-folder docs/ *.md` → auto-selects MyDocs → count gate → ingest
- [ ] `/projects-list` shows MyDocs `[✓]`
- [ ] `/dump-context` shows "Available documents" fact in system prompt after ingest
- [ ] Chat: "What does Agency's ingestion pipeline do?" → LLM calls `semantic_search` without being told to
- [ ] `/projects-unload MyDocs` → same question → no project results
- [ ] Re-ingest same file → `[y/N]` prompt appears
- [ ] Folder with >50 files → count confirmation appears
