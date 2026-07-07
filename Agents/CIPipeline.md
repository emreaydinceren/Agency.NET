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
