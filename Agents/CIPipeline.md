# CI Pipeline

How the Gitea Actions CI works, how to investigate a failing run, and the non-obvious
failure modes that have already cost a debugging session. Read this **before** diving into
a red CI run — most failures here are environmental, not code regressions.

## Topology

- **Host:** self-hosted Gitea at `http://gitea-host.example:3000` (repo `emre/Agency`). Actions
  run on a runner labelled `dotnet-10` inside the `mcr.microsoft.com/dotnet/sdk:10.0` Linux
  container, `shell: bash`.
- **Workflows** (`.gitea/workflows/`):
  - `ci-pr.yaml` — on PRs to `main`. Steps: Checkout → Restore → Build → Unit Tests → Functional Tests.
  - `ci-main.yaml` — on push to `main`. Same test steps, then version gate → third-party
    notices → NuGet pack → publish.
  - Both `paths-ignore: docs/**`.
- **Backing services reachable from the runner** (the runner is hosted on the
  `runner-host.example` machine, so these resolve):
  - PostgreSQL — via `ConnectionStrings__PostgreSql` secret.
  - HTTP cache proxy — `http://runner-host.example:12345` (the **standalone `Agency.HttpCacheProxy` repo**).
  - LM Studio — `http://llm.test:1234` (proxy's upstream on a miss).

## Checkout is manual

Neither workflow uses `actions/checkout`. They `git clone --depth=1 --branch <ref>` with a
token-injected URL, then `checkout $GITHUB_SHA`. Implications:

- It is always a **fresh clone** each run — no warm working tree, no cached git config.
- **Line-ending normalization is governed entirely by `.gitattributes`** (there is no
  per-runner `core.autocrlf` you can rely on). See the EOL gotcha below — this matters.

## Tests

- **Unit:** `dotnet test --filter "Category!=Functional"`.
- **Functional:** `dotnet test --filter "Category=Functional&Category!=Cloud" -- RunConfiguration.MaxCpuCount=1`.
- Both steps wrap the command in a **3-attempt retry loop** (`for i in 1 2 3`). So:
  - A test that fails **all 3 attempts** is a real/deterministic failure, not flake.
  - The log contains up to 3 full failure blocks — read the *last* one for the final verdict,
    and look for `Attempt N failed, retrying...` to count attempts.
- `RunConfiguration.MaxCpuCount=1` serializes test **assemblies**. This is mandatory: parallel
  functional assemblies crash the AMD 8060S iGPU with `ErrorDeviceLost` mid-inference.

### Functional tests run OFFLINE via the cache proxy

CI cannot do live, non-deterministic LLM calls reliably. Instead, functional tests point their
client base URL at the HTTP cache proxy (`runner-host.example:12345`), which **replays recorded responses**
keyed by an exact hash of the request:

```
key = SHA256( "{Method}|{PathAndQuery}|{SHA256(request body)}" )
```

- **No fuzzy matching.** One differing byte in the request body → different key → miss.
- On a **miss** the proxy forwards to live LM Studio. The standalone proxy does **not**
  re-persist misses, so a miss surfaces as a *flaky/garbage live response*, not a 502 —
  which makes misses look like model/parse bugs. Always rule out a cache miss first.
- Cassettes live in the **`Agency.HttpCacheProxy` repo** (`src/Agency.Utils.HttpCacheProxy/cache/*.json`),
  **not** in this repo. Each blob: `{ Method, PathAndQuery, BodyHash, StatusCode, Headers, Body(base64) }`.
- A request is cacheable **iff its bytes are identical on every run and every OS**. Anything
  per-run-variable (GUIDs, timestamps, unsorted collections) is uncacheable — those tests must
  degrade gracefully (`Assert.Skip`), e.g. the Group 3 consolidation tests.

## Publishing (ci-main only)

- Unconditional since RT49 — no `if:` gate. Every push to `main` runs notices/pack/publish and
  lands a `0.1.<height>-g<sha>` prerelease on the Gitea feed; a `v*` tag runs the same steps and
  NBGV's `publicReleaseRefSpec` yields a clean `0.1.<height>` stable version instead. This
  supersedes RT16's `if: startsWith(github.ref, 'refs/tags/v')` gate (see [`Releasing.md`](Releasing.md)).
- `ci-main.yaml` triggers on both `push: branches: [main]` and `push: tags: ['v*']`. Tag-trigger
  support verified end-to-end 2026-07-02 with a throwaway tag (`v0.0.1-citest1`): all 30 packages
  landed on the feed at the MinVer-derived version, then were deleted along with the tag.
- Third-party notices: packages embed `localhost:3000` as their repo URL; the step runs
  `socat` to forward local `:3000` → `gitea-host.example:3000` so `thirdlicense` can read license
  data from inside the `.nupkg`.
- Pack + `curl PUT` to the Gitea NuGet feed using `NUGETPUBLISHTOKEN`.

## Investigating a failing run

1. **Pull the log** with the Gitea MCP tool (don't scrape HTML):
   `actions_run_read` `method=download_job_log owner=emre repo=Agency job_id=<id>` → saves a
   local `.log`; then `Grep` it for `Failed|FAIL|Error Message|Test Run Failed`.
2. **Separate real failures from expected ones.** Failure-recovery tests (e.g. `Group5...`,
   the `E1.x`/`E2.3` lines) print "Distillation failed ..." on purpose and still **pass**. Find
   the line that actually says `Failed: 1` / `[FAIL]` for the *named* test.
3. **Reproduce locally against the same proxy** (`appsettings.json` already points at
   `runner-host.example:12345`):
   `dotnet test <proj> -c Release --filter "FullyQualifiedName~<TestClass>" -- RunConfiguration.MaxCpuCount=1`.
   - Passes locally but fails in CI → environment/cache divergence (OS, line endings, env vars),
     **not** a logic bug.
4. **Confirm cache hits.** Cassettes are in the sibling `Agency.HttpCacheProxy` checkout, under
   `src/Agency.Utils.HttpCacheProxy/cache`. Decode a blob's base64 `Body` to see the recorded
   response. The proxy was recorded on **Windows (CRLF)** — see below.

## Known failure modes (check these first)

| Symptom | Cause | Fix / action |
|---|---|---|
| Functional test fails all 3 attempts with a parse/`missing 'records'` error, but passes locally on Windows | **Line-ending → cache-key mismatch** (see reflection) | Ensure `.gitattributes` forces `*.cs eol=crlf`; re-run |
| `ErrorDeviceLost` / GPU crash mid-run | Parallel functional assemblies on the iGPU | `RunConfiguration.MaxCpuCount=1` (already in workflows) |
| LM Studio hard-crashes mid-inference | >2 concurrent slots on AMD 8060S iGPU | Keep concurrency ≤ 2 |
| `Agency.CodeIndexer` `EdgeResolver` `Assert.Single` flake | Known metrics race | Re-run; not a regression |
| `thirdlicense` can't fetch license data | packages embed `localhost:3000` | `socat` forward to `gitea-host.example:3000` (already in ci-main) |
| Cassette "missing" after clearing `cache/` | Proxy in-memory tier masks the miss | Restart the proxy after clearing `cache/` |
| `build-and-publish` fails at a "Read package revision"-style step right after a versioning change | A gate step greps a field that a prior commit removed/renamed; empty grep + `bash -e -o pipefail` → exit 1 | Check what the *previous* merge to `main` changed in versioning/build props before assuming a code regression |
| "🔍 Inspect & test-install published packages" fails: `error: The source specified has already been added` | `src/NuGet.Config` already registers `gitea-local` at the same feed URL the step tries to add as `gitea-smoketest`; `dotnet nuget add source` dedupes by URL, not name | Use `dotnet nuget update source gitea-local --username … --password … --store-password-in-clear-text` instead of adding a new source, and point `dotnet add package --source` at `gitea-local` |
| "🔍 Inspect & test-install published packages" fails: `cp: cannot stat '../PackageSmokeTest': No such file or directory` | The step's `working-directory` is already `src`, so `PackageSmokeTest` lives one level below cwd, not two; the `../` was a leftover from writing the command as if run from repo root | Use `cp -r PackageSmokeTest "$work/harness"` (no `../`) |
| Copied-out `PackageSmokeTest` harness fails every package with `NuGet.Frameworks.FrameworkException: Invalid framework identifier ''` (restore) or `CS0103: The name 'Console' does not exist` (build) | `Agency.PackageSmokeTest.csproj` had no explicit `TargetFramework`/`ImplicitUsings`; it relied on `src/Directory.Build.props` via MSBuild's directory-props auto-import, which stops working once the project is `cp -r`'d to a `mktemp -d` outside `src/` | Set `TargetFramework`/`ImplicitUsings` explicitly in the harness `.csproj` itself — don't rely on ancestor `Directory.Build.props` for a project designed to be copied out of the tree |

## Reflection — 2026-06-11: the CRLF/LF cache-key trap

**Failure:** `EndToEndRecallTests.EndToEnd_FactWrittenInSessionN_RecalledInSessionNPlus1`
failed all 3 retries with `Distillation failed ... JSON response missing 'records' array`.

**Root cause:** distiller prompts are built from C# **verbatim** string literals (`$@"..."`),
so their newlines are baked into the compiled binary. The repo had **no `.gitattributes`** and
`core.autocrlf` differs by OS — `git ls-files --eol` showed `i/lf w/crlf`. Cassettes were
recorded on Windows (**CRLF**); the Linux CI container checked out **LF** → the prompt bytes
differed → different `SHA256` cache key → **permanent cache miss** → proxy forwarded to live
LM Studio → non-deterministic JSON without a `records` array → dead-letter → fail.

**Proof:** converting `EpisodeExtractionPrompt.cs` to LF locally and rebuilding **reproduced**
the exact failure; CRLF passed (hit cassette `8d770df4…`). This is the decisive technique —
flip the line endings and rebuild, don't theorize.

**Fix:** added root `.gitattributes` with `*.cs text eol=crlf` (matches `.editorconfig`
`end_of_line = crlf` and the CRLF-recorded cassettes; no cassette regeneration; `git add
--renormalize .` stages 0 `.cs` files because the index already stores LF).

**Lessons for future agents:**
- "Passes locally, fails in CI, all retries" on a functional test ⇒ suspect a **cache miss**
  from request-byte divergence before suspecting model/logic.
- Any content baked into a cached request body (verbatim-string newlines, GUIDs, timestamps,
  collection order) must be **byte-identical across OSes**. Line endings are the silent one.
- `git ls-files --eol` is the diagnostic; `git status` hides the index↔worktree EOL split.
- The cache proxy forwards misses **live** (no 502), so a cache problem disguises itself as a
  flaky model/parse error.

## Reflection — 2026-07-01: the revision-gate break after the MinVer migration

**Failure:** three consecutive `push`-triggered `build-and-publish` runs (jobs for the RT5,
RT9, and RT2 merges to `main`) failed at a "🔎 Read package revision" step, each after tests
had already passed.

**Root cause:** that step gated pack/notices/publish on `steps.version.outputs.revision == '0'`,
where `revision` was grepped out of a hand-maintained `<Revision>` in `src/Directory.Build.props`.
RT2 (`027888d`) migrated versioning to MinVer and deleted `<Revision>` in the same push that
merged to `main`. From that point, the grep matched nothing, `revision` was empty, and the step
ran under Actions' default `bash -e -o pipefail` — an empty/false comparison exits non-zero, so
the step itself failed (not just evaluated to skip).

**Proof:** each failing job's step list showed Checkout → Restore → Build → Unit Tests →
Functional Tests all green, then "Read package revision" red, then pack/notices/publish
`skipped` as a consequence — i.e. a gate-evaluation failure, not a test or build regression.
Confirmed by reading `git show` on the RT2 commit and seeing `<Revision>` removed from
`Directory.Build.props`.

**Fix:** RT16 (`b30f456`) deleted the "Read package revision" step entirely and switched the
three gated steps to `if: startsWith(github.ref, 'refs/tags/v')`. The next push-triggered run
(job 377) showed those three steps as `skipped` (not failed) — the intended behavior for an
ordinary `main` push under the new tag-based gate.

**Lessons for future agents:**
- A gate/setup step failing immediately after tests pass, right after a versioning or
  build-props change landed, is very likely reading a field the prior change removed — check
  the immediately preceding merge to `main` before assuming a regression in the new commit.
- `skipped` and `failed` look similar in a step list at a glance — read the `conclusion` field
  per step, not just whether the job as a whole is red.
- `bash -e -o pipefail` turns an empty-variable comparison into a hard failure, not a silent
  false — a grep-based gate has no graceful "field doesn't exist" path.

## Reflection — 2026-07-09: the duplicate NuGet source in the smoke-test step

**Failure:** `build-and-publish` job 449 failed at "🔍 Inspect & test-install published packages"
with `error: The source specified has already been added to the list of available package
sources. Provide a unique source.`, right after `dotnet nuget remove source gitea-smoketest
2>/dev/null || true` had already run (and printed `Unable to find any package source(s)
matching name: gitea-smoketest`, i.e. no source by that name existed yet).

**Root cause:** `src/NuGet.Config` (checked in) already defines a source named `gitea-local`
pointing at the exact same feed URL (`http://gitea-host.example:3000/api/packages/emre/nuget/index.json`)
that the step tried to add under the new name `gitea-smoketest`. `dotnet nuget add source`
rejects the add when the **URL** is already registered, regardless of name — so "remove the
name I'm about to add" is not sufficient idempotency; the real collision was a *different*
name mapping to the *same* URL.

**Proof:** `job_id=449` log via `mcp__gitea__actions_run_read` (`get_job_log_preview`) showed
the remove-by-name step being a no-op (name not found) immediately followed by the add-by-URL
failure. Reading `src/NuGet.Config` confirmed `gitea-local` already maps to that URL.

**Fix:** replaced add-a-new-source with `dotnet nuget update source gitea-local --username …
--password … --store-password-in-clear-text` (reuse the existing source, just attach
credentials), and changed the later `dotnet add package --source` argument from
`gitea-smoketest` to `gitea-local`. Applied to both `ci-main.yaml` and the in-progress
`rt39-package-smoke-test` worktree copy of the same step.

**Lessons for future agents:**
- `dotnet nuget add source` dedupes by **source value (URL)**, not by `--name`. Before adding a
  source in a CI step, check whether the repo's own `NuGet.Config` (or another workflow step)
  already registers that URL under a different name — reuse/update it instead of adding.
- A `remove source <name> || true` guard only protects against a source of that *exact* name;
  it gives false confidence that the add that follows is idempotent.

## Reflection — 2026-07-09: two bugs stacked in the same step, and a fix that never reached `main`

**Failure:** run 381 (push to `main`, after PR #139 merged) failed on the same
"🔍 Inspect & test-install published packages" step again, but with a different error:
`cp: cannot stat '../PackageSmokeTest': No such file or directory`. The `secret-scan` job
also failed again on the same `internal-mdns-host` finding at `Agents/CIPipeline.md:178`
that PR #139 was supposed to have already redacted.

**Root cause (path bug):** the step's `working-directory` is `src`, so relative paths in its
`run:` block resolve against `src/`. `PackageSmokeTest` lives at `src/PackageSmokeTest`, i.e.
just `PackageSmokeTest` from that cwd — but the step wrote `cp -r ../PackageSmokeTest`,
looking one directory too high (repo root), which has no such folder. This bug was latent
and masked by the earlier NuGet-source-collision failure (fixed in PR #139); once that
earlier failure was fixed, execution reached this line for the first time and exposed it.

**Root cause (redaction never landed):** PR #139 was merged at `13:29:38`, but the commit that
redacted the real internal hostname (`6cb4c6a`) was pushed to the PR's branch at `13:37:57` —
**after** the PR was already merged and closed. Pushing to a closed PR's branch does not
retroactively update `main`; the commit was stranded. `git merge-base --is-ancestor 6cb4c6a
origin/main` confirmed it was not an ancestor, despite the Gitea API's `pull_request_read`
still showing it as the PR's `head.sha`.

**Proof:** `mcp__gitea__actions_run_read` (`get_run`, `run_id=381`) gave `head_sha=314c003…`,
matching PR #139's `merge_commit_sha` exactly (via `pull_request_read`). `git log --graph
--oneline --all` showed `6cb4c6a` on a branch tip that forked *after* `314c003` (the merge
commit), i.e. off to the side of `main`'s history rather than inside it.

**Fix:** created a new branch directly off current `origin/main` (rather than reusing/rebasing
the now-merged `fix/ci-nuget-smoketest-source` branch) and applied both fixes fresh: re-redacted
the real hostname to the `gitea-host.example` placeholder in this file, and `cp -r
../PackageSmokeTest` → `cp -r PackageSmokeTest` in `ci-main.yaml`.

**Lessons for future agents:**
- Once a PR is merged, further commits pushed to its (now-closed) branch do **not** reach
  `main` — verify with `git merge-base --is-ancestor <sha> origin/main` before assuming a
  post-merge push "landed." If it merged before your fix was ready, open a fresh PR instead.
- A step failing early can mask a second, independent bug further down the same script. After
  fixing the first failure, expect (don't be surprised by) a new failure the *next* time the
  step actually reaches previously-unexecuted lines.
- When a `run:` block sets `working-directory:`, sanity-check every relative path in it against
  that directory, not the repo root — `../` is easy to write out of habit when drafting the
  command as if it ran from the top of the repo.

## Reflection — 2026-07-09: a harness project that only works inside its original directory

**Failure:** with the previous two bugs fixed, the smoke-test step got further but failed on
*every* package with `NuGet.Frameworks.FrameworkException: Invalid framework identifier ''`
during `dotnet add package`, immediately after the harness had been `cp -r`'d into a `mktemp -d`.

**Root cause:** `Agency.PackageSmokeTest.csproj` never set `<TargetFramework>` or
`<ImplicitUsings>` itself — it was always built as a project living under `src/`, so it silently
inherited both from `src/Directory.Build.props` (MSBuild walks up the directory tree looking for
`Directory.Build.props`/`.targets` files to auto-import). Once the step copies the project out to
an unrelated `mktemp -d`, that walk no longer finds `src/Directory.Build.props`, so
`TargetFramework` resolves to empty — hence "invalid framework identifier ''". This also meant
the project (explicitly excluded from `Agency.slnx` and never built as part of the normal
build/test/pack pipeline) had likely never actually been build-verified end-to-end before; two
earlier bugs in the same step (the NuGet source collision, then the `cp` path bug) each masked
this one by failing before execution ever reached it.

**Proof:** reproduced locally — `cp -r src/PackageSmokeTest /tmp/pst-test && cd /tmp/pst-test &&
dotnet build` failed with the same framework error. Adding `<TargetFramework>net10.0</TargetFramework>`
got past that, but then failed to *compile* with `CS0103: The name 'Console' does not exist`
(Program.cs uses top-level statements and calls `Console`/`Exception` without a `using System;`,
relying on the same inherited `ImplicitUsings=enable`). Adding `<ImplicitUsings>enable</ImplicitUsings>`
made the local standalone build succeed with 0 errors.

**Fix:** added both `<TargetFramework>net10.0</TargetFramework>` and
`<ImplicitUsings>enable</ImplicitUsings>` directly to `Agency.PackageSmokeTest.csproj`, so it no
longer depends on ancestor `Directory.Build.props` files it won't have access to once copied out.

**Lessons for future agents:**
- A project deliberately excluded from the solution and only ever invoked by copying it elsewhere
  cannot rely on `Directory.Build.props`/`Directory.Packages.props` inheritance — every property
  those files would normally supply (`TargetFramework`, `ImplicitUsings`, `Nullable`, etc.) must be
  set explicitly in the project file itself.
- Before pushing a CI fix that depends on MSBuild/dotnet behavior, reproduce the exact
  transformation locally (`cp -r <dir> /tmp/x && cd /tmp/x && dotnet build`) rather than pushing
  and waiting on a full CI round-trip — it's faster and surfaces compile errors the runtime log
  wouldn't show until a later step.
- When a step has failed at the same point for multiple unrelated reasons in a row, don't assume
  the *next* failure after a fix is a flake or unrelated noise — it's often the next bug in the
  same never-actually-executed code path.
