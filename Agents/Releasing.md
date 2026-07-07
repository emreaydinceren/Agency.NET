# Releasing — Triggering NuGet Publish from ci-main

How to actually ship packages to the Gitea NuGet feed: why publish is gated the way it is, the
exact chain of components involved, and the runbook for both a real release and a disposable
dry-run test.

## Why gate publish on a tag

Every push to `main` builds and tests. Only a few of those commits should actually become a
published package version — publishing is a deliberate, auditable act, not a side effect of
merging. The gate has to answer one question: *"is this commit a release?"*

The original gate answered it with `steps.version.outputs.revision == '0'`, grepped out of a
hand-maintained `<Revision>` field in `Directory.Build.props`. That's fragile — a stray edit to
a numeric field silently flips publish behavior, with no audit trail, and it broke outright when
RT2 migrated versioning to MinVer and removed `<Revision>` entirely (three real CI failures; see
the reflection in [`CIPipeline.md`](CIPipeline.md)).

RT16 replaced it with `if: startsWith(github.ref, 'refs/tags/v')` — "is this commit a release" is
answered independently of "what version is this": *is there a `v*` tag pointing at it?* That tag
check no longer has to agree with the versioning tool's own logic (RT47 replaced MinVer's
tag-as-version-source with NBGV's git-height scheme — see below), it's purely a workflow gate.

## The components in play

In the order they act when you push a `v*` tag:

| # | Component | Role |
|---|---|---|
| 1 | **The git tag itself** (`v<version>`) | The *trigger* — matched against the workflow's `tags:` filter. Since RT47, it no longer doubles as the version source; NBGV computes the version from `version.json` + git height regardless of tags, and `publicReleaseRefSpec` in `version.json` (`^refs/tags/v\d+\.\d+`) only decides whether that computed version is a clean *public release* build or a `-g<sha>` prerelease. |
| 2 | **`.gitea/workflows/ci-main.yaml` → `on.push`** | `branches: [main]` (build+test every push) plus `tags: ['v*']` (also run on a version tag). Actions resolves *which* workflow triggers run by reading the workflow file **as it exists at the pushed ref** — a tag can only trigger this job if the commit it points to already contains a `ci-main.yaml` with the `tags:` filter. (This is exactly what was missing until 2026-07-02 — see Known gotchas.) |
| 3 | **The three gated steps** in `build-and-publish` (📋 notices, 📦 pack, 🚀 publish) | Each has `if: startsWith(github.ref, 'refs/tags/v')`. For a tag push, `github.ref` is `refs/tags/<tagname>`, so this is true only when the ref is a `v`-prefixed tag — never for an ordinary branch push. |
| 4 | **Nerdbank.GitVersioning** (`GlobalPackageReference` in `Directory.Packages.props`; `version.json` at the repo root) | At build/pack time, computes `major.minor` from `version.json`'s `version` field and the patch from **git height** — commits since that field last changed. Every commit gets a real, monotonic version (`0.1.<height>-g<sha>`); a tag matching `publicReleaseRefSpec` strips the `-g<sha>` suffix for a clean stable version. Needs full git history to count height, which is exactly what RT48 (un-shallowing CI checkouts) provides. |
| 5 | **Checkout step** | `git clone --branch "$BRANCH"` (full clone since RT48) then `checkout $GITHUB_SHA`. |
| 6 | **`dotnet build` / `dotnet pack`** | `IncludeSymbols=true` + `SymbolPackageFormat=snupkg` (in `Directory.Build.props`) means pack also emits a `.snupkg` per project. The vestigial `/p:BuildNumber=${{ github.run_number }}` (a no-op left over from pre-MinVer versioning) was dropped by RT47 — NBGV owns the version now. |
| 7 | **Third-party notices step** | Installs `socat` to forward local `:3000` → `gitea-host.example:3000` (packages embed `localhost:3000` as their repo URL; `thirdlicense` reads that URL from inside the built `.nupkg`), installs the `thirdlicense` global tool, runs it per packable project. Doesn't affect versioning — purely license-notice generation. |
| 8 | **Publish step** | Loops `nupkg/*.nupkg` then `nupkg/*.snupkg`, `curl --fail -X PUT -H "Authorization: token $NUGETPUBLISHTOKEN"` to `http://gitea-host.example:3000/api/packages/emre/nuget` (and `/symbolpackage` for `.snupkg`). `NUGETPUBLISHTOKEN` is a Gitea Actions repo secret scoped to `emre`'s package registry. |
| 9 | **The Gitea NuGet feed** | Where it lands. Browse via `mcp__gitea__package_read` (`type=nuget`, `owner=emre`) or `http://gitea-host.example:3000/emre/-/packages/nuget/<name>/<version>`. |

## How to cut a real release

1. Decide the version. RT3 ("0.x vs 1.0.0") is still undecided as of this writing — treat any
   tag today as pre-1.0 unless that's been explicitly resolved.
2. Confirm `main` has everything you want shipped, and that its `ci-main.yaml` already has the
   `tags: ['v*']` trigger (true since PR #124 / 2026-07-02).
3. From `main`, tag the exact commit you want the version to represent:
   `git tag -a v<version> -m "<summary>"` (annotated, not lightweight — carries a message).
4. `git push origin v<version>`.
5. Watch Gitea Actions for the new tag-triggered `ci-main` run. Confirm `build-and-publish`'s
   notices/pack/publish steps show `success`, not `skipped` — `skipped` means the tag didn't
   match the gate (wrong prefix, or the ref wasn't actually a tag push).
6. Verify the packages landed: check the Gitea package registry for the new version across all
   packable projects (currently ~30).
7. There's no public mirror or changelog automation yet — this only reaches the private Gitea
   feed. Publishing to nuget.org needs RT15 (a separate GitHub Actions workflow); changelog
   generation is RT19. Both still backlog.

## Dry-run testing without a real release

The playbook used to validate RT16 end-to-end, safe to repeat:

1. Pick an obviously-disposable prerelease tag — e.g. `v0.0.1-citestN` — never something that
   could be mistaken for a real version.
2. `git tag -a v0.0.1-citestN -m "throwaway, will be deleted"` then
   `git push origin v0.0.1-citestN`.
3. Confirm the run and packages as in steps 5–6 above.
4. Clean up immediately: delete every package at that test version (`mcp__gitea__package_write`
   `method=delete`, once per package name — there's no bulk delete), then delete the tag both
   remotely (`git push origin --delete <tag>`) and locally (`git tag -d <tag>`).
5. This publishes to the *real* feed — there's no separate staging feed. It's low-risk only
   because the feed is private/internal, not nuget.org.

## Known gotchas

- **No `tags:` trigger before 2026-07-02.** A tag pushed before PR #124 merged would never have
  run this workflow — silently, since nothing errors, the run simply never starts.
- **NBGV needs full history, not just the tagged commit.** Unlike MinVer, NBGV counts git height
  rather than searching for a tag, so a shallow clone always yields height 0 (`0.0.x`). RT48
  removed `--depth=1` from both checkout steps for exactly this reason — don't reintroduce it.
- **Publishing here ≠ a public release.** This is Emre's private Gitea feed only. Public
  nuget.org publishing is a separate, not-yet-built pipeline (RT15).
- **No "unpublish" workflow.** Cleanup after a mistaken publish is a manual per-package delete
  (see Dry-run steps above), not a single revert.

## Related

- [`CIPipeline.md`](CIPipeline.md) — general CI topology, failure modes, and the RT16 revision-gate reflection.
- [`Trackers.md`](Trackers.md) — where RT3, RT15, RT16, RT19 live.
