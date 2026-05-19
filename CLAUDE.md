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

## Code Style

Read this file only when writing new code or editing existing code: @.editorconfig

## Build & Test Commands

```bash
# Build the full solution
dotnet build src/Agency.slnx

# Run all non-functional tests
dotnet test src/Agency.slnx --filter "Category!=Functional"

# Run a single test project
dotnet test src/Agency.Embeddings.Test/Agency.Embeddings.Test.csproj
dotnet test src/Agency.Llm.Test/Agency.Llm.Test.csproj

# Run functional tests (requires LM Studio running at http://llm-host.example:1234)
dotnet test src/Agency.Llm.Test --filter "Category=Functional"

# Start local infrastructure (PostgreSQL + pgvector)
cd src && docker-compose up -d
```

## Architecture Overview

This is a .NET 10 solution that provides a RAG (Retrieval-Augmented Generation) pipeline: generate embeddings → store in PostgreSQL/pgvector → query → format results → send to LLM.

### Project Dependency Graph

```
Agency.Common               (base: Dataset, IEmbeddingGenerator)
    ↓
Agency.Embeddings           (IEmbeddingGenerator → OpenAI-compatible API)
Agency.SQL                  (PostgreSQL + pgvector; SQLQueryEmbedder injects embeddings)
Agency.RagFormatter         (Dataset → Markdown table for LLM context)
Agency.Llm.Abstractions     (ILlmClient interface)
    ↓
Agency.Llm.Claude           (Anthropic SDK implementation)
Agency.Llm.OpenAI           (OpenAI SDK implementation)
```

## C# Conventions

- Always use XML doc comments (`///`) for all class and method comments — never plain `//` comments for documentation. Use `<summary>`, `<param>`, `<returns>`, and `<see cref="..."/>` tags as appropriate. This applies to test projects as well as production code.
- Do NOT use `yield return` inside try-catch blocks — this does not compile in C#
- Do NOT instantiate abstract classes directly; use interfaces or concrete implementations
- Sealed classes cannot be mocked — use functional/integration tests or extract an interface
- Always verify builds pass (`dotnet build`) after code changes before declaring success

### ILlmClient Abstraction

All LLM providers implement `ILlmClient` (`Agency.Llm.Abstractions`):

- `SendAsync()` — single request/response
- `StreamAsync()` — returns `IAsyncEnumerable<string>` chunks

Both `ClaudeClient` and `OpenAIClient` follow the same structure: constructor takes `IOptions<TOptions>` and `ILogger<T>`, with full OpenTelemetry instrumentation (ActivitySource + Meter with request count, error count, duration, and token usage histograms).

### Observability Pattern

Every client (LLM and embeddings) exposes:

- An `ActivitySource` for distributed tracing
- A `Meter` with counters (`requests`, `errors`) and histograms (`duration`, `tokens.input`, `tokens.output`)
- Tags include: `system`, `model`, `method`, `token_type`

### SQLQueryEmbedder

`Agency.SQL/SQLQueryEmbedder.cs` uses regex to find `vectorize('<text>')` placeholders in SQL queries and replaces them with pgvector literal format (`[f1,f2,...]`) using the injected `IEmbeddingGenerator`. This allows writing SQL like:

```sql
SELECT * FROM docs ORDER BY embedding <-> vectorize('search query') LIMIT 5
```

### Infrastructure

- PostgreSQL 18 + pgvector extension via Docker (`src/docker-compose.yml`)
- Credentials: `dev_user` / `dev_password`, database: `dev_db`, port `5432`
- Functional LLM tests target LM Studio at `http://llm-host.example:1234`

### Global Build Config & Centralized Package Management

`src/Directory.Build.props` is the single source of truth for:

- **Package versions** — all NuGet dependencies are pinned here and referenced by version in individual `.csproj` files (no duplicate version strings)
- **Compiler settings** — `TreatWarningsAsErrors=true` and `Nullable=enable` for all projects
- **Code standards** — all code must be warning-free and null-safe

When adding or updating a dependency:

1. Add/update the version in `Directory.Build.props`
2. Reference it in the `.csproj` file without repeating the version number
3. This ensures consistency across the entire solution and makes dependency updates a single-point change

## Git & Auth

- Git push may fail due to credential/auth issues on this machine. If push fails, inform the user rather than retrying endlessly.
- This repo uses scoped Git credentials via includeIf directives

## Testing

- Always run the full test suite after changes: `dotnet test`
- For PostgreSQL tests, ensure connection strings are configured via user secrets — check `dotnet user-secrets list` before assuming config is correct.

## Debugging Approach

- When diagnosing bugs, read the actual error and failing code carefully before suggesting fixes. Do NOT guess at the root cause — verify with evidence first.
- When user says an approach is wrong, pivot immediately rather than doubling down.