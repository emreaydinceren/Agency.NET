# agency-index reference

Every command prints one JSON object on stdout; check the exit code before parsing.

## index

`agency-index index --index <name> --root <dir> [--ext .md,.html] [--names README,NOTES] [--max-file-kb N] [--wait]`

- `<name>`: letters, digits, `.`, `_`, `-` (case-insensitive). One index = one root directory.
- After the first run `--root` may be omitted.
- Default types: `.md .markdown .mdx .txt .rst .adoc .html .htm` plus `README`, `CHANGELOG`,
  `CONTRIBUTING`. The selection is remembered; files that stop matching are removed from the index.
- Skipped automatically: `.git`, `node_modules`, `bin`, `obj`, `dist`, files over 1 MB.
- HTML is reduced to readable text; its hits carry no line numbers because the text differs from the file.
- Files indexed before line tracking existed keep their old chunks (no `heading`/`start_line`) until they
  change. `drop` and re-`index` to refresh them all.

## search

`agency-index search --index <name> --query "<question>" [--top 5] [--full]`

- Phrase the query as a natural-language question, not keywords.
- Output: `best_score`, `top_gap` and `hint` (only when at least two hits and the top hit leads the
  runner-up by 0.08 or more; a heuristic, not tuned per embedding model), then `hits`.
- Each hit: `path`, `chunk`, `score` (cosine similarity, 0-1), and when known `heading` (nearest Markdown
  heading), `start_line`, `end_line` (1-based, in the file). `text` only with `--full`.
- Line spans are matched back from the chunk text, so an occasional chunk has none.

## read

`agency-index read --index <name> --path <file> [--start 1] [--end N]`

Returns `path`, `start_line`, `end_line`, `total_lines`, `stale` and `text`. At most 400 lines per call.
`path` must be a file of the index. `stale: true` means the file changed since indexing, so line numbers
from search may have drifted; re-run `index`.

## Inspect and clean up (no embedding endpoint needed)

```bash
agency-index indexes                  # every index and its root
agency-index list --index <name>      # indexed files with size, mtime, chunk count
agency-index drop --index <name>      # delete the index
```

## Naming and partitioning (several repos on one machine)

All repos share one database, so an index name is global to the machine, and `search` only looks inside the
index you name.

1. One index per repo, named after the repo folder (lower-case): `/work/billing-api` is `billing-api`.
2. Before creating an index, run `agency-index indexes`. An index whose `root` is the current repo exists:
   use it. Your name exists with a different `root`: another repo owns it, pick another (for example
   `<org>-<repo>`). Never `drop` or re-point an index you did not create for this repo.
3. Always pass `--index <this repo's name>` when searching. To search several repos, search each index and
   combine the results yourself.

If `index` fails with exit code 2 saying the index is bound to another root, the name is taken: choose another.

## Exit codes

| Code | Meaning | What to do |
|------|---------|------------|
| 0 | Success | Parse stdout. |
| 1 | Failure. For `index`, `failed` lists files that could not be indexed; the rest was applied. | Report `message` / `failed`. Failed files are retried on the next run. |
| 2 | Usage or configuration error (unknown index, missing option, root mismatch, no embedding endpoint, model changed, `read` path not in the index). | Fix the arguments as `message` says. Do not retry unchanged. |
| 3 | Another process is indexing this index. | Searching is still safe. To index anyway, re-run with `--wait`. |

## Concurrency

One process writes an index at a time; others get exit code 3 (or wait with `--wait`). Searches and reads
never block and may run while an index is being refreshed.

## Configuration

Settings come from command-line options, then `AGENCY_INDEX_*` environment variables, then
`~/.agency/indexer.json`:

```json
{
  "Provider": "sqlite",
  "Database": "/home/me/.agency/index.db",
  "Embedding": { "BaseUrl": "http://localhost:1234/v1", "ModelId": "text-embedding-nomic-embed-text-v1.5", "ApiKey": "unused", "Dimensions": 768 }
}
```

- `Provider` is `sqlite` (default, database at `~/.agency/index.db`) or `postgres` (`Database` is then a
  connection string; requires the pgvector extension).
- Any OpenAI-compatible embeddings endpoint works (OpenAI, LM Studio, Ollama's `/v1`, ...).
- Environment form: `AGENCY_INDEX_Provider`, `AGENCY_INDEX_Database`, `AGENCY_INDEX_Embedding__BaseUrl`,
  `AGENCY_INDEX_Embedding__ModelId`, ...
- An index is tied to the embedding model it was built with; switching models requires `drop` and a fresh `index`.
