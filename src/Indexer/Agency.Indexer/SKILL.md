---
name: agency-index
description: Find where something is explained in a folder of documentation (Markdown, text, reStructuredText, AsciiDoc, HTML) by meaning rather than exact words, using the agency-index CLI. Use for "where is X documented?", "which ADR covers Y?", "how does the project handle Z?", "what do our docs say about W?" when the docs tree is large (more than ~30 files) and you don't know the wording. Not for code, identifiers, error strings or file names (use grep).
---

# agency-index — semantic index for documentation

`agency-index` keeps a named **index** of a directory's text documents and answers semantic queries
against it. Every command prints a single JSON object on stdout, including failures
(`{"status":"error","message":"..."}`); use the exit code to decide what to do with it.

## Prerequisites — check before relying on it

Run `agency-index doctor`: one JSON object whose `status` is `ok` or `problems`, with a `fix` for each
failing check. (It exits 0 either way; `agency-index indexes` is the lighter check.) If the command is
not found, or `doctor` reports `embedding_config`/`endpoint` problems, the tool is not set up (see
[Configuration](#configuration), or `agency-index setup` which does it for you after the user agrees):
don't try to fix it mid-task, use grep.

The first `index` of a large folder can take minutes (about 3 minutes for 55 files on a local model,
over 10 for a few hundred files). Run `agency-index index --index <name> --root <dir> --dry-run` first: it
writes nothing and reports the chunk count and an estimated time. A real run prints progress to
stderr (`indexing 12/340 files, 410 chunks, ~6 min left`) and the JSON result to stdout at the end;
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
   - HTML is reduced to its readable text before indexing.

   Output (`path`s are absolute; `failed` is empty on a clean run, otherwise `{path, reason}` entries with the
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
   agency-index search --index <name> --query "how are releases published?" --top 5
   ```

   `--top` defaults to 5. Output:

   ```json
   {"status":"ok","index":"billing-api","hits":[{"path":"/work/billing-api/docs/releases.md","chunk":2,"score":0.7657,"text":"..."}]}
   ```

   Each hit has an absolute `path`, the `chunk` number within that file, a `score` (cosine similarity,
   higher is better) and the passage `text`.

   **Scores depend on the embedding model, so there is no built-in cutoff.** With
   `text-embedding-qwen3-embedding-0.6b`, real answers score about 0.55–0.75 but an unrelated query still
   tops out near 0.5. If `Search.MinScore` is configured (or you pass `--min-score`), weaker hits are
   already removed. When that leaves nothing, the result is not an error:

   ```json
   {"status":"ok","index":"billing-api","hits":[],"filtered":5,"best_score":0.5138}
   ```

   Nothing relevant is in the docs: fall back to grep. `filtered` (how many hits were dropped) and
   `best_score` (the top score before filtering) appear whenever hits were dropped. With no threshold
   configured, treat a top score near the model's noise floor (see the table under
   [Configuration](#configuration)) as "not in the docs".

   Keep the output small with these options (flags, not config):

   - `--min-score <0..1>`: drop hits below this score; overrides `Search.MinScore`.
   - `--within <0..1>`: keep only hits within this distance of the best score (for example `--within 0.05`);
     needs no per-model calibration.
   - `--no-text`: path, chunk and score only, no passage text; open the file for what you need.
   - `--snippet-chars <N>`: cut each hit's text to at most N characters.

   - Phrase the query as a natural-language question, not keywords.
   - Answer from the returned `text` when it is enough; open the file at `path` only if you need more.
   - If the hits don't answer the question, rephrase once, then fall back to grep. Don't keep re-querying.

3. **Inspect / clean up** (no embedding endpoint needed):

   ```bash
   agency-index indexes                  # every index and its root
   agency-index list --index <name>      # indexed files with size, mtime, chunk count
   agency-index drop --index <name>      # delete the index
   ```

## Naming and partitioning (several repos on one machine)

All repos share one database, so an index name is global to the machine, and `search` only ever looks
inside the single index you name — it never crosses indexes. Keep repos apart like this:

1. **One index per repo, named after the repo folder** (lower-case): `/work/billing-api` → `billing-api`.
   If the repo has a `.agency-index.json`, its `Index` is the name: use it.
2. **Before creating an index, run `agency-index indexes`.** Each entry shows its `root`.
   - An index whose `root` is the current repo already exists → use that name; don't create another.
   - Your intended name exists with a *different* `root` → another repo owns it. Pick a different name
     (for example `<org>-<repo>`). Never `drop` or re-point an index you did not create for this repo.
3. **Always pass `--index <this repo's name>` when searching.** To search several repos, run `search`
   once per index and combine the results yourself.

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
`Index`, `Root` (relative to the file), `Extensions`, `Names` and `MaxFileKb`. Precedence per key: command line,
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
| `text-embedding-3-small` (OpenAI) | 1536 | not measured |

`Search.MinScore` (`AGENCY_INDEX_Search__MinScore`, or `--min-score`) drops hits below a score. It has no
default because scores depend on the model; to calibrate, search an index for something unrelated and set it
slightly above the top score. It is a user-level setting: a repo's `.agency-index.json` cannot set it.

- `Provider` is `sqlite` (default, database at `~/.agency/index.db`) or `postgres` (`Database` is
  then a connection string; requires the pgvector extension).
- There is no default embedding endpoint: until one is configured, `index` and `search` exit 2.
  `agency-index setup --endpoint lmstudio` (add `--yes` to apply) picks the model and measures
  `Dimensions` for you.
  Paths above use POSIX style; on Windows use e.g. `C:\Users\me\.agency\index.db` (Git Bash still
  resolves `~/.agency`). If `agency-index` is "not found" right after installing, add
  `~/.dotnet/tools` (Windows: `%USERPROFILE%\.dotnet\tools`) to `PATH`.
- Any OpenAI-compatible embeddings endpoint works (OpenAI, OpenRouter, LM Studio, Ollama's `/v1`, ...).
- **The API key is a secret: keep it in the environment, never in `indexer.json` or on a command line.**
  Local servers need none. Hosted ones read `OPENAI_API_KEY` (api.openai.com) or `OPENROUTER_API_KEY`
  (openrouter.ai); any other endpoint reads `AGENCY_INDEX_Embedding__ApiKey`. `doctor` tells you when a
  hosted endpoint has no key or a key sits in the config file. Never print or log the key.
- Environment variable form: `AGENCY_INDEX_Provider`, `AGENCY_INDEX_Database`,
  `AGENCY_INDEX_Embedding__BaseUrl`, `AGENCY_INDEX_Embedding__ModelId`, ...
- An index is tied to the embedding model it was built with; switching models requires
  `drop` and a fresh `index`, and the tool refuses to refresh or search an index with another model.
  `agency-index setup` warns about every existing index a model change would orphan (`warnings`, or
  `status: index_model_mismatch` for the index it is about to build) and never drops anything itself.
  Change settings with `setup`, which merges into `indexer.json`; do not overwrite the file by hand.
