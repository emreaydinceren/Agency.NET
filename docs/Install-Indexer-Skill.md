# Install the documentation search skill (`agency-index`)

Following this page installs `agency-index`, which gives a coding agent semantic search over a folder of
documentation, and ends with a working skill and a passing smoke-test search. It needs a .NET SDK and an
OpenAI-compatible embeddings endpoint (LM Studio, Ollama, OpenAI, ...). Installing takes a few minutes; the
first index of a large docs tree takes longer (see [First index](#first-index)).

To have an agent do it, tell it: `Install the Agency indexer skill from docs/Install-Indexer-Skill.md`.
To remove it again, see [Where things live](#where-things-live) and [Remove it](#remove-it).

Curious how it works under the hood: how files are selected, chunked and embedded, the commands, and how the
single writer lock works? See [Agency.Indexer](Projects/Agency.Indexer.md).

## If you are an agent reading this

You were handed this page, so the job is to **install the skill**. Do the steps below. Do not summarise the
page and do not ask the user what they want done with it. (If the request says remove or uninstall, go to
[Remove it](#remove-it); if it says upgrade or refresh, go to [Upgrading](#upgrading).)

Use what the request already says and ask only for what is missing, **one question at a time, as numbered
options with the default marked**. Read-only discovery (`agency-index doctor`, `agency-index indexes`) needs no
question.

### Questions to ask (skip any the request already answers)

1. **How to set it up.** When nothing is stated, this is your first message, word for word:

   > How do you want to set it up?
   > 1. For this repo (the skill and the index settings live in this repo)
   > 2. Globally (the skill is available in all your repos)

   1 means `--scope repo`: the skill goes in `<repo>/.claude/skills` and the repo gets a `.agency-index.json`.
   2 means `--scope user`: the skill goes in `~/.claude/skills` and `~/Agents/skills`, with no repo file. Either
   way the tool is one global .NET tool and the embeddings settings are user-level.
2. **Where the embeddings come from:** 1. LM Studio (local), 2. Ollama (local), 3. OpenAI, 4. OpenRouter,
   5. another OpenAI-compatible URL. These map to `--endpoint lmstudio|ollama|openai|openrouter` or
   `--embedding-url <url>`.
3. **Which model.** If the request names one, use it. Otherwise run the `setup` preview without
   `--embedding-model`: it picks the only embedding model the server lists, and when there are several it fails
   with the list, which you show the user to choose from. Never guess a model.
4. **How to authenticate.** A local server needs no key: say so and move on. OpenAI and OpenRouter need a key in
   the environment variable named in [API key](#api-key-openai-openrouter) (`OPENAI_API_KEY`,
   `OPENROUTER_API_KEY`): tell the user which to set, check with `agency-index doctor` (its `api_key` check never
   shows the value), and **never ask for the key, print it, or put it in a command line or file**.
5. **Storage: do not ask.** Use SQLite unless the request mentions PostgreSQL or a database server. For PostgreSQL,
   pass `--provider postgres` to `setup`, tell the user to set the `AGENCY_INDEX_Database` environment variable
   (see [PostgreSQL](#postgresql-optional)), and **never ask for, print or store the connection string**: it contains
   the password.
6. **What to index now.** Scope 1: the default is `docs/` if it exists. Scope 2: ask whether to index this
   repo's docs now or skip.

### What to do

1. Run `agency-index doctor`. If the command is missing, install the tool
   (`dotnet tool install -g AgencyDotNet.Indexer --prerelease`, see [PATH](#1-install-the-tool)); asking the
   user to set it up counts as approval for that install.
2. Run `agency-index indexes` to see what already exists.
3. Ask the questions above. Then **preview** the setup, which writes nothing:

   ```bash
   agency-index setup --scope repo --endpoint lmstudio --embedding-model <id> --index <name> --root <dir> --no-index
   ```

   (Scope 2: `--scope user` and no `--index`/`--root`.) Show the user the `config_before` / `config_after` it
   reports, read `status` and `warnings`, and handle them:
   - `name_taken`: the index name belongs to another repo's folder. Ask for another name.
   - `index_model_mismatch`, or `warnings` listing indexes: those indexes were built with a different embedding
     model and cannot be refreshed or searched with the new one. Name them and ask: **keep the old model**
     (pass `--embedding-model <old>`) or **rebuild** them (`agency-index index --index <name> --rebuild`, which
     re-embeds every file under the new model in one step). Never rebuild or drop without a yes.
4. Apply: run the same command with `--yes`. It installs the skill, **merges** the settings into
   `~/.agency/indexer.json` and, for scope 1, writes the repo's `.agency-index.json`. Never write or overwrite
   `indexer.json` by hand; the manual steps further down only show what `setup` writes.
5. If the user wants an index: run `agency-index index --index <name> --root <dir> --dry-run` (scope 1:
   `agency-index index --dry-run`) and tell the user the file count and time estimate. Ask before a run that
   will take more than a minute, then run the real `index`; its progress goes to stderr.
6. Smoke test: run `agency-index search --query "<a question these docs answer>"` (add `--index <name>` for
   scope 2) and report the top hit's file and score. If the result is empty or poor, say so plainly.
7. Offer to calibrate the search threshold ([step 5](#5-calibrate-the-search-threshold-recommended)); at least run
   `agency-index calibrate --index <name>` and tell the user the `noise_ceiling`. Save a threshold only with their yes.
8. Run `agency-index doctor` again; every check should be `ok`. Report anything that is not.
9. Tell the user how to remove it again ([Remove it](#remove-it)).

### Approvals and limits

- A request that asks you to set it up (for example "set it up globally with model X") approves the global tool
  install, the `setup` apply and the skill install in the scope chosen.
- Ask first, every time, before: dropping or rebuilding an existing index, any overwrite that is not a merge,
  an index run estimated over a minute, writing outside the chosen scope, and `--scope all` anything.
- Do not claim it works until the smoke-test search has run. If you could not run a step, say which.

## Quick path

```bash
dotnet tool install -g AgencyDotNet.Indexer --prerelease
agency-index doctor                                   # what is missing, with a fix for each item
agency-index setup --endpoint lmstudio --index myrepo --root ./docs         # preview, writes nothing
agency-index setup --endpoint lmstudio --index myrepo --root ./docs --yes   # apply
```

`doctor` prints one JSON object: `{"status":"ok"|"problems","checks":[{"name","ok","detail","fix"}]}`
and exits 0 either way; branch on `status` and each check's `fix`. `setup` installs the skill (default
scope: this repo), picks the embedding model (the configured one if the server lists it, else the only
model with "embed" in its id; otherwise it asks you to pass `--embedding-model`), measures the model's
dimensions from the endpoint, merges `~/.agency/indexer.json` without touching other keys, and, when
`--index` or `--root` is given, runs the first index and one smoke-test search (`--query` to change it;
`--no-index` writes the config and repo file but skips indexing, so you can `--dry-run` first).
With `--scope repo` and an index it also writes the repo's `.agency-index.json` (see
[Per-repo config](#per-repo-config)). It stops with exit 2 and `status: name_taken` if the index name belongs to
another root. Options:
`--scope repo|user`, `--endpoint lmstudio|ollama|openai|openrouter` or `--embedding-url <url>`,
`--embedding-model`. Hosted endpoints need their [API key](#api-key-openai-openrouter) in the
environment first. The manual steps below show what it does.

## Choices

| Choice | Options | Default |
| --- | --- | --- |
| Skill scope | this repo (`<repo>/.claude/skills`) / all my repos (`~/.claude/skills`) | this repo for `setup`; the bare `install-skill` command defaults to all my repos |
| Embeddings | LM Studio / Ollama / OpenAI / other OpenAI-compatible | LM Studio |
| Storage | SQLite / PostgreSQL + pgvector (see [PostgreSQL](#postgresql-optional)) | SQLite |
| What to index first | a folder, or skip | `docs/` if it exists |

## Manual steps

The agent steps above use `setup`, which does all of this and merges instead of overwriting. These are the same
steps by hand.

### 1. Install the tool

```bash
dotnet tool install -g AgencyDotNet.Indexer --prerelease
```

`--prerelease` is needed until a clean (non `-g<sha>`) version is published to nuget.org.

An already-open shell may not find `agency-index` straight after install, because the tools folder is
new on `PATH`. Open a new shell, or add it for the current one:

| Shell | Command |
| --- | --- |
| PowerShell | `$env:PATH += ";$env:USERPROFILE\.dotnet\tools"` |
| Git Bash / bash / zsh | `export PATH="$PATH:$HOME/.dotnet/tools"` |

### 2. Install the skill

```bash
# this repo only (what setup does unless you say otherwise)
agency-index install-skill --scope repo

# all my repos (what install-skill alone does)
agency-index install-skill --scope user
```

The two commands have different defaults on purpose: `setup` is the guided path and picks this repo, so it
changes the least; `install-skill` on its own installs for the user. Pass `--scope` to either to be explicit.
`--scope repo` writes `./.claude/skills/agency-index/`; `--scope user` writes **two** copies,
`~/.claude/skills/agency-index/` (Claude Code) and `~/Agents/skills/agency-index/` (other agent tools that read
that folder); `--dir <skills-root>` chooses any other location. The output lists each file and whether it
replaced an existing one; `uninstall` removes every copy.

### 3. Point it at an embeddings endpoint

There is no default endpoint: `index` and `search` exit 2 until one is configured. `setup` writes this for
you. By hand, **merge** these keys into `~/.agency/indexer.json` (on Windows
`C:\Users\<you>\.agency\indexer.json`; Git Bash still resolves `~/.agency`) and never replace a file that
already exists: other keys, and the model your existing indexes were built with, depend on it:

```json
{
  "Embedding": {
    "BaseUrl": "http://localhost:1234/v1",
    "ModelId": "text-embedding-qwen3-embedding-0.6b",
    "Dimensions": 1024
  }
}
```

`Dimensions` must match the model; it defaults to 1024 when omitted. The index is created with that
column width, so a wrong value fails or corrupts the index.

| Model | Dimensions |
| --- | --- |
| `text-embedding-nomic-embed-text-v1.5` | 768 |
| `text-embedding-qwen3-embedding-0.6b` | 1024 |
| `text-embedding-qwen3-embedding-4b` | 2560 |
| `text-embedding-qwen3-embedding-8b` | 4096 |
| `mxbai-embed-large` | 1024 |
| `text-embedding-3-small` (OpenAI) | 1536 |
| `text-embedding-3-large` (OpenAI) | 3072 |

`setup` does not read this table: it asks the endpoint for one vector and uses that length, so a model that is
not listed here still works. The table is for writing `indexer.json` by hand.

**Optional: a search threshold** (the full procedure is [step 5](#5-calibrate-the-search-threshold-recommended)). Scores depend on the model; with qwen3-embedding-0.6b an unrelated query
still tops out near 0.5, so a fixed "below 0.45 means not found" rule lets irrelevant text through. Set
`"Search": { "MinScore": 0.55 }` in `indexer.json` (or `AGENCY_INDEX_Search__MinScore`, or `--min-score`
per search) to drop weaker hits before they reach the agent's context. To pick the number, search for
something unrelated and set it slightly above the top score, or let `agency-index calibrate --index <name>`
do that: it runs twelve unrelated queries, reports the `noise_ceiling` (the best score any of them reached) and a
`suggested_min_score` just above it, and `--save` stores that value with the index so `search` uses it when no
threshold is configured (a configured `Search:MinScore` or `--min-score` still wins). On-topic scores are not
measured, so look at a few real queries before trusting the suggestion. It stays a user-level setting: a repo's
`.agency-index.json` cannot set it. When a threshold removes every hit, `search` still succeeds with
`"hits":[]` plus `filtered` and `best_score`, which tells the agent to fall back to grep.

For another model, ask the endpoint: the length of one returned vector is the value.

```bash
curl -s http://localhost:1234/v1/embeddings -H "Content-Type: application/json" \
  -d '{"model":"<model id>","input":"probe"}'
```

#### API key (OpenAI, OpenRouter)

Local servers (LM Studio, Ollama) need no key. Hosted providers do, and the key is a secret: the tool
reads it from the environment and **never writes it to `indexer.json`** (or anywhere else).

| Endpoint | Variable |
| --- | --- |
| `https://api.openai.com/v1` (`--endpoint openai`) | `OPENAI_API_KEY` |
| `https://openrouter.ai/api/v1` (`--endpoint openrouter`) | `OPENROUTER_API_KEY` |
| any other endpoint | `AGENCY_INDEX_Embedding__ApiKey` |

`setup` and `doctor` fail with the variable's name when a hosted endpoint has no key, and `doctor` flags an
`Embedding:ApiKey` left in `indexer.json` in plain text (delete it from the file). `--embedding-key` exists
for a one-off run but is visible in shell history and the process list, and `setup` does not save it;
prefer the variable. Azure OpenAI is not supported.

##### Managing the key

Hosted providers receive the text of every chunk you index, so use them only for documentation you are
happy to send. Create a key used only for this tool, with a spend limit if the provider offers one, so a
leak is cheap and revocable.

Where to keep it, from simplest to safest:

| Where | How |
| --- | --- |
| Windows user environment | `[Environment]::SetEnvironmentVariable("OPENAI_API_KEY", (Read-Host -MaskInput), "User")` in PowerShell 7 prompts without echoing or recording the value (`setx` would put it in your history). Open a new shell afterwards. |
| Shell profile (macOS, Linux) | `export OPENAI_API_KEY=...` in `~/.zshrc` or `~/.bashrc` works but leaves the key in a plain-text file. Prefer the next row. |
| Secret manager, read at shell start | macOS Keychain: `export OPENAI_API_KEY="$(security find-generic-password -s openai-api-key -w)"`. 1Password CLI: `export OPENROUTER_API_KEY="$(op read 'op://Private/OpenRouter/credential')"`. The key never sits in a file you might commit. |
| One command only | `op run --env-file=... -- agency-index index ...`, or `OPENAI_API_KEY=... agency-index ...` (the latter lands in shell history). |
| CI | Store it in the CI system's secret store and expose it as the environment variable for the indexing step only. Do not echo it. |

Rules that keep it secret:

- Never put the key in `indexer.json`, a `.env` file that is not git-ignored, a command line, a commit,
  an issue or a chat. If an agent is doing the install, you set the variable yourself; the agent must not
  ask you to paste the key and must never print or log it.
- To check it is picked up without revealing it, run `agency-index doctor` and read the `api_key` check:
  it names the variable and never shows the value.
- If a key leaks (for example it was committed), revoke it at the provider first, create a new one,
  update the variable, then run `doctor`. Removing it from the file or history is not enough.
- A key left in `indexer.json` is flagged by `doctor` as `api_key_storage`; delete it from the file and
  treat it as exposed if the file was ever shared or committed.

### PostgreSQL (optional)

SQLite is the default and needs nothing. Use PostgreSQL with pgvector when several machines or people should
share one index database, or you already run one. The connection string contains the **password, so it is a
secret like the API key: it goes in the `AGENCY_INDEX_Database` environment variable and never in
`indexer.json`, a repo file or a command line you keep.**

1. **A server with pgvector.** If you have none, this runs one locally (the image already includes the extension):

   ```bash
   docker run -d --name agency-pg -e POSTGRES_USER=agency -e POSTGRES_PASSWORD=<choose a password> \
     -e POSTGRES_DB=agency -p 5432:5432 pgvector/pgvector:pg18-trixie
   ```

2. **Choose PostgreSQL** (this part is not secret, so `indexer.json` keeps it):

   ```json
   { "Provider": "postgres" }
   ```

   or run `agency-index setup --provider postgres ...`, which saves exactly that and tells you the connection
   string was not saved. Without a connection string, every command stops with a clear error instead of
   quietly using SQLite.
3. **Set the connection string in the environment**, the same ways as the [API key](#managing-the-key):

   ```text
   AGENCY_INDEX_Database=Host=localhost;Port=5432;Database=agency;Username=agency;Password=<password>
   ```

4. **Check it:** `agency-index doctor` shows `database` as "PostgreSQL connection opened", and flags
   `database_credentials` if a password was left in `indexer.json` (delete it from the file and treat the
   password as exposed if the file was ever shared).

Good to know:

- **First run:** the tool runs `CREATE EXTENSION IF NOT EXISTS vector` and creates `semantic_kv_store`,
  `semantic_kv_projects` and `kv_store` in the database. The user needs permission to create tables, and to create
  the extension unless an administrator has already run `CREATE EXTENSION vector;` there once.
- **Embedding size is fixed per database:** the vector column takes its width from `Dimensions` when the tables
  are first created. To switch to a model with a different size, use a new database (or drop those tables).
- **Shared tables:** other Agency applications can use the same database and tables, which is why
  [Remove it](#remove-it) deletes the rows but leaves the tables.

### 4. Index and verify

```bash
agency-index indexes                                    # lists existing indexes and their roots
agency-index index --index <repo-name> --root ./docs
agency-index search --index <repo-name> --query "how are releases published?"
```

Name the index after the repo folder, lower-case. Index names are global to the machine: if `indexes`
shows your name with a different `root`, another repo owns it, so pick another name (for example
`<org>-<repo>`). A good result is `"status":"ok"` with hits whose `score` is above about 0.5.

### 5. Calibrate the search threshold (recommended)

`agency-index` applies no minimum score by default, because scores depend on the embedding model. Without a
threshold, `search` always returns the top N hits, even for a question the docs never answer. Calibrate once per
model and index; it takes a few minutes.

**1. Measure the noise ceiling.** The quick way is `agency-index calibrate --index <repo-name>`: it runs twelve
unrelated queries and reports `noise_ceiling`. For a sharper number, also search 10-20 things your docs do not
cover, including a few plausible-sounding ones from your own stack, with the filter off:

```bash
agency-index search --index <repo-name> --query "chocolate cake recipe" --top 1 --min-score 0 --no-text
```

Record the highest `score` returned. It is the best score a hit can reach by chance.

**2. Measure the real-answer floor.** Write 15-30 real questions in your own words; do not reuse the documents'
title words. For each, note the document that should answer it. Run them with `--min-score 0 --top 5 --no-text`
and record the best score and whether the expected document is in the top 5.

**3. Pick a threshold in the gap.** Choose a value above the noise ceiling and below the real-answer floor, and
check how many real questions it would drop and how much noise it would keep:

| Threshold | Real questions kept | Noise let through |
| --- | --- | --- |
| 0.50 | all | some |
| 0.55 | all | few or none |
| 0.60 | some dropped | none |

If the two ranges overlap, no threshold separates them cleanly. Pick the value that keeps every real question
and rely on `best_score` to spot misses.

**4. Save it.** Either set it in the user-level file (a repo's `.agency-index.json` cannot set it):

```json
{ "Search": { "MinScore": 0.55 } }
```

or store the `calibrate` suggestion with the index: `agency-index calibrate --index <repo-name> --save` (used
only when no `Search:MinScore` or `--min-score` is set). The `calibrate` figure is only the noise side plus a
margin, so prefer the value from step 3 when you have measured the real-answer floor.

**5. Verify.** An unrelated query should now return no hits and say why:

```text
{"hits":[],"filtered":3,"best_score":0.4947,"min_score":0.55}
```

A real question that returns nothing shows its `best_score`. If it is just under the threshold, lower the
setting or pass `--min-score 0` for that search.

Notes:

- Recalibrate after changing the embedding model; scores are not comparable across models.
- Real answers cluster tightly. One measurement on this repo's docs with `text-embedding-qwen3-embedding-4b`:
  real answers scored 0.57-0.72 and noise reached 0.50, so a narrow gap is normal. Your numbers will differ.
- A threshold fixes weak hits, not ranking. If large, repetitive documents crowd out better ones, use
  [`Exclude`, `--path` and `--group-by-file`](#choosing-what-to-index-and-how-to-search-it) rather than a higher threshold.

## Per-repo config

A repo can carry its own defaults, so every command works from any folder inside it without `--index` and
`--root`. Put `.agency-index.json` at the repo root, or let `setup --index <name> --root <dir> --yes` write it:

```json
{ "Index": "myrepo", "Root": "docs", "Extensions": [".md"], "Names": ["README"], "MaxFileKb": 1024, "Exclude": ["docs/manual-tests", "**/*.draft.md"] }
```

Then, from any folder of the repo:

```bash
agency-index index                    # same as: --index myrepo --root <repo>/docs
agency-index search --query "how are releases published?"
```

| Rule | Behavior |
| --- | --- |
| Found like `.gitignore` | The nearest `.agency-index.json` walking up from the current folder wins outright. Files in between are not merged. |
| Relative `Root` | Relative to the folder holding the file, never the current folder, so it means the same thing everywhere. |
| Precedence, per key | Command line, then `AGENCY_INDEX_*` environment variables, then the repo file, then the user file (`~/.agency/indexer.json`), then the built-in default. A list such as `Extensions` is replaced, not merged. |
| User file | May set the same six keys as machine-wide defaults (use an absolute `Root` there), and holds everything else: endpoint, model, dimensions, database. |
| "This repo" | The nearest folder holding `.agency-index.json` or `.git`. It decides where `--scope repo` puts the skill and which indexes `uninstall` treats as this repo's, from any folder of it. |

**Only six keys are read from the repo file:** `Index`, `Root`, `Extensions`, `Names`, `MaxFileKb` and `Exclude`. Anything
else in it (`Embedding`, `Provider`, `Database`, `ApiKey`, ...) is ignored, and `doctor` reports it. The file
arrives with the repository, and a setting that chose the embeddings endpoint would let a cloned repo send
your document text, and the `OPENAI_API_KEY` Bearer token, to another server. Keep endpoint, model and
database in your user file.

Commit the file so teammates get the same index name and root. Each person's index still lives in their own
database, and index names are global to a machine: if the name is already owned by another repo's root,
`setup` stops with `name_taken` and suggests another. `agency-index doctor` shows the file in use
(`repo_config`) and each default with the layer it came from (`defaults`).

## Choosing what to index and how to search it

Which files go in matters more than any search option. Generated or log-like folders (manual test runs,
changelogs, meeting notes) can be half the chunks of a docs tree and crowd out the document you want.

**Leave files out.** `Exclude` takes gitignore-style globs relative to the index root, in `.agency-index.json`, in
`indexer.json`, in `AGENCY_INDEX_Exclude`, or as `--exclude` on `index`:

```bash
agency-index index --exclude "engineering/manual-tests,**/*.draft.md"
```

| Glob | Matches |
| --- | --- |
| `manual-tests` | a file or folder of that name at any depth, and everything under a folder |
| `engineering/manual-tests` | that folder under the root, and everything under it |
| `**/*.draft.md` | any `.draft.md` file at any depth |
| `*.md` inside a path (`docs/*.md`) | files directly in that folder; `*` never crosses a `/` |

The setting is remembered with the index; adding an exclusion removes the files it covers on the next run, and
removing one brings them back. `agency-index index --dry-run` shows the result before anything is written.

**One index or two?** One index per repo is the rule, and an exclusion usually removes the need for a second.
Build a second, narrower index (for example `myrepo-adr` over just the decision records) only when you keep
asking questions about one body of documents and want its answers ranked on their own; search both at once with
`--index myrepo,myrepo-adr`.

**Search options** (all optional, all flags of `search`):

| Option | Effect |
| --- | --- |
| `--path <glob>` | Only files whose path under the index root matches, for example `--path "adr"` or `--path "engineering/**"`. |
| `--group-by-file` / `--per-file N` | At most the best 1 (or N) chunks of any one file, so one file cannot take the top three places. |
| `--hybrid` | Also rank by keyword match, for questions that name a config key, analyzer id or ADR number. Chunks containing an identifier from the query are exempt from `--min-score`. It reorders the best 50 or so vector hits; it cannot find a chunk the vector search ranked far down. |
| `--index a,b` | Search several indexes (built with the same model) and merge the hits by score; each hit names its `index`. |
| `--min-score`, `--within`, `--no-text`, `--snippet-chars`, `--full` | Chunk text is left out unless `--full` or `--snippet-chars` is given. As in the [search threshold](#3-point-it-at-an-embeddings-endpoint) section and the skill. |

Each hit of an index built by this version carries `heading` (the Markdown heading path above the chunk, for
example `Guide > Install`) and `start_line` / `end_line`, so an agent can open the exact section. Hits from
indexes built earlier simply lack them; `index --rebuild` adds them.

**Switching the embedding model** is one command: `agency-index index --index <name> --rebuild`. It re-embeds every
file with the configured model, keeps serving searches while it runs (a search during the rebuild mixes old and
new vectors, so treat results as unreliable until it finishes) and, if it is interrupted, a plain `agency-index
index` run finishes it. On PostgreSQL the vector column cannot change width, so a model with a different size
needs a new database there.

**Long output.** `list` and `index --dry-run` print file paths relative to the `root` in the result, and
`--summary` prints counts instead of lists (`index`, `index --dry-run`, `list`).

## First index

Each file's chunks are embedded in requests of at most 32 inputs, one request at a time, so a local
model is not flooded. Expect minutes, not seconds, for a docs tree of a few hundred files: about
3 minutes for 55 files on a local model, and the whole tree took over 10 minutes in one reported
install.

- `agency-index index --index <name> --root <dir> --dry-run` writes nothing and reports the files to
  index, the chunk count and a time estimate. The estimate embeds about 48 chunks spread across the whole set and
  scales their time by text length; it is still a guess, since a local model slows down under sustained load, so
  say "about" and expect a run to take somewhat longer. Run it first and tell the user.
- A real run prints progress lines to stderr (`indexing 12/340 files, 410/1980 chunks, ~6 min left`, with the
  chunk total known up front) and a `still working on <file>` line every 30 seconds, while stdout stays one JSON
  object at the end. Run it in the background with `--log <file>` and read the file to follow it.
- A file that cannot be embedded is reported the moment it fails, on stderr as `FAILED <path>: <reason>` and in
  the final JSON as `failed: [{path, reason}]` (the reason includes the HTTP status and the start of the
  server's response body). `--log <file>` also appends the progress and failure lines, timestamped, to a file.
  The rest of the run continues and the failed file is retried next time. Transient errors (timeout, 429, 5xx,
  connection refused) are retried first, 3 times with a 1, 2, 4 second wait; set `Embedding:MaxRetries` and
  `Embedding:RetryDelayMs` in `indexer.json` to change that (`RetryDelayMs: 0` retries immediately).
- Later runs only process added, changed or deleted files and take seconds.

## Where things live

| What | Default location |
| --- | --- |
| The tool | `~/.dotnet/tools/agency-index` (Windows: `%USERPROFILE%\.dotnet\tools\agency-index.exe`); `dotnet tool list -g` shows it |
| The skill, this repo | `<repo>/.claude/skills/agency-index/SKILL.md` and `REFERENCE.md` |
| The skill, all repos | `~/.claude/skills/agency-index/` and `~/Agents/skills/agency-index/`, each with `SKILL.md` and `REFERENCE.md` (or the `--dir` you installed with) |
| Config, user | `~/.agency/indexer.json` (Windows: `C:\Users\<you>\.agency\indexer.json`) |
| Config, repo | `<repo>/.agency-index.json`, committable; index defaults only (see [Per-repo config](#per-repo-config)) |
| Database, SQLite (default) | `~/.agency/index.db`, plus `index.db-wal` and `index.db-shm` while it is open and `index.db.<index>.lock` while an index run holds the writer lock, all in the same folder |
| Database, PostgreSQL | The database named by your connection string, in the tables `semantic_kv_store`, `semantic_kv_projects` and `kv_store` (and the `vector` extension) |
| The API key | Not stored by the tool. It lives in your environment or secret manager (see [API key](#api-key-openai-openrouter)) |

The default SQLite database is **one file for every index on the machine**, for every repo, which is why
index names are global. Move it with the `Database` key in `indexer.json`, `AGENCY_INDEX_Database` or `--db`.

To run against a different profile folder (a sandbox, CI, a test), set `AGENCY_INDEX_HOME`: the `.agency` folder
and the user-scope skill folders are then taken from it instead of your real profile.

You do not have to remember these: `agency-index doctor` prints the config path, the database path and
every skill copy it finds (marking stale ones), and `agency-index indexes` lists each index with the root
it belongs to.

## Remove it

One command does it, in two steps: preview, then apply. It never removes the tool itself (a running
program cannot delete its own executable), so the last step is printed for you to run.

```bash
agency-index uninstall                       # preview for this repo only; changes nothing
agency-index uninstall --yes                 # apply: drop this repo's indexes, remove this repo's skill
agency-index uninstall --scope all           # preview everything
agency-index uninstall --scope all --yes     # apply everything
dotnet tool uninstall -g AgencyDotNet.Indexer   # the last step, listed under "remaining"
```

| Scope | Removes | Leaves |
| --- | --- | --- |
| `repo` (default) | The indexes whose root is inside this repo, `<repo>/.claude/skills/agency-index/` and the repo's `.agency-index.json` | Other repos' indexes, the user-scope skill, the database file, `indexer.json`, the tool |
| `all` | Every index, every skill copy (this repo and both user folders), the SQLite database files (`index.db`, `-wal`, `-shm`, `.lock`) and `indexer.json` | The tool and your API key |

The output is one JSON object: each skill file, each index with `this_repo` and what happened to it
(`would_drop`, `dropped`, `locked`, `keep`), the data files, the config file, the repo config file, and `remaining`. Use `--dir
<skills-root>` to include a skill folder you installed with `--dir`. If an index is being written by
another process the command reports it as `locked`, deletes none of the shared data, and exits 1; re-run
when the other run finishes. On PostgreSQL it deletes the rows but not the tables or the `vector`
extension, because other Agency applications can share them; drop those yourself only if the database is
dedicated to the indexer. The API key lives in your environment or secret manager: remove the
`OPENAI_API_KEY` / `OPENROUTER_API_KEY` / `AGENCY_INDEX_Embedding__ApiKey` entry yourself and revoke the
key at the provider if nothing else uses it.

### Ask your agent to remove it

Paste this into any coding agent:

> Remove the Agency.Indexer by following the "Remove it" section of `docs/Install-Indexer-Skill.md`.
> Run `agency-index uninstall` (a preview) and show me what it found, saying which indexes belong to
> this repo and which to other repos. Ask me whether to remove only this repo or everything. Apply it
> with `--yes` (and `--scope all` if I chose everything) only after I agree, then tell me the
> `remaining` steps and run the tool uninstall only if I say so.

Actions that need explicit approval: any `--yes`, and especially `--scope all`, which drops other
repos' indexes and deletes the database and config that serve every repo on the machine; and the global
tool uninstall.

### By hand

If the tool is already gone or broken: delete the skill folders listed in [Where things
live](#where-things-live), delete `~/.agency/index.db` with its `-wal`, `-shm` and `*.lock` files (once
no index run is in progress) and `~/.agency/indexer.json`, then run `dotnet tool uninstall -g
AgencyDotNet.Indexer`.

Check it worked: `agency-index` is "not found", `dotnet tool list -g` no longer lists
`AgencyDotNet.Indexer`, and the skill, config and database paths from the table above are gone.

## Upgrading

After upgrading the tool, `doctor` flags a skill that no longer matches the bundled one; re-run
`setup --yes` (or `install-skill` with the same scope) to refresh it.

See [Agency.Indexer](Projects/Agency.Indexer.md) for how the tool works.
