# Releasing — Triggering NuGet Publish from ci-main

How to actually ship packages: why publish is gated the way it is, the exact chain of components
involved, and the runbook for both a real release and a disposable dry-run test. Covers both feeds
— the private Gitea feed (publishes on every push) and the public nuget.org feed (gated by a tag +
the guarded Gitea→GitHub sync + a human approval) — plus the Gitea/GitHub relationship that makes
the two-feed, two-forge setup work at all. For the full design rationale behind that split, see
`PRIVATE/versioning-release-mirror-plan.md`.

## Why the Gitea feed publishes on every push

Every push to `main` builds and tests. Historically only tagged commits could become a published
package version — publishing was treated as a deliberate, auditable act, not a side effect of
merging.

The original gate answered "is this commit a release?" with `steps.version.outputs.revision == '0'`,
grepped out of a hand-maintained `<Revision>` field in `Directory.Build.props`. That's fragile — a
stray edit to a numeric field silently flips publish behavior, with no audit trail, and it broke
outright when RT2 migrated versioning to MinVer and removed `<Revision>` entirely (three real CI
failures; see the reflection in [`CIPipeline.md`](CIPipeline.md)).

RT16 replaced it with `if: startsWith(github.ref, 'refs/tags/v')` — a pure workflow gate,
independent of the versioning tool's own logic.

