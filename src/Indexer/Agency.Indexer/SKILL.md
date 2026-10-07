---
name: agency-index
description: Find where something is explained in a large docs folder (30+ files) by meaning, via the agency-index CLI. For "where is X documented?", "which ADR covers Y?" when you don't know the wording. Not for code, identifiers or error strings (grep).
---

# agency-index

1. `agency-index doctor`: if `status` is not `ok`, the tool isn't set up; use grep.
2. `agency-index indexes`: reuse the index whose `root` is this repo, else `agency-index index --index <repo-name> --root <dir> --dry-run`, tell the user the time estimate, then run it without `--dry-run`.
3. `agency-index search --index <name> --query "<question>"` prints `path:line: text` lines, then `[score]` per file. Usually that answers it.
4. Last line: `# top hit leads...` = stop. `# no clear winner` = reword once, or `agency-index read --hit 1` and `--hit 2` (a 40-line window; `--around LINE`, `--all`). `# no match` = not in the docs: grep.

- Name an exact identifier or key in the query and it is matched literally too.
- Never `drop` or re-point an index you didn't create.
- Exit 2: fix the arguments, don't retry. Exit 3: another writer, add `--wait`.
- A `warning:` about an older index: tell the user to run `index --rebuild`.

Flags, `--json`, scores, setup, removal: `REFERENCE.md` beside this file.
