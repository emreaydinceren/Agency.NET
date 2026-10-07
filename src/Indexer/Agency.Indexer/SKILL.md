---
name: agency-index
description: Semantic search over a folder of docs (Markdown, text, rst, adoc, HTML) via the agency-index CLI; returns where the answer is, as JSON.
when_to_use: Finding information by meaning in many docs, notes or ADRs when the wording is unknown. Not for code, exact identifiers or error strings (grep), nor small folders or ones with an index page (read it).
---

# agency-index

1. `agency-index indexes`: reuse the index whose `root` is this repo, else create one named after the repo folder: `agency-index index --index <name> --root <dir>`. Re-run to refresh; only changes are processed.
2. `agency-index search --index <name> --query "<question>"` returns hits with `path`, `heading`, `start_line`, `end_line`, `score`, no text.
3. `agency-index read --index <name> --path <path> --start <start_line> --end <end_line>` returns just that span.

Rules:
- If `hint` says the top hit leads, read it and stop. Otherwise rephrase once, then grep.
- Missing `start_line` means that file predates line tracking: read the file.
- `search --full` adds chunk text; prefer `read`.
- Never drop or re-point an index you did not create.
- Exit 2: fix the arguments, don't retry. Exit 3: another writer, add `--wait`.

Options, output fields, config: `REFERENCE.md` beside this file.