**RT49 removed that gate entirely for the Gitea feed.** Once NBGV (RT47) made every commit's
version a deterministic, monotonic `0.1.<height>-g<sha>`, there was no longer a reason to withhold
publishing from ordinary `main` pushes — the private Gitea feed is low-risk, so it now publishes
*everything that passes CI*: a plain push lands a `-g<sha>` prerelease, a `v*` tag lands the
matching clean stable version (via `version.json`'s `publicReleaseRefSpec`). The tag-gated model
described above is retained here for history; **it no longer reflects `ci-main.yaml`'s actual
behavior.** A tag-based gate still applies to the nuget.org publish path (RT15/RT17, see "How to
cut a real release" step 8 below) — that one stays deliberate, since a public release is
effectively unpublishable.

## The components in play

In the order they act when you push a `v*` tag:

| # | Component | Role |
|---|---|---|
| 1 | **The git tag itself** (`v<version>`) | The *trigger* — matched against the workflow's `tags:` filter. Since RT47, it no longer doubles as the version source; NBGV computes the version from `version.json` + git height regardless of tags, and `publicReleaseRefSpec` in `version.json` (`^refs/tags/v\d+\.\d+`) only decides whether that computed version is a clean *public release* build or a `-g<sha>` prerelease. |
| 2 | **`.gitea/workflows/ci-main.yaml` → `on.push`** | `branches: [main]` (build+test every push) plus `tags: ['v*']` (also run on a version tag). Actions resolves *which* workflow triggers run by reading the workflow file **as it exists at the pushed ref** — a tag can only trigger this job if the commit it points to already contains a `ci-main.yaml` with the `tags:` filter. (This is exactly what was missing until 2026-07-02 — see Known gotchas.) |
| 3 | **The three notices/pack/publish steps** in `build-and-publish` (📋 notices, 📦 pack, 🚀 publish) | Unconditional since RT49 (no `if:` gate) — they run on every `main` push and every `v*` tag push alike. NBGV determines whether the resulting package is a `-g<sha>` prerelease or a clean stable version; the workflow no longer distinguishes the two cases itself. |
| 4 | **Nerdbank.GitVersioning** (`GlobalPackageReference` in `Directory.Packages.props`; `version.json` at the repo root) | At build/pack time, computes `major.minor` from `version.json`'s `version` field and the patch from **git height** — commits since that field last changed. Every commit gets a real, monotonic version (`0.1.<height>-g<sha>`); a tag matching `publicReleaseRefSpec` strips the `-g<sha>` suffix for a clean stable version. Needs full git history to count height, which is exactly what RT48 (un-shallowing CI checkouts) provides. |
| 5 | **Checkout step** | `git clone --branch "$BRANCH"` (full clone since RT48) then `checkout $GITHUB_SHA`. |
| 6 | **Tag-assertion step** (RT50, tag pushes only) | Installs the `nbgv` CLI and compares `nbgv get-version -v NuGetPackageVersion` against the pushed tag name (`$GITHUB_REF_NAME` minus its `v` prefix); fails the job on any mismatch. Guards against the one failure mode NBGV *introduces*: since the tag no longer sets the version, a hand-typed tag can silently disagree with what NBGV actually computes. Always cut releases with `nbgv tag`, never `git tag`, and this step never fires. |
| 7 | **`dotnet build` / `dotnet pack`** | `IncludeSymbols=true` + `SymbolPackageFormat=snupkg` (in `Directory.Build.props`) means pack also emits a `.snupkg` per project. The vestigial `/p:BuildNumber=${{ github.run_number }}` (a no-op left over from pre-MinVer versioning) was dropped by RT47 — NBGV owns the version now. |
| 8 | **Third-party notices step** | Installs `socat` to forward local `:3000` → `gitea-host.example:3000` (packages embed `localhost:3000` as their repo URL; `thirdlicense` reads that URL from inside the built `.nupkg`), installs the `thirdlicense` global tool, runs it per packable project. Doesn't affect versioning — purely license-notice generation. |
| 9 | **Publish step** | Loops `nupkg/*.nupkg` then `nupkg/*.snupkg`, `curl --fail -X PUT -H "Authorization: token $NUGETPUBLISHTOKEN"` to `http://gitea-host.example:3000/api/packages/emre/nuget` (and `/symbolpackage` for `.snupkg`). `NUGETPUBLISHTOKEN` is a Gitea Actions repo secret scoped to `emre`'s package registry. |
| 10 | **The Gitea NuGet feed** | Where it lands. Browse via `mcp__gitea__package_read` (`type=nuget`, `owner=emre`) or `http://gitea-host.example:3000/emre/-/packages/nuget/<name>/<version>`. |
| 11 | **Inspect & test-install step (RT39)** | Runs after the publish step, on every push (same "everything that passes CI" scope as publish, not just tags). For each just-published `.nupkg`: reads its `.nuspec` for id/version, checks `unzip -l` for the DLL, `.xml` doc, `README.md`, `LICENSE`, `NOTICE`, and icon (and fails on a stray `.pdb` or loose `.cs`), then copies `src/PackageSmokeTest` (a small committed console harness — never packed, not in `Agency.slnx`) to a scratch dir, `dotnet add package`s that id/version **from the Gitea feed itself** (proving a consumer's restore actually works, not just that the local build succeeded), and runs it: `Agency.PackageSmokeTest` loads the package's main assembly via reflection and forces type resolution, catching a missing transitive dependency or wrong target framework that content inspection alone would miss. Collects failures across all ~30 packages before failing the job, so one bad package doesn't hide problems in the rest. |

## How to cut a real release

1. Decide the version. RT3 ("0.x vs 1.0.0") is still undecided as of this writing — treat any
   tag today as pre-1.0 unless that's been explicitly resolved.
2. Confirm `main` has everything you want shipped, and that its `ci-main.yaml` already has the
   `tags: ['v*']` trigger (true since PR #124 / 2026-07-02).
3. From `main`, at the commit you want to ship, run `nbgv tag`. **Never hand-type a release tag**
   (`git tag v<version>`) — since RT47, the tag no longer *sets* the version (git height does), so
   a hand-typed tag can silently disagree with what NBGV actually computes: tag `v0.1.99` on a
   commit NBGV resolves to `0.1.7` would ship a `0.1.7` package under a `99` tag. `nbgv tag` reads
   NBGV's own computed version for that commit and creates the matching `v<x.y.z>` tag, so tag and
   package can never drift. (Preview the tag name first with `nbgv tag --what-if`; install the CLI
   once with `dotnet tool install --global nbgv` if you don't have it.)
4. `git push origin v<x.y.z>` (the exact tag `nbgv tag` created — check its output or
   `git describe --tags`).
5. Watch Gitea Actions for the new tag-triggered `ci-main` run. The first `build-and-publish` step
   asserts the pushed tag matches NBGV's computed version and fails the job on mismatch — see
   Known gotchas below if it does. Confirm the notices/pack/publish steps show `success` (they run
   unconditionally now, so `skipped` would mean the job never reached them — e.g. an earlier step
   failed).
6. Confirm the **inspect & test-install step (RT39)** is green too — it's the last thing standing
   between this Gitea-feed publish and pushing the tag out to GitHub for the public nuget.org
   release (RT51/RT15, step 8 below). A red run here means a packaging defect (missing
   README/XML/icon, a stray `.pdb`, or a package that can't actually be restored from the feed)
   that content inspection alone wouldn't have caught.
7. Verify the packages landed: check the Gitea package registry for the new version across all
   packable projects (currently ~30).
8. The steps above only reach the private Gitea feed. To also reach the public nuget.org feed,
   run the guarded outbound sync (`sync-github.yaml`, `workflow_dispatch` on Gitea Actions — see
   "Contribution lifecycle" below) so `main` **and the new tag** reach GitHub. GitHub's
   `.github/workflows/release.yaml` (RT15/RT17) then triggers automatically on the `v*` tag: it
   re-asserts the tag matches NBGV's computed version, restores/builds/tests/packs from scratch
   on GitHub's own runner, then the `publish` job pauses at the `nuget-release` environment for a
   required human approval before exchanging a short-lived GitHub OIDC token for a nuget.org API
   key and pushing — no long-lived nuget.org secret is stored anywhere. See that workflow's header
   comment for the one-time nuget.org/GitHub setup this requires.
9. Changelog generation (RT19) is still backlog — there's no automated release-notes step yet.

## Dry-run testing without a real release

Since RT50, an obviously-fake hand-typed tag (the old way to dry-run this) no longer reaches
publish at all — it now fails fast at the tag-assertion step, because `nbgv tag` is the only way
to produce a tag that matches NBGV's computed version. That's the intended behavior, not a bug in
the dry-run:

1. Hand-type a tag that can't match the current computed version, e.g. `git tag v0.0.1-citestN`
   (lightweight is fine — this is deliberately wrong, not a release).
2. `git push origin v0.0.1-citestN`.
3. Confirm the tag-triggered `ci-main` run **fails** at "Assert tag matches computed version" with
   a mismatch error, and never reaches restore/build/pack/publish.
4. Clean up: delete the tag remotely (`git push origin --delete v0.0.1-citestN`) and locally
   (`git tag -d v0.0.1-citestN`). No packages were published, so there's nothing to delete there.

There's no longer a way to dry-run the *stable publish* path disposably — `nbgv tag` always names
the tag after the real computed version, so a tag it creates is indistinguishable from cutting an
actual release. That path was already exercised once during the RT47/RT48 rollout; it doesn't need
a standing recipe.

## Contribution lifecycle — validating and merging a public PR

Contributions arrive as ordinary GitHub pull requests, but they can only be *validated* on
Gitea — the functional test suite needs the home-lab LLMs, which aren't reachable from GitHub's
public CI (see `CONTRIBUTING.md`'s "About functional tests" section, which sets this expectation
for contributors up front). This is the maintainer-side counterpart to that doc: how a PR gets
from "opened on GitHub" to "merged and mirrored back," entirely without ever touching GitHub's
own Merge button.

**The inviolable rule: never click Merge on GitHub.** Gitea `main` is the sole source of truth;
GitHub `main` is a mirror written only by the guarded outbound push (RT51, below). Merging on
GitHub directly would let GitHub `main` diverge from Gitea `main` with no way to reconcile them
short of another force-push — so it's not a style preference, it's what keeps the two histories
from splitting.

**One-time setup** (once per clone that does this work):

```bash
git remote add github https://github.com/emreaydinceren/Agency.NET.git
```

### Act 1 — fetch and validate

1. Fetch the PR at its exact, read-only ref and check it out on a throwaway branch:

   ```bash
   git fetch github refs/pull/<n>/head
   git switch -c pr-<n> FETCH_HEAD
   ```

2. Run the full test suite locally, including the `Category=Functional` tests GitHub's CI
   couldn't run (see `Agents/BuildAndTest.md`).

### Act 2 — the needs-changes loop

If validation fails, comment on the GitHub PR describing what needs to change. The PR **stays
open**; there's no local branch to clean up or re-derive. Once the contributor pushes new commits
to their fork, `refs/pull/<n>/head` updates automatically — re-run step 1 (the `git fetch` +
`git switch -c` pair) to pick up the new commits and re-test. Repeat until it passes or the PR is
abandoned.

### Act 3 — merge and publish

3. On pass, merge into Gitea `main` with `--no-ff`:

   ```bash
   git switch main
   git merge --no-ff pr-<n>
   ```

4. Run the guarded outbound push (RT51's `sync-github.yaml`, `workflow_dispatch`-triggered on
   Gitea Actions) to mirror the merge to GitHub. This closes the loop — see "Known gotchas" below
   for a caveat on whether it actually auto-closes the PR as merged; the sync mechanics themselves
   are exercised and working, but that specific auto-close behavior hasn't been tested against a
   real external contributor PR yet.

**Why `--no-ff`, not squash or rebase:** a fast-forward, squash, or rebase merge rewrites the
contributor's commits onto new parents, which changes their SHAs. `--no-ff` adds a merge commit on
top but leaves the contributor's original commits — and their SHAs — untouched. The intent is that
GitHub's own merge-detection (a PR auto-closes as "merged" when its exact head commit SHA becomes
reachable from the base branch, not by matching diff content) recognizes the contributor's commits
once they reach GitHub `main`, crediting them as the PR author rather than leaving the PR to be
closed by hand with no merge record. See the caveat below on RT51 — this only holds if the
outbound push actually preserves those SHAs.

**Escape hatch:** for a messy drive-by fix where preserving individual commits isn't worth it,
squash-merge locally instead and close the PR on GitHub manually with a comment linking the squash
commit. This forgoes the auto-close/authorship-credit behavior above — use it deliberately, not by
default.

## Known gotchas

- **RT51's SHA-preservation claim is verified only for the sync mechanics, not for a real
  contributor PR.** `sync-github.yaml` has since been triggered against the live feed multiple
  times (e.g. runs #416/#417/#420 on Gitea Actions) and successfully force-pushes a rewritten
  history to GitHub — that part works. What's still unverified is the auto-close behavior the
  contribution lifecycle above depends on: a contributor's PR-head SHA, once merged `--no-ff` into
  Gitea `main`, would need to survive the guarded outbound push and become reachable on GitHub
  `main` for GitHub to auto-close the PR as "merged." But `sync-github.yaml` does a **full
  `git-filter-repo` rewrite of the entire history on every run** (path-strip + text redaction +
  mailmap), then force-pushes the result — by its own header comment, "this is always a
  full-history force-push, never an incremental one." Rewriting any commit changes the hash of
  every descendant commit, even ones whose own content never changed, because a commit hash covers
  its parent hash. That means a contributor's original SHA is unlikely to reach GitHub `main`
  intact, which would mean the PR does *not* auto-close as merged despite matching content. No real
  external contribution has gone through Act 1–3 yet to confirm either way — the sync runs so far
  have all mirrored Emre's own solo-authored merges. If a real PR sync doesn't auto-close the
  source PR, this is the first thing to check, and RT51's design (full rewrite vs. an incremental
  sync that only rewrites new commits) would need revisiting.
- **GitHub Actions can't restore from `src/NuGet.Config` — it needs its own config.**
  `src/NuGet.Config` lists two home-lab-only feeds (`baget-local`, `gitea-local`) unreachable from
  a GitHub-hosted runner. `--ignore-failed-sources` alone doesn't save it: `TreatWarningsAsErrors`
  (`Directory.Build.props`) promotes the resulting NU1801 ("unable to load the service index")
  warning back into a hard error, so restore fails outright — first hit on
  [GitHub Actions run 29114671436](https://github.com/emreaydinceren/Agency.NET/actions/runs/29114671436/job/86435020483),
  2026-07-10, right after the sync job's text-redaction had rewritten `gitea-host.example` to the
  placeholder `gitea-host.example` in that file's history. Fixed by giving `.github/workflows/ci.yaml`
  and `release.yaml` their own `nuget.org`-only `src/NuGet.GitHub.config`
  (`dotnet restore --configfile NuGet.GitHub.config`), so there's no unreachable source to warn
  about in the first place. `src/NuGet.Config` (Gitea's config, with all three sources) is
  untouched — don't try to make one `NuGet.Config` serve both forges; give GitHub CI steps that
  restore packages their own config file instead.
- **No `tags:` trigger before 2026-07-02.** A tag pushed before PR #124 merged would never have
  run this workflow — silently, since nothing errors, the run simply never starts.
- **NBGV needs full history, not just the tagged commit.** Unlike MinVer, NBGV counts git height
  rather than searching for a tag, so a shallow clone always yields height 0 (`0.0.x`). RT48
  removed `--depth=1` from both checkout steps for exactly this reason — don't reintroduce it.
- **Tag ≠ version drift (RT50).** The tag no longer sets the version, so a hand-typed release tag
  can silently disagree with what NBGV actually computes. Always use `nbgv tag`; the tag-assertion
  step in `build-and-publish` fails the build if a pushed `v*` tag doesn't match
  `nbgv get-version -v NuGetPackageVersion` anyway, so a mismatch is caught, not shipped.
- **Publishing here ≠ a public release, automatically.** The steps in this doc (through "How to
  cut a real release" step 7) only reach Emre's private Gitea feed. Public nuget.org publishing
  (RT15/RT17) is a separate, deliberate step — see step 8 — gated by the guarded outbound sync
  plus a required human approval, not something that happens as a side effect of a Gitea tag push.
- **No "unpublish" workflow.** Cleanup after a mistaken publish is a manual per-package delete
  (see Dry-run steps above), not a single revert.
- **RT39's test-install step assumes `NUGETPUBLISHTOKEN` also works for read.** It registers the
  Gitea feed as a NuGet source with `--username emre --password $NUGETPUBLISHTOKEN` to restore the
  just-published package back out. This is the standard Gitea package-registry auth pattern, but it
  hasn't been exercised against the live feed yet — if the first real run 401s on the restore, the
  token's scope (or the username) is the first thing to check.

## Related

- [`CONTRIBUTING.md`](../CONTRIBUTING.md) — the contributor-facing counterpart to the contribution
  lifecycle above: how to build/test locally, coding style, and the PR process from the
  contributor's side.
- [`CIPipeline.md`](CIPipeline.md) — general CI topology, failure modes, and the RT16 revision-gate reflection.
- [`Trackers.md`](Trackers.md) — where RT3, RT15, RT16, RT19 live.
