# agency-index reference

The short entry point is `SKILL.md`; this is the full detail, loaded on demand.

`agency-index` keeps a named **index** of a directory's text documents and answers semantic queries
against it. Every command prints a single JSON object on stdout, including failures
(`{"status":"error","message":"..."}`), except `search` and `read`, which print plain text (add `--json` for
JSON); use the exit code to decide what to do with it.

## Prerequisites — check before relying on it

Run `agency-index doctor`: one JSON object whose `status` is `ok` or `problems`, with a `fix` for each
failing check. (It exits 0 either way; `agency-index indexes` is the lighter check.) If the command is
not found, or `doctor` reports `embedding_config`/`endpoint` problems, the tool is not set up (see
[Configuration](#configuration), or `agency-index setup` which does it for you after the user agrees):
don't try to fix it mid-task, use grep.

The first `index` of a large folder can take minutes (about 3 minutes for 55 files on a local model,
over 10 for a few hundred files). Run `agency-index index --index <name> --root <dir> --dry-run` first: it
writes nothing and reports the chunk count and an estimated time. A real run prints progress to
stderr (`indexing 12/340 files, 410/1980 chunks, ~6 min left`) and the JSON result to stdout at the end;
tell the user the estimate first, and run it in the background or with a long timeout. Refreshes after
that take seconds.

## When to use it — and when not

| Question | Use | Why |
|----------|-----|-----|
| "How is X decided?", "why did we choose Y?", "where do the docs explain Z?" and you don't know the wording | `agency-index search` | One call returns the few relevant passages; grep for a vague word like "release" returns hundreds of lines and several file reads. |
| Exact identifier, error string, config key or file name | grep | Cheaper and exact. |
| Anything about code | grep / read | Only documentation is indexed. |
| You know which file or folder it is in | read it / grep there | No index needed. |

If the docs have an index page (e.g. `docs/Home.md`), read it first; if it doesn't answer the question
in one hop, search. Roughly 30+ documents is where search starts paying for itself.

## Workflow

1. **Index (or refresh) the folder.** Safe and cheap to run before every search session — only added,
   changed or deleted files are processed.

   ```bash
   agency-index index --index <name> --root <dir>
   ```

   - `<name>`: letters, digits, `.`, `_`, `-` (case-insensitive). One index = one root directory.
   - After the first run `--root` may be omitted: `agency-index index --index <name>`.
   - If the repo has a `.agency-index.json` (`agency-index doctor` shows `repo_config` and `defaults`), omit
     `--index` and `--root` entirely: they are read from it, from any folder of the repo.
   - Default file types: `.md .markdown .mdx .txt .rst .adoc .html .htm` plus `README`, `CHANGELOG`,
     `CONTRIBUTING`. Override with `--ext .md,.html` and/or `--names README,NOTES`. The selection is
     remembered; files that stop matching are removed from the index.
   - Skipped automatically: `.git`, `node_modules`, `bin`, `obj`, `dist`, and files over 1 MB
     (`--max-file-kb` to change).
   - `--exclude "dir,**/*.draft.md"` (or `Exclude` in `.agency-index.json`) leaves out gitignore-style globs
     relative to the root; use it for generated or log-like folders that crowd out real answers. It is
     remembered, and `--dry-run` shows the effect first.
   - HTML is reduced to its readable text before indexing.

   Output (file lists are relative to `root`, `--summary` prints counts instead; `failed` is empty on a clean run, otherwise `{path, reason}` entries with the
   error text, e.g. the HTTP status and response body of a refused embedding). Each failure is also printed to
   stderr as `FAILED <path>: <reason>` the moment it happens, and `--log <file>` appends the progress and
   failure lines to a file with timestamps. Transient embedding errors (timeouts, 429, 5xx, connection errors)
   are retried 3 times with a 1, 2, 4 second wait before the file is marked failed; failed files are retried
   by the next run:

   ```json
   {"status":"ok","index":"billing-api","root":"/work/billing-api/docs","added":["..."],"changed":[],"removed":[],"unchanged":54,"skipped_too_large":[],"failed":[],"chunks_written":3,"duration_ms":2835}
   ```

2. **Search.**

   ```bash
   agency-index search --index <name> --query "how are releases published?"
   ```

   Default output (`--format lines`) is grep-shaped: per file, the lines closest to the question as
   `path:line: text` (paths relative to the index root, `/` separators, true line numbers), then one summary line, then
   a verdict:

   ```text
   adr/0015-adapter-change.md:29: Changing the Adapter clears both Model and Effort, and says so inline.
     [score 0.63, +3.1 sd] Adapters > Consequences
   Huddle.UserGuide.md:352: messages already posted keep the old name, because the transcript records what
     [score 0.60, +2.4 sd] Renaming a Teammate
   # top hit leads by 0.03; read it and stop
   ```

   - `[score s, +z sd]`: `s` is the cosine similarity of the file's best passage; `z` is how many standard deviations
     it sits above the best scores of unrelated queries against this index (measured after every `index` run, so
     scores are comparable across queries and indexes). The heading path of the passage follows.
   - Last line, one of: `# top hit leads by X; read it and stop` (the top file leads the next one by at least 1.5
     noise standard deviations, or 0.03 when the index has no noise statistics: a heuristic, not tuned per model),
     `# no clear winner; reword or open two`, or `# no match above min score (0.54)`. The last means nothing cleared
     the minimum (`Search:MinScore`, `--min-score`, a calibrated value, or else the noise ceiling): the topic is not
     in the docs. Exit code is 0 in all three cases.
   - `# ignored words common to every page: huddle` means words found in at least 75% of the files were left out of the
     embedded query, so a project name does not lift every score. `--raw-query` embeds the query as typed.
   - Retrieval fuses the vector ranking with a keyword (BM25) ranking of the files, reciprocal-rank fusion, so an
     exact term (`LibraryPathResolver`, `sprints.md`) is found even when the embedding drifted; a query word that looks
     like an identifier counts as one more first-place vote. `--vector-only` turns the keyword side off.
   - Which lines: the passages of the best files are cut into lines and the lines are embedded in one batch call
     (plus one more call for keyword-only passages), so the printed lines are the semantically closest ones.

   Options:

   - `--top <N>` files (default 4), `--per-file <N>` passages considered per file (default 2, `--group-by-file` is 1),
     `--lines <N>` printed per file (default 2), `--max-line-chars <N>` (default 200), `--highlight` (marks query words
     with `**`; off by default to save characters).
   - `--path <glob>`: only files whose path under the index root matches (`--path adr`).
   - `--min-score <0..1>` and `--within <0..1>` as before.
   - `--index a,b`: search several indexes (same embedding model); paths are then prefixed `index:`.
   - `--log <file>`: append one JSON line per search (`time`, `query`, `flags`, `chars`, `top_score`), so a benchmark can
     count calls and payload.

   **JSON output** (`--json` or `--format json`) is the schema of 0.1.221, unchanged: `hits` with `path`, `chunk`,
   `score`, `heading`, `start_line`, `end_line` (`text` with `--full` or `--snippet-chars <N>`), `top_gap`, `hint`,
   `filtered`/`best_score`. Its options: `--top` (default 5), `--min-score`, `--within`, `--full`, `--no-text`,
   `--snippet-chars`, `--path`, `--group-by-file`/`--per-file`, `--hybrid` (rerank the vector candidates by keyword
   match; identifiers are exempt from `--min-score`), `--index a,b`.

   **Read:**

   ```bash
   agency-index read --hit 1                          # the first file block of the last search in this folder
   agency-index read --index <name> --path <path> --around <line>
   agency-index read --index <name> --path <path> --start <line> --end <line>
   ```

   Plain text: a header `# path lines 10-49 of 321` (plus `[stale: ...]` when the file changed since indexing, so line
   numbers may have drifted: re-run `index`), then `N: text` lines. Without a range you get the first 40 lines; with
   `--around LINE` (or `--hit`) 40 lines centred on it; `--window N` changes 40; `--all` returns the whole file;
   `--start/--end` returns that range (at most 400 lines). `--json` gives `path`, `start_line`, `end_line`,
   `total_lines`, `stale`, `text`. `path` must be a file of the index, absolute or relative to its root. The last
   search is remembered per working folder in `~/.agency/last-search/`.

   **Scores depend on the embedding model**, so there is no built-in cutoff beyond the noise ceiling. With
   `text-embedding-qwen3-embedding-0.6b`, real answers score about 0.55-0.75 but an unrelated query still tops out near
   0.5. Real answers to everyday-word questions can sit only a little above that, so a hand-picked `Search:MinScore`
   can hide a correct page: measure it with `calibrate --questions` (see [Configuration](#configuration)).

   - Phrase the query as a natural-language question, not keywords.
   - If the lines don't answer the question, rephrase once, then fall back to grep. Don't keep re-querying.

3. **Inspect / clean up** (no embedding endpoint needed):

   ```bash
   agency-index indexes                  # every index and its root
   agency-index list --index <name>      # indexed files with size, mtime, chunk count (--summary: counts only)
   agency-index drop --index <name>      # delete the index
   ```

## Naming and partitioning (several repos on one machine)

All repos share one database, so an index name is global to the machine, and `search` only ever looks
inside the indexes you name. Keep repos apart like this:

1. **One index per repo, named after the repo folder** (lower-case): `/work/billing-api` → `billing-api`.
   If the repo has a `.agency-index.json`, its `Index` is the name: use it. Exclude noisy folders before
   thinking of a second index; a second, narrower index (`billing-api-adr`) is justified only when you keep
   asking about one body of documents and want it ranked on its own.
2. **Before creating an index, run `agency-index indexes`.** Each entry shows its `root`.
   - An index whose `root` is the current repo already exists → use that name; don't create another.
   - Your intended name exists with a *different* `root` → another repo owns it. Pick a different name
     (for example `<org>-<repo>`). Never `drop` or re-point an index you did not create for this repo.
3. **Always pass `--index <this repo's name>` when searching.** `--index a,b` searches several indexes
   at once (they must use the same embedding model) and merges the hits; each hit names its `index`.

If `index` fails with exit code 2 saying the index is bound to another root, the name is taken —
choose another name; do not retry the same one.

## Exit codes

| Code | Meaning | What to do |
|------|---------|------------|
| 0 | Success | Parse stdout. |
| 1 | Failure. For `index`, `failed` lists files that could not be indexed; everything else was applied. | Read `message` / `failed` from the JSON and report them. Failed files are retried on the next run. |
| 2 | Usage or configuration error (unknown index, missing option, root mismatch, no embedding endpoint, model changed). | Fix the arguments as the `message` says. Do not retry unchanged. If it is "no embedding endpoint", fall back to grep. |
| 3 | Another process is indexing this index right now. | Searching is still safe. To index anyway, re-run with `--wait` to block until the other writer finishes. |

## Concurrency

Only one process writes an index at a time; others get exit code 3 (or wait with `--wait`).
Searches never block and may run while an index is being refreshed.

## Removing it (when the user asks)

Use `agency-index uninstall`; it previews by default and changes nothing without `--yes`.

1. Run `agency-index uninstall` and show the user the JSON: the skill files, each index with
   `this_repo` (does its `root` lie inside this repo?), the database files, the config file and
   `remaining`. The default scope `repo` only touches this repo's indexes and skill; `--scope all` also
   covers every other repo's indexes, every skill copy, the SQLite database files (one file holding all
   repos' indexes, default `~/.agency/index.db`) and `~/.agency/indexer.json`.
2. Ask which scope they want, and get an explicit yes before `agency-index uninstall --yes`
   (`--scope all --yes` for everything). `all` drops other repos' indexes: say so.
3. Exit 1 with `locked` means another run is writing an index; nothing shared was deleted. Retry later.
4. It never removes the tool. Report `remaining` and run `dotnet tool uninstall -g AgencyDotNet.Indexer`
   only if the user says so. The API key variable (`OPENAI_API_KEY` / `OPENROUTER_API_KEY` /
   `AGENCY_INDEX_Embedding__ApiKey`) is theirs to remove. On PostgreSQL the tables stay; leave them.

`uninstall` also deletes the repo's own `.agency-index.json`. The full description is in
`docs/Install-Indexer-Skill.md` ("Remove it") in the Agency repository.

## Repo config

A repo may have `.agency-index.json` (found by walking up from the current folder; the nearest wins) with
`Index`, `Root` (relative to the file), `Extensions`, `Names`, `MaxFileKb` and `Exclude`. Precedence per key: command line,
`AGENCY_INDEX_*` environment, repo file, `~/.agency/indexer.json`, default. **Only those keys are read from the
repo file**: never put an endpoint, database or key in it; they are ignored, and `doctor` reports them.
`agency-index setup --index <name> --root <dir> --yes` writes it.

## Configuration

Settings come from command-line options, then `AGENCY_INDEX_*` environment variables, then
`~/.agency/indexer.json`:

```json
{
  "Provider": "sqlite",
  "Database": "/home/me/.agency/index.db",
  "Embedding": { "BaseUrl": "http://localhost:1234/v1", "ModelId": "text-embedding-qwen3-embedding-0.6b", "Dimensions": 1024 },
  "Search": { "MinScore": 0.55 }
}
```

`Dimensions` must equal the model's vector length (default 1024 when omitted); a wrong value breaks
the index.

| Model | Dimensions | Noise floor (top score of an unrelated query) |
|-------|------------|-----------------------------------------------|
| `text-embedding-nomic-embed-text-v1.5` | 768 | not measured |
| `text-embedding-qwen3-embedding-0.6b` | 1024 | about 0.5 (0.51 on a 102-file docs tree) |
| `text-embedding-qwen3-embedding-4b` | 2560 | not measured; run `calibrate` |
| `text-embedding-3-small` (OpenAI) | 1536 | not measured |

`Search.MinScore` (`AGENCY_INDEX_Search__MinScore`, or `--min-score`) drops hits below a score. It has no
default because scores depend on the model. **Calibration:** `agency-index calibrate --index <name>` searches twelve
unrelated queries and reports the noise ceiling and a suggestion just above it. That is a guess from unrelated
queries only, and it can sit above the score of a correct page. Give it real questions instead:

```bash
agency-index calibrate --index <name> --questions questions.json [--save]
# questions.json: [{"question": "After I relabel a coworker, where do its earlier exchanges end up?", "expected_path": "adr/0011-a-rename.md"}]
```

It reports `noise_ceiling`, `answer_floor` (10th percentile score of the expected files), `answers_at_or_below_ceiling`,
`missed` (expected file not in the best 200 hits) and a `suggested_min_score` halfway through the gap, or a `warning`
when the two overlap, in which case no threshold is suggested or saved: rely on the normalized score and the `# no match`
line. `--save` stores the noise statistics and, when there is a gap, the threshold and answer floor; `doctor` warns when
a configured minimum score is above the saved answer floor. It is a user-level setting: a repo's `.agency-index.json` cannot set it.

- `Provider` is `sqlite` (default, database at `~/.agency/index.db`) or `postgres` (requires a server with the
  pgvector extension). For PostgreSQL the connection string contains the password, so it is a secret like the API
  key: set it in the `AGENCY_INDEX_Database` environment variable, never in `indexer.json` (`doctor` flags it as
  `database_credentials`) and never print it. `setup --provider postgres` saves only `"Provider": "postgres"`.
  Without a connection string every command fails with a clear error instead of using SQLite. The vector column
  width is fixed when the tables are first created, so a model with a different size needs a new database.
- There is no default embedding endpoint: until one is configured, `index` and `search` exit 2.
  `agency-index setup --endpoint lmstudio` (add `--yes` to apply) picks the model and measures
  `Dimensions` for you.
  Paths above use POSIX style; on Windows use e.g. `C:\Users\me\.agency\index.db` (Git Bash still
  resolves `~/.agency`). If `agency-index` is "not found" right after installing, add
  `~/.dotnet/tools` (Windows: `%USERPROFILE%\.dotnet\tools`) to `PATH`.
- **Index format.** New and rebuilt indexes are *passage-level* (format 2): each file is cut into passages of at most
  `PassageLines` (default 6) non-blank lines, `PassageOverlap` (default 1) lines shared with the next, never across a
  Markdown heading, each embedded together with its heading path and stored with its exact line range. Set them in
  `indexer.json` (`"PassageLines"`, `"PassageOverlap"`); changing them is refused until `index --rebuild`. An index
  built before this (format 1, chunk-level) keeps working and is refreshed in its own format; `search` prints a
  `warning:` on stderr and re-ranks the lines of the chunks at query time, and `doctor` reports `index_format`. Passages
  multiply the chunk count by roughly four to six, so a first index and the database are that much bigger and slower;
  refreshes only touch changed files. HTML files keep the splitter's chunks (no line numbers).
- Any OpenAI-compatible embeddings endpoint works (OpenAI, OpenRouter, LM Studio, Ollama's `/v1`, ...).
- **The API key is a secret: keep it in the environment, never in `indexer.json` or on a command line.**
  Local servers need none. Hosted ones read `OPENAI_API_KEY` (api.openai.com) or `OPENROUTER_API_KEY`
  (openrouter.ai); any other endpoint reads `AGENCY_INDEX_Embedding__ApiKey`. `doctor` tells you when a
  hosted endpoint has no key or a key sits in the config file. Never print or log the key.
- Environment variable form: `AGENCY_INDEX_Provider`, `AGENCY_INDEX_Database`,
  `AGENCY_INDEX_Embedding__BaseUrl`, `AGENCY_INDEX_Embedding__ModelId`, ...
- An index is tied to the embedding model it was built with; switching models is
  `agency-index index --index <name> --rebuild`, and the tool refuses to refresh or search an index with
  another model until then.
  `agency-index setup` warns about every existing index a model change would orphan (`warnings`, or
  `status: index_model_mismatch` for the index it is about to build) and never drops anything itself.
  Change settings with `setup`, which merges into `indexer.json`; do not overwrite the file by hand.
