---
name: agency-index
description: Semantic search over a folder of documentation (Markdown, text, reStructuredText, AsciiDoc, HTML) through the agency-index CLI. Indexes incrementally — only files whose size or modification time changed are re-embedded — and returns the most relevant passages as JSON.
when_to_use: To find information by meaning in a large set of documentation, notes or ADRs when you don't know the exact wording. Not for code, exact identifiers or error strings (use grep), and not for small folders or ones with an index page (read it).
---

# agency-index — semantic index for documentation

`agency-index` keeps a named **index** of a directory's text documents and answers semantic queries
against it. Every command prints a single JSON object on stdout; check the exit code before parsing.

## When to use it — and when not

- **Use it** for questions about meaning in prose ("how is X decided?", "why did we choose Y?") when you
  don't know the exact wording, or there are too many documents to skim.
- **Don't** use it for exact identifiers, error strings or file names: grep is cheaper and exact.
- **Don't** use it for code: only documentation is indexed.
- **Skip it** if the folder is small or has an index page (e.g. `docs/Home.md`); read that instead.

## Workflow

1. **Index (or refresh) the folder.** Safe and cheap to run before every search session — only added,
   changed or deleted files are processed.

   ```bash
   agency-index index --index <name> --root <dir>
   ```

   - `<name>`: letters, digits, `.`, `_`, `-` (case-insensitive). One index = one root directory.
   - After the first run `--root` may be omitted: `agency-index index --index <name>`.
   - Default file types: `.md .markdown .mdx .txt .rst .adoc .html .htm` plus `README`, `CHANGELOG`,
     `CONTRIBUTING`. Override with `--ext .md,.html` and/or `--names README,NOTES`. The selection is
     remembered; files that stop matching are removed from the index.
   - Skipped automatically: `.git`, `node_modules`, `bin`, `obj`, `dist`, and files over 1 MB
     (`--max-file-kb` to change).
   - HTML is reduced to its readable text before indexing.

2. **Search.**

   ```bash
   agency-index search --index <name> --query "how are releases published?" --top 5
   ```

   Each hit has `path`, `chunk`, `score` (cosine similarity, 0–1, higher is better) and `text`.

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
| 1 | Failure. For `index`, `failed` lists files that could not be indexed; everything else was applied. | Report the `message` / `failed` entries. Failed files are retried on the next run. |
| 2 | Usage or configuration error (unknown index, missing option, root mismatch, no embedding endpoint, model changed). | Fix the arguments as the `message` says. Do not retry unchanged. |
| 3 | Another process is indexing this index right now. | Searching is still safe. To index anyway, re-run with `--wait` to block until the other writer finishes. |

## Concurrency

Only one process writes an index at a time; others get exit code 3 (or wait with `--wait`).
Searches never block and may run while an index is being refreshed.

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

- `Provider` is `sqlite` (default, database at `~/.agency/index.db`) or `postgres` (`Database` is
  then a connection string; requires the pgvector extension).
- Any OpenAI-compatible embeddings endpoint works (OpenAI, LM Studio, Ollama's `/v1`, ...).
- Environment variable form: `AGENCY_INDEX_Provider`, `AGENCY_INDEX_Database`,
  `AGENCY_INDEX_Embedding__BaseUrl`, `AGENCY_INDEX_Embedding__ModelId`, ...
- An index is tied to the embedding model it was built with; switching models requires
  `drop` and a fresh `index`.
