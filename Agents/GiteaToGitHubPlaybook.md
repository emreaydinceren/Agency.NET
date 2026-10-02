# Gitea → GitHub: publishing playbook (for porting to a sibling repo)

Derived from Agency.NET's `.gitea/workflows/`, `.github/workflows/` and `Agents/{CIPipeline,Releasing}.md`.

## 1. Model

- **Gitea `main` is the source of truth.** GitHub `main` is a scrubbed, write-only mirror.
- Nothing is ever merged on GitHub (never click Merge). GitHub PRs are fetched (`git fetch github refs/pull/<n>/head`), validated on Gitea, merged `--no-ff` into Gitea `main`, then mirrored out.
- Gitea's history is never rewritten. Only a **disposable clone** is rewritten and force-pushed.

```
Gitea main ──(manual workflow_dispatch: sync-github.yaml)──► disposable clone
   clone → filter-repo scrub → verify (fail closed) → gitleaks → force-push ──► GitHub main
                                                                                  │
                          GitHub CI (ci.yaml) ──success──► release.yaml (workflow_run)
                                                           └─ human approval (environment) → nuget.org (OIDC)
```

## 2. The push: `.gitea/workflows/sync-github.yaml`

Trigger: `workflow_dispatch` only. The click *is* the intent to publish.

Why not Gitea's built-in push mirror: it is all-refs, no pre-push guard, no filtering. Internal hostnames/IPs/personal email in history would leak.

Steps, in order (any failure = nothing is pushed):

