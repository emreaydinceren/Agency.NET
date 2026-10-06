# Agency.Indexer

## What It Is

`Agency.Indexer` is the `agency-index` command-line tool: incremental semantic indexing of a folder of
documentation for agents. It ships as a .NET global tool (package `AgencyDotNet.Indexer`) with an embedded
agent skill (`SKILL.md`). An agent indexes a directory under a name, re-runs the index whenever it likes
(only added, changed or deleted files are processed), and searches it; every command prints one JSON
object to stdout.

**Namespace:** `Agency.Indexer` · **Command:** `agency-index` · **Project type:** console app, `PackAsTool`

## Install

```bash
dotnet tool install -g AgencyDotNet.Indexer && agency-index install-skill
```

For the full walk-through (scope, embeddings endpoint, `PATH`, first index, agent runbook) see
[Install the documentation search skill](../Install-Indexer-Skill.md). `install-skill` writes `SKILL.md` to `~/.claude/skills/agency-index/` and `~/Agents/skills/agency-index/`
(or `--dir <skills-root>`). Until a clean (non-`-g<sha>`) version is published to nuget.org, add
`--prerelease` to the install.

## Commands

| Command | Purpose |
|---|---|
| `index --index <name> [--root <dir>] [--ext ...] [--names ...] [--max-file-kb N] [--wait] [--dry-run] [--log <file>]` | Create or refresh an index. `--root` is required on the first run and fixed afterwards. Progress lines and each failed file with its reason go to stderr (and `--log`); `failed` in the result is `{path, reason}` entries; `--dry-run` reports the delta, chunk count and a time estimate without writing. |
| `search --index <name> --query <text> [--top N] [--min-score X] [--within D] [--no-text] [--snippet-chars N]` | Semantic search; hits carry `path`, `chunk`, `score` (cosine similarity) and `text`. `--min-score` (or `Search:MinScore`) and `--within` drop weak hits, reported as `filtered` with the pre-filter `best_score`; `--no-text` and `--snippet-chars` shrink the output. |
| `list --index <name>` | The index configuration and every indexed file with size, last-write time and chunk count. |
| `indexes` | Every index and its root. |
| `drop --index <name> [--wait]` | Delete the index's chunks, manifest and configuration. |
| `install-skill [--dir <skills-root> \| --scope repo\|user]` | Write the bundled `SKILL.md` (default scope `user`); the output says which files replaced an existing one. |
| `uninstall [--scope repo\|all] [--dir <skills-root>] [--yes]` | Remove the skill and the data. `repo` (default) drops this repo's indexes and its skill; `all` also drops every index, removes every skill copy, the SQLite database files and `indexer.json`. Previews unless `--yes`; never removes the tool itself (the last step is returned under `remaining`). |
| `uninstall-skill [--dir <skills-root> \| --scope repo\|user]` | Remove the skill file written by `install-skill`. |
| `doctor` | Read-only JSON report of every prerequisite (tool, skill, config, endpoint, model, dimensions, database, indexes), each with a `fix`. Exits 0; branch on `status`. |
| `setup [--scope] [--endpoint lmstudio\|ollama\|openai\|openrouter \| --embedding-url <url>] [--embedding-model <id>] [--index <name>] [--root <dir>] [--yes]` | Install the skill, pick the embedding model, measure its dimensions, merge `indexer.json`, optionally run the first index and a smoke search. Without `--yes` it only previews. |

Exit codes: `0` ok, `1` failure (for `index`: some files failed; the rest was applied), `2` usage or
configuration error, `3` another process holds the index's writer lock.

