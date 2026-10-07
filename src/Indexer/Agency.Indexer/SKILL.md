---
name: agency-index
description: Find where something is explained in a large docs folder (30+ files) by meaning, via the agency-index CLI. For "where is X documented?", "which ADR covers Y?" when you don't know the wording. Not for code, identifiers or error strings (grep).
---

# agency-index

Every command prints one JSON object; failures are `{"status":"error",...}`.

1. `agency-index doctor`: if `status` is not `ok`, the tool isn't set up; use grep.
2. `agency-index indexes`: reuse the index whose `root` is this repo, else `agency-index index --index <repo-name> --root <dir> --dry-run`, tell the user the time estimate, then run it without `--dry-run`.
3. `agency-index search --index <name> --query "<question>"` returns hits with `path`, `heading`, `start_line`, `end_line`, `score`, no text.
4. `agency-index read --index <name> --path <path> --start <start_line> --end <end_line>` returns that span.

- If `hint` appears, read the top hit and stop. Otherwise rephrase once, then grep.
- No `start_line` on a hit: read the file.
- Never `drop` or re-point an index you didn't create.
- Exit 2: fix the arguments, don't retry. Exit 3: another writer, add `--wait`.

Options, scores, setup, removal: `REFERENCE.md` beside this file.