1. **Full clone** of Gitea `main` into `scrub-clone/` (token-injected URL, `safe.directory '*'`). No shallow clone: filter-repo needs full history.
2. **Install** `git-filter-repo` (raw script from GitHub + `python3`) and `gitleaks` (latest release tarball).
3. **Write rule files from secrets**: `SYNC_MAILMAP` → `mailmap.txt`, `SYNC_REPLACEMENTS` → `replacements.txt`. They are secrets, not tracked files, because they contain the real hostnames/IPs/emails. Fail if unset.
4. **Rewrite history**: `git-filter-repo --force --path <private-file> --invert-paths --replace-text replacements.txt --replace-message replacements.txt --mailmap mailmap.txt`. (`--replace-message` is needed because Gitea's `Reviewed-on: <gitea-url>` merge trailers live in commit messages.)
5. **Swap in the public NuGet config**: copy `src/NuGet.GitHub.config` over `src/NuGet.Config` and commit, so a plain clone-and-restore works for outsiders.
6. **Verify, fail closed**: no private path in history; only the allowed noreply author/committer email survives; every needle in `replacements.txt` is absent from blobs (`git log -S`) and messages (`git log --grep`). The needles are read from the runtime copy of the secret, so the tracked workflow never contains them.
7. **Gitleaks backstop** on the rewritten tree.
8. **Force-push**: `git remote add github https://x-access-token:$SYNC_GITHUB_TOKEN@github.com/<owner>/<repo>.git`; `git push --force github main`; `git push --force github --tags`.

Consequence: filter-repo changes every descendant SHA, so every sync is a full-history force-push. Contributor SHAs probably do not survive, so GitHub PRs may not auto-close as "merged" (unverified in this repo; close by hand with a link if so).

### One-time setup for the sibling repo

| Where | What |
|---|---|
| GitHub | Empty target repo; fine-grained PAT, that repo only, `Contents: Read and write` |
| Gitea repo secrets | `SYNC_GITHUB_TOKEN`, `SYNC_MAILMAP`, `SYNC_REPLACEMENTS` |
| Gitea runner | Label used by `runs-on` (here `dotnet-10`) with container support |
| Repo files | `.gitleaks.toml`; a public-safe `NuGet.GitHub.config` (nuget.org only) if you restore NuGet |
| Local clone | `git remote add github <url>` (for fetching PR refs) |

Edit in the sibling: the private path(s) to strip, repo name/owner in the push URL, git identity in the NuGet-config commit, and the allowed-email regex in step 6.

## 3. How GitHub CI differs from Gitea CI

Same YAML dialect (Gitea Actions is GitHub-Actions-compatible), different environment.

| Aspect | Gitea (`ci-pr.yaml`, `ci-main.yaml`) | GitHub (`ci.yaml`, `release.yaml`, `docs.yaml`) |
|---|---|---|
| Runner | self-hosted, `runs-on: dotnet-10`, container `dotnet/sdk:10.0` | `ubuntu-latest`, same container image |
| Checkout | **Manual `git clone`** with `${{ github.token }}` in the URL, then `checkout $GITHUB_SHA`. `actions/checkout` is not used: the SDK container has no Node.js, which JS actions need | `actions/checkout` (SHA-pinned), `fetch-depth: 0` (NBGV needs full height) |
| Shell | `defaults.run.shell: bash` set explicitly | default |
| NuGet restore | `dotnet restore` using `src/NuGet.Config` (nuget.org + two home-lab feeds) | `dotnet restore --configfile NuGet.GitHub.config` (nuget.org only). Unreachable feeds → NU1801, promoted to error by `TreatWarningsAsErrors`; `--ignore-failed-sources` does not help |
| Postgres service | `pgvector/pgvector:pg18-trixie`, host `postgres` | identical (job is containerised, so use the service name, not `localhost`) |
| Tests | Unit **and full Functional** (`Category=Functional&Category!=Cloud`), serialised (`-maxcpucount:1` + `RunConfiguration.MaxCpuCount=1`), 3-attempt retry loop, LLM calls via self-hosted record/replay cache proxy to LM Studio | Unit + Postgres-only functional (`Category!=RequiresLlm`); no route to the proxy or LM Studio; no retry loop |
| Extra gates | gitleaks working-tree scan (separate job), vulnerable-package check, NBGV tag-matches-version check on `v*` tags | Console boot smoke test; Codecov upload (needs Node → GitHub only); `gnupg` install for codecov |
| Secrets | LLM API keys, `NUGETPUBLISHTOKEN` | `CODECOV_TOKEN`, `NUGET_USER` (username only) |
| Triggers | PR to main, push to main, `v*` tags (ignore `docs/**`); `sync-github` is manual | PR + push to main (ignore `docs/**`); `release.yaml` via `workflow_run` on CI success; `docs.yaml` on path filters |
| Publishing | `ci-main`: pack + `curl PUT` to the Gitea NuGet registry with a token, then install-and-load smoke test of the published packages | `release.yaml`: build/test/pack/SBOM (CycloneDX) → artifacts → `publish` job gated by `environment: nuget-release` (required reviewer) → **OIDC Trusted Publishing** via `NuGet/login` (no stored NuGet key) → tag + GitHub Release with SBOM |
| Permissions | n/a | workflow `contents: read`; `id-token: write` and `contents: write` only on `publish` |
| Docs | none | DocFX → GitHub Pages (`pages: write`) |

Design intent: Gitea is the strict, private gate (everything runs, including LLM tests). GitHub CI is a public, reproducible subset that proves a clean clone builds with only public inputs, and holds the only nuget.org credential path.

## 4. Gotchas to carry over

- **Don't let one NuGet.Config serve both forges.** Keep a separate `NuGet.GitHub.config` and pass `--configfile`. CycloneDX ignores `--configfile`; the release workflow overwrites `NuGet.Config` with the GitHub one before running it.
- **No Node in the .NET SDK container → no JS actions on Gitea.** Replace `actions/checkout` with the manual clone. Keep JS actions on GitHub only.
- **Pin GitHub actions by commit SHA.**
- **Pin the release checkout** to `github.event.workflow_run.head_sha`; otherwise a racing push gets released.
- **NBGV needs `fetch-depth: 0`.** Shallow clones silently yield `0.0.x`.
- **Release tag is created after publish**, on GitHub, so a tag/Release only exists for commits that reached nuget.org. Side effect: `workflow_run` triggers produce `-g<sha>` prereleases, never clean `x.y.z`.
- **Replacement redaction rewrites tracked files too** (e.g. the Gitea hostname in `NuGet.Config` becomes a placeholder), so anything on GitHub must not depend on private hosts.
- **Gitea's `dotnet nuget update source` drops `allowInsecureConnections`.** Re-assert it (only matters for plain-HTTP internal feeds).
- Keep the scrub rules and token in Gitea secrets only; never commit needles to the repo.

## 5. Port checklist

1. Create GitHub repo, PAT, and the three Gitea secrets.
2. Decide what to strip/redact; write mailmap + replacements; run filter-repo once locally on a throwaway clone and inspect the result.
3. Copy `sync-github.yaml`; adjust owner/repo, stripped paths, allowed email, nuget step (or delete it).
4. Add `.gitleaks.toml` and, if needed, `NuGet.GitHub.config`.
5. Copy `.github/workflows/ci.yaml`; trim tests to what runs without private infra; mark private-infra tests with a trait (here `RequiresLlm`) and filter them out.
6. If publishing: copy `release.yaml`; configure the nuget.org Trusted Publishing policy (repo, workflow file, environment), the `nuget-release` environment with a required reviewer, and `NUGET_USER`.
7. Dry run: trigger sync, confirm verification passes, confirm GitHub CI goes green, approve (or skip) the release.