The embeddings API key is a secret and is read from the environment (`OPENAI_API_KEY`, `OPENROUTER_API_KEY`, or
`AGENCY_INDEX_Embedding__ApiKey`); `setup` never writes it to a file and `doctor` flags one stored in
`indexer.json`. A repo can carry its own defaults in `.agency-index.json` at its root (found by walking up from the current
folder, nearest wins): `Index`, `Root` (relative to the file), `Extensions`, `Names` and `MaxFileKb`, so commands
work from any folder of the repo without `--index`/`--root`. Only those keys are read from it; endpoint, model,
database and key settings are ignored there, because the file arrives with the repository. Per key, the command
line beats `AGENCY_INDEX_*` variables, which beat the repo file, which beats the user file. Configuration
(highest precedence first): command-line options, `AGENCY_INDEX_*` environment variables
(`AGENCY_INDEX_Provider`, `AGENCY_INDEX_Database`, `AGENCY_INDEX_Embedding__BaseUrl`, ...), then
`~/.agency/indexer.json`. The default provider is SQLite at `~/.agency/index.db`.

## How It Works

1. **Selection** — `FileScanner` walks the root, skipping `.git`, `node_modules`, `bin`, `obj` and `dist`,
   and selects files by extension (default `.md .markdown .mdx .txt .rst .adoc .html .htm`) or exact
   extensionless name (default `README`, `CHANGELOG`, `CONTRIBUTING`); files above the size cap (1 MB)
   are reported as skipped.
2. **Delta** — `DeltaPlanner` compares the scan with the manifest by size and last-write ticks only (no
   content hashing) and classifies each file as added, changed, removed or unchanged.
3. **Write** — each added or changed file is read (HTML is reduced to text by `HtmlTextExtractor`),
   chunked with [Agency.Ingestion.SemanticKernel](Agency.Ingestion.SemanticKernel.md), and written with
   `IVectorStore.ReplaceDocumentAsync` ([Agency.VectorStore.Common](Agency.VectorStore.Common.md)) — sequential
   embedding requests of at most `Embedding:MaxBatchSize` (default 32) chunks per file, stale chunks removed. Its manifest entry is saved afterwards, so a crash
   between the two just re-indexes that file next run. Removed files are replaced with no chunks.
4. **Storage** — every index is a vector-store project owned by the user `agency-index`. The manifest and
   index configuration live in an [`IKVStore`](Agency.KeyValueStore.Common.md): file entries under the
   session `files:<index>` keyed by full path, configurations under the session `indexes`.

## Concurrency

One writer per index, any number of readers:

- **SQLite** — the writer opens `<database>.<index>.lock` with `FileShare.None`; the database runs in WAL
  mode so searches never block on a running index. Not reliable on network file systems.
- **PostgreSQL** — the writer holds a session-level advisory lock (`pg_try_advisory_lock`, key derived from
  the index name) on a dedicated unpooled connection.

Both locks are released by the OS or the server if the process dies. A second writer gets exit code `3`,
or blocks with `--wait`.

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [Agency.VectorStore.Sql.Sqlite](Agency.VectorStore.Sql.Sqlite.md) · [Agency.VectorStore.Sql.Postgres](Agency.VectorStore.Sql.Postgres.md) | Chunk storage and similarity search, through `IVectorStore` |
| [Agency.KeyValueStore.Sql.Sqlite](Agency.KeyValueStore.Sql.Sqlite.md) · [Agency.KeyValueStore.Sql.Postgres](Agency.KeyValueStore.Sql.Postgres.md) | Manifest and index configuration, through `IKVStore` |
| [Agency.Embeddings.OpenAI](Agency.Embeddings.OpenAI.md) | Embeddings from any OpenAI-compatible endpoint |
| [Agency.Ingestion](Agency.Ingestion.md) · [Agency.Ingestion.SemanticKernel](Agency.Ingestion.SemanticKernel.md) | `Document` and the Markdown-aware text splitter |

## Design Notes

- A skill plus a CLI instead of an MCP server: each command is a short-lived process with JSON on stdout,
  which avoids stdio-protocol logging pitfalls and tool-call timeouts on long index runs.
- An index is tied to its root and embedding model; changing either is a usage error (drop and rebuild).
  Narrowing `--ext`/`--names` is allowed and removes files that no longer match.
- Searches pass a non-null session id so both backends restrict results to the index's project (the
  Postgres store reads a null session as "every session and project of the user").
- The package carries no `.pdb` files and no symbols package; `Directory.Build.targets` skips
  `IncludeSymbols` for `PackAsTool` projects.
