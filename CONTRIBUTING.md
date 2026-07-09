# Contributing to Agency

Thanks for considering a contribution. Agency is pre-1.0 and the API is still
settling, so a little coordination up front saves rework on both sides.

## Before you start

- **Non-trivial changes:** open an issue first and sketch the approach before
  writing code. It's much cheaper to redirect a plan than a finished PR.
- **Small, well-scoped fixes** (typos, small bugs, a missing test, a thin
  README): a PR straight away is fine.
- Looking for a starting point? Issues labeled `good-first-issue` are
  small, self-contained, and come with clear acceptance criteria.

## Building and testing locally

Full commands live in [`Agents/BuildAndTest.md`](Agents/BuildAndTest.md); the
essentials:

```bash
dotnet build src/Agency.slnx
dotnet test src/Agency.slnx --filter "Category!=Functional"
```

That unit-test filter is what you should run before opening a PR — it needs
no external services and is what CI runs on your PR too.

### About functional tests

Some tests are tagged `[Trait("Category", "Functional")]` and exercise a real
LLM endpoint through a home-lab HTTP cache proxy. **Public CI does not run
these** — the proxy and LLM aren't reachable from a public runner, so your PR
will only show unit-test results, not the full suite. That's expected, not a
gap in your PR. If your change touches code exercised by functional tests,
say so in the PR description; a maintainer will run them locally before merge.

### PostgreSQL for integration-style tests

Some unit tests exercise the Postgres-backed stores and need a local instance:

```bash
cd src && docker-compose up -d
```

Credentials and connection details are in
[`Agents/BuildAndTest.md`](Agents/BuildAndTest.md).

## Coding style

Formatting and analyzer rules are enforced by [`.editorconfig`](.editorconfig)
(indentation, brace style, naming, `dotnet_diagnostic` severities) — most
editors pick this up automatically. Beyond formatting, see
[`Agents/CSharpPrinciples.md`](Agents/CSharpPrinciples.md) for the project's
conventions (immutability, constructor injection, `Result<T>` vs. exceptions,
async hygiene, and more). New code should follow these; changes to unrelated
existing code to "fix" style are best left for a separate PR.

**Line endings:** `*.cs` files are pinned to CRLF via [`.gitattributes`](.gitattributes),
matching `.editorconfig`'s `end_of_line = crlf`. This isn't cosmetic — several
prompts used by functional tests are built from verbatim string literals, so
their line endings are baked into the compiled request bytes and the
functional-test cache is keyed on an exact hash of those bytes. A stray LF
conversion (common if your editor or `core.autocrlf` normalizes on save)
changes the hash and silently breaks the cache for that test. Git should
apply the CRLF rule automatically on checkout; if your diff shows whole-file
line-ending changes, check `git config core.autocrlf` before committing.

## Pull request process

1. Fork/branch, make your change, and make sure `dotnet test src/Agency.slnx
   --filter "Category!=Functional"` passes locally.
2. Fill out the PR template — description, related issue, and the checklist.
3. Keep PRs focused: one logical change per PR is much easier to review than
   a bundle of unrelated fixes.
4. A maintainer will review, may run functional tests locally if your change
   touches that surface, and will merge once CI and review are green.

## Code of Conduct

This project follows the [Contributor Covenant](CODE_OF_CONDUCT.md). By
participating, you're expected to uphold it.

## Reporting security issues

Please don't file public issues for security vulnerabilities — see
[`SECURITY.md`](SECURITY.md) for private disclosure instructions.
