# Agency.Indexer

## What It Is

`Agency.Indexer` is the `agency-index` command-line tool: incremental semantic indexing of a folder of
documentation for agents. It ships as a .NET global tool (package `AgencyDotNet.Indexer`) with an embedded
agent skill (a short `SKILL.md` plus an on-demand `REFERENCE.md`). An agent indexes a directory under a name, re-runs the index whenever it likes
(only added, changed or deleted files are processed), and searches it; every command prints one JSON
object to stdout.

**Namespace:** `Agency.Indexer` · **Command:** `agency-index` · **Project type:** console app, `PackAsTool`

## Install

```bash
dotnet tool install -g AgencyDotNet.Indexer && agency-index install-skill
```

For the full walk-through (scope, embeddings endpoint, `PATH`, first index, agent runbook) see
[Install the documentation search skill](../Install-Indexer-Skill.md). `install-skill` writes `SKILL.md` and `REFERENCE.md` to `~/.claude/skills/agency-index/` and `~/Agents/skills/agency-index/`
(or `--dir <skills-root>`). Until a clean (non-`-g<sha>`) version is published to nuget.org, add
`--prerelease` to the install.

## Commands

| Command | Purpose |
|---|---|
| `index --index <name> [--root <dir>] [--ext ...] [--names ...] [--exclude <glob>,...] [--max-file-kb N] [--wait] [--dry-run] [--rebuild] [--summary] [--log <file>]` | Create or refresh an index. `--root` is required on the first run and fixed afterwards. Progress lines (files, chunks, ETA, plus a 30 s heartbeat) and each failed file with its reason go to stderr (and `--log`); `failed` in the result is `{path, reason}` entries; the other file lists are relative to `root`, and `--summary` prints counts instead. `--exclude` takes gitignore-style globs relative to the root. `--rebuild` re-embeds every file, which is how the embedding model is switched. `--dry-run` reports the delta, chunk count and a time estimate (a sample spread over the whole set, scaled by text length) without writing. |
| `search --index <name>[,<name>...] --query <text> [--top N] [--per-file N] [--lines N] [--max-line-chars N] [--highlight] [--vector-only] [--raw-query] [--path <glob>] [--min-score X] [--within D] [--log <file>] [--json]` | Grep-style search. Default output prints, per file, the lines closest to the question as `relative/path.md:LINE: text`, then `  [score 0.63, +3.1 sd] heading`, then a verdict line (`# top hit leads by X; read it and stop`, `# no clear winner; reword or open two`, or `# no match above min score`). The vector ranking is fused (reciprocal rank) with a BM25 ranking of the files read from disk; words found in 75% of the files are left out of the embedded query (`--raw-query` keeps them); scores are normalized against the index's noise statistics. `--json` (or `--format json`) prints the unchanged 0.1.221 schema: `hits` with `path`, `chunk`, `score`, `heading`, `start_line`, `end_line`, plus `--full`, `--snippet-chars`, `--group-by-file`, `--hybrid`, `top_gap` and `hint`. `--log` appends one JSON line per call. |
| `read [--index <name> --path <file> \| --hit N] [--start N] [--end N \| --around LINE] [--window 40] [--all] [--json]` | Plain text: a `# path lines A-B of N` header (with the stale flag) and numbered lines. No range: the first 40 lines; `--around`/`--hit` (the Nth file of the last search from this folder): a 40-line window; `--all`: the whole file; explicit ranges: at most 400 lines. |
| `calibrate --index <name> [--questions <file>] [--save]` | Runs twelve unrelated queries and reports the noise ceiling and a suggested minimum score; `--save` stores it (and the noise statistics) in the index configuration, and `search` uses it when no threshold is configured. `--questions` takes `{question, expected_path}` rows and also reports the answer floor, how many answers score no better than noise, and a threshold inside the gap, or a warning when they overlap (then nothing is saved). |
| `list --index <name> [--summary]` | The index configuration and every indexed file (relative to the root) with size, last-write time and chunk count; `--summary` gives counts. |
| `indexes` | Every index and its root. |
| `drop --index <name> [--wait]` | Delete the index's chunks, manifest and configuration. |
| `install-skill [--dir <skills-root> \| --scope repo\|user]` | Write the bundled `SKILL.md` and `REFERENCE.md` (default scope `user`, two copies: `~/.claude/skills` and `~/Agents/skills`; `setup` defaults to `repo`); the output says which files replaced an existing one. |
| `uninstall [--scope repo\|all] [--dir <skills-root>] [--yes]` | Remove the skill and the data. `repo` (default) drops this repo's indexes and its skill; `all` also drops every index, removes every skill copy, the SQLite database files and `indexer.json`. Previews unless `--yes`; never removes the tool itself (the last step is returned under `remaining`). |
| `uninstall-skill [--dir <skills-root> \| --scope repo\|user]` | Remove the skill file written by `install-skill`. |
| `doctor` | Read-only JSON report of every prerequisite (tool, skill, config, endpoint, model, dimensions, database, indexes), each with a `fix`. Exits 0; branch on `status`. |
| `setup [--scope] [--endpoint lmstudio\|ollama\|openai\|openrouter \| --embedding-url <url>] [--embedding-model <id>] [--index <name>] [--root <dir>] [--no-index] [--yes]` | Install the skill, pick the embedding model, measure its dimensions, merge `indexer.json`, optionally run the first index and a smoke search (`--no-index`: write the config and repo file only). Without `--yes` it only previews. Warns about existing indexes built with another model (`warnings`) and stops with `index_model_mismatch` if the index it would build was. |

