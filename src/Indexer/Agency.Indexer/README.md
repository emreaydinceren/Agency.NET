# Agency.Indexer (`agency-index`)

A .NET global tool that gives agents incremental semantic search over a folder of documentation.
It indexes text files (Markdown, plain text, reStructuredText, AsciiDoc, HTML) into SQLite or
PostgreSQL/pgvector through `IVectorStore`, re-embedding only files whose size or modification time
changed, and answers queries with grep-style `path:line: text` lines (or JSON with `--json`).

## Install

```bash
dotnet tool install -g AgencyDotNet.Indexer && agency-index install-skill
```

For the full walk-through (skill scope, embeddings endpoint, `PATH`, first index, an agent runbook) see
`docs/Install-Indexer-Skill.md` in the repository.

`install-skill` writes the bundled `SKILL.md` and `REFERENCE.md` to `~/.claude/skills/agency-index/` (Claude Code) and
`~/Agents/skills/agency-index/` (Agency harness); pass `--dir <skills-root>` to choose another location.

## Use

```bash
agency-index index  --index docs --root ./docs
agency-index search --index docs --query "how are releases published?"
```

With a `.agency-index.json` at the repo root (`{ "Index": "docs", "Root": "docs" }`), `index` and `search`
work from any folder of the repo without `--index`/`--root`; only those index settings are read from it.

`search` is compact (paths, headings and line spans, no text); fetch a span with
`agency-index read --index docs --path <file> --start <start_line> --end <end_line>`.
See `REFERENCE.md` for the full command reference, exit codes and configuration, and `SKILL.md` for the short
agent entry point.

## How it works

- **Delta:** the manifest (an `IKVStore`) records each file's size, last-write time and chunk count.
  A run re-chunks and re-embeds only files that were added or whose size or last-write time changed,
  and removes files that disappeared. Contents of unchanged files are never read.
- **Replace:** each changed file is written with `IVectorStore.ReplaceDocumentAsync` — chunks
  embedded in sequential requests of at most 32, upserted, then the file's stale chunks deleted.
- **Single writer:** SQLite uses an exclusively opened `<db>.<index>.lock` file; PostgreSQL uses a
  session advisory lock. Both are released automatically if the process dies. SQLite runs in WAL
  mode so searches never block on a running index.
