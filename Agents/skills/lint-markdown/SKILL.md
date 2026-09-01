---
name: lint-markdown
description: >
  Lint and fix Markdown files according to markdownlint rules. Use this skill
  whenever the user wants to lint, fix, clean up, or improve the formatting of
  a Markdown document. Triggers on phrases like "lint this markdown", "fix my
  markdown", "clean up this .md file", "check markdown rules", "markdownlint",
  "make this markdown compliant", or any time a .md file has formatting issues
  that need correcting. Always use this skill rather than guessing at markdown
  style conventions — the rules here are authoritative.
---

# Lint-Markdown Skill

You are a precise Markdown linter and fixer. Your job is to read a Markdown
document, apply the markdownlint rules, and return a corrected version with
a clear summary of every change made.

## Step 1 — Check the rules cache

Before linting, read `references/cache-meta.json` (in this skill's directory)
to determine when the rules were last fetched.

- Compute the number of days between `cached_at` and today's date.
- If that number is **≥ 15**, fetch the source URL and compare it to the
  content of `references/Rules.md`. If the content differs, overwrite
  `references/Rules.md` with the new content and update `cached_at` to today.
  Tell the user: "Rules updated from upstream (markdownlint v0.40.0 source)."
- If the cache is fresh (< 15 days old), proceed without fetching.

## Step 2 — Read the rules

Read `references/Rules.md` in full. The file documents every markdownlint rule
(MD001–MD060) with its rationale, parameters, and corrected/incorrect examples.

You don't need to apply every rule mechanically — understand *why* each rule
exists so you can make intelligent fixes rather than blind substitutions.

Key rules to apply by default (the most impactful and universally applicable):

| Rule | What it checks |
|------|---------------|
| MD001 | Heading levels increment by one at a time |
| MD003 | Consistent heading style (ATX by default) |
| MD004 | Consistent unordered list marker style |
| MD007 | Unordered list indentation (2 spaces) |
| MD009 | No trailing spaces |
| MD010 | No hard tabs |
| MD011 | No reversed link syntax `(text)[url]` |
| MD012 | No multiple consecutive blank lines |
| MD018 | Space after `#` in ATX headings |
| MD019 | Single space after `#`, not multiple |
| MD022 | Headings surrounded by blank lines |
| MD023 | Headings start at beginning of line |
| MD025 | Single top-level heading per document |
| MD026 | No trailing punctuation in headings |
| MD031 | Fenced code blocks surrounded by blank lines |
| MD032 | Lists surrounded by blank lines |
| MD034 | No bare URLs (use `<url>` or `[text](url)`) |
| MD037 | No spaces inside emphasis markers |
| MD038 | No spaces inside code span backticks |
| MD039 | No spaces inside link text brackets |
| MD040 | Fenced code blocks have a language specified |
| MD041 | File starts with a top-level heading |
| MD047 | File ends with a single newline |

If the user specifies particular rules to apply or skip, respect that. If no
guidance is given, apply all rules in Rules.md that have clear, unambiguous
fixes.

## Step 3 — Lint the document

Go through the document systematically. For each violation found:

1. Note the rule ID and alias (e.g., `MD022 / blanks-around-headings`)
2. Note the line number or context
3. Apply the fix inline

Do **not** change the meaning, wording, or structure of the content. Only fix
formatting issues the rules identify. If a fix would require a judgment call
that changes content meaning (e.g., MD043 required heading structure), flag it
as a comment rather than silently altering the document.

## Step 4 — Return the result

Provide two things:

### 1. Linted document

Return the fully corrected Markdown document, ready to use. If the input was
provided inline, return it inline. If the input was a file, write the corrected
version back to that file (or a new file if the user asked for a copy).

### 2. Change summary

After the document, list every change made in this format:

```
Changes applied:
- [MD009] Line 14: Removed trailing whitespace
- [MD022] Line 23: Added blank line before heading "## Installation"
- [MD040] Line 31: Added language specifier `bash` to fenced code block
- [MD047] EOF: Added trailing newline
```

If no violations were found, say so clearly: "No markdownlint violations found."

## Notes on judgment calls

- **MD013 (line length)**: This rule defaults to 80 characters, which is often
  too strict for prose-heavy documents. Skip it unless the user explicitly asks
  for line-length enforcement, or the document has egregiously long lines (>200
  chars).
- **MD033 (inline HTML)**: Don't remove HTML that serves a structural purpose
  (e.g., `<br>`, `<details>`, alignment attributes). Flag it instead.
- **MD041 (first line heading)**: If the document is a fragment or template that
  intentionally lacks a top-level heading, note this rather than adding one.
- **Obsidian/wiki links** (`[[link]]`, `![[embed]]`): These are not standard
  Markdown. Don't treat them as violations — leave them untouched.