Exit codes: `0` ok, `1` failure (for `index`: some files failed; the rest was applied), `2` usage or
configuration error, `3` another process holds the index's writer lock.

The embeddings API key is a secret and is read from the environment (`OPENAI_API_KEY`, `OPENROUTER_API_KEY`, or
`AGENCY_INDEX_Embedding__ApiKey`); `setup` never writes it to a file and `doctor` flags one stored in
`indexer.json`. A repo can carry its own defaults in `.agency-index.json` at its root (found by walking up from the current
folder, nearest wins): `Index`, `Root` (relative to the file), `Extensions`, `Names`, `MaxFileKb` and `Exclude`, so commands
work from any folder of the repo without `--index`/`--root`. Only those keys are read from it; endpoint, model,
database and key settings are ignored there, because the file arrives with the repository. Per key, the command
line beats `AGENCY_INDEX_*` variables, which beat the repo file, which beats the user file. Configuration
(highest precedence first): command-line options, `AGENCY_INDEX_*` environment variables
(`AGENCY_INDEX_Provider`, `AGENCY_INDEX_Database`, `AGENCY_INDEX_Embedding__BaseUrl`, ...), then
`~/.agency/indexer.json`. The default provider is SQLite at `~/.agency/index.db`. For PostgreSQL (`Provider: postgres`, pgvector required) the
connection string contains the password, so it is read from `AGENCY_INDEX_Database` and `setup` never writes it
to `indexer.json`; `doctor` flags one left there. See
[Install-Indexer-Skill](../Install-Indexer-Skill.md#postgresql-optional).

## How It Works

1. **Selection** — `FileScanner` walks the root, skipping `.git`, `node_modules`, `bin`, `obj` and `dist`,
   and selects files by extension (default `.md .markdown .mdx .txt .rst .adoc .html .htm`) or exact
   extensionless name (default `README`, `CHANGELOG`, `CONTRIBUTING`); files above the size cap (1 MB)
   are reported as skipped.
2. **Delta** — `DeltaPlanner` compares the scan with the manifest by size and last-write ticks only (no
   content hashing) and classifies each file as added, changed, removed or unchanged.
3. **Write** — each added or changed file is read (HTML is reduced to text by `HtmlTextExtractor`),
   cut into passages by `PassageSplitter` (format 2: at most `PassageLines` = 6 non-blank lines, `PassageOverlap` = 1, never across a heading, embedded with the heading path, exact `start_line`/`end_line`) — or, for a format 1 index and for HTML, chunked with [Agency.Ingestion.SemanticKernel](Agency.Ingestion.SemanticKernel.md) — and written with
   `IVectorStore.ReplaceDocumentAsync` ([Agency.VectorStore.Common](Agency.VectorStore.Common.md)) — sequential
   embedding requests of at most `Embedding:MaxBatchSize` (default 32) chunks per file, stale chunks removed. Its manifest entry is saved afterwards, so a crash
   between the two just re-indexes that file next run. Removed files are replaced with no chunks. After a run that changed anything, the unrelated-query noise distribution and the words common to most files are measured and stored in the index configuration.
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

- **Index format.** `IndexConfig.FormatVersion` is 1 (splitter chunks; what older indexes read as) or 2 (passages). An index keeps its format until `index --rebuild`, which re-cuts it; changing `PassageLines`/`PassageOverlap` is refused until then. A format 1 index still works: `search` warns on stderr and `LineSearch` picks lines by embedding the lines of the retrieved chunks, as it does for passages. `doctor` reports a stale format.
- **Where the lines come from.** `LineSearch` retrieves a vector pool, fuses the keyword ranking (`Lexical`, BM25 over passage-sized windows of the indexed files, read at search time, so keyword-only hits carry true line numbers and are scored by one extra embedding call), keeps the best files, then embeds the lines of their top passages in one batch and prints the closest. Normalized scores and the verdict line use `NoiseStats` (mean, standard deviation and ceiling of the best scores of twelve unrelated probes).

- A skill plus a CLI instead of an MCP server: each command is a short-lived process with JSON on stdout,
  which avoids stdio-protocol logging pitfalls and tool-call timeouts on long index runs.
- An index is tied to its root and embedding model; changing the root is a usage error (drop it), and changing the model needs `index --rebuild`. A rebuild forgets the manifest first, so an interrupted one is finished by a plain run. Searching with another model than the index's is refused, since its scores would be meaningless.
- Chunk locations (`heading`, `start_line`, `end_line`) are found by locating each chunk's first and last line in the source text, because the splitter reports no offsets; a chunk whose whitespace the splitter rewrote has none rather than a wrong one. `HybridRanker` is reciprocal-rank fusion of the vector rank and a BM25 rank computed over the retrieved candidate pool, not a second index.
  Narrowing `--ext`/`--names` is allowed and removes files that no longer match.
- Searches pass a non-null session id so both backends restrict results to the index's project (the
  Postgres store reads a null session as "every session and project of the user").
- The package carries no `.pdb` files and no symbols package; `Directory.Build.targets` skips
  `IncludeSymbols` for `PackAsTool` projects.
