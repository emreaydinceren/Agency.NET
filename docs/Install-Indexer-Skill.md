# Install the documentation search skill (`agency-index`)

`agency-index` gives a coding agent semantic search over a folder of documentation. It needs a .NET SDK
and an OpenAI-compatible embeddings endpoint (LM Studio, Ollama, OpenAI, ...). Installing takes a few
minutes; the first index of a large docs tree takes longer (see [First index](#first-index)).

## Ask your agent to do it

Paste this into any coding agent:

> Install the Agency.Indexer skill by following `docs/Install-Indexer-Skill.md`. Before changing anything,
> run `agency-index doctor` (if the tool is missing, offer the global install and wait for my yes).
> Then ask me, one question at a time with the default stated: skill scope, embeddings provider and
> model, and which folder to index. Run `agency-index setup` without `--yes` and show me what it
> would write; apply it with `--yes` only after I agree. Finish with the smoke-test search it runs
> and tell me the result. Do not install anything globally, write outside the chosen scope, or start
> a long index without telling me the `agency-index index --dry-run` estimate first.

Actions that need explicit approval: the global tool install, writing `~/.agency/indexer.json`, writing
to `~/.claude/skills`, and a first index estimated to take over a minute.

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
`--index` or `--root` is given, runs the first index and one smoke-test search (`--query` to change it).
It stops with exit 2 and `status: name_taken` if the index name belongs to another root. Options:
`--scope repo|user`, `--endpoint lmstudio|ollama|openai|openrouter` or `--embedding-url <url>`,
`--embedding-model`. Hosted endpoints need their [API key](#api-key-openai-openrouter) in the
environment first. The manual steps below show what it does.

## Choices

| Choice | Options | Default |
| --- | --- | --- |
| Skill scope | this repo (`<repo>/.claude/skills`) / all my repos (`~/.claude/skills`) | this repo |
| Embeddings | LM Studio / Ollama / OpenAI / other OpenAI-compatible | LM Studio |
| Storage | SQLite / PostgreSQL + pgvector | SQLite |
| What to index first | a folder, or skip | `docs/` if it exists |

## Steps

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
# this repo only (default choice)
agency-index install-skill --scope repo

# all my repos
agency-index install-skill --scope user
```

`--scope repo` writes `./.claude/skills/agency-index/`; `--scope user` (the default for this command)
writes `~/.claude/skills/agency-index/` and `~/Agents/skills/agency-index/`; `--dir <skills-root>`
chooses any other location. The output lists each file and whether it replaced an existing one.

### 3. Point it at an embeddings endpoint

There is no default endpoint: `index` and `search` exit 2 until one is configured. Create
`~/.agency/indexer.json` (on Windows `C:\Users\<you>\.agency\indexer.json`; Git Bash still resolves
`~/.agency`):

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
| `text-embedding-3-small` (OpenAI) | 1536 |

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

For PostgreSQL, add `"Provider": "postgres"` and `"Database": "<connection string>"` (pgvector required).

### 4. Index and verify

```bash
agency-index indexes                                    # lists existing indexes and their roots
agency-index index --index <repo-name> --root ./docs
agency-index search --index <repo-name> --query "how are releases published?"
```

Name the index after the repo folder, lower-case. Index names are global to the machine: if `indexes`
shows your name with a different `root`, another repo owns it, so pick another name (for example
`<org>-<repo>`). A good result is `"status":"ok"` with hits whose `score` is above about 0.5.

## First index

Each file's chunks are embedded in requests of at most 32 inputs, one request at a time, so a local
model is not flooded. Expect minutes, not seconds, for a docs tree of a few hundred files: about
3 minutes for 55 files on a local model, and the whole tree took over 10 minutes in one reported
install.

- `agency-index index --index <name> --root <dir> --dry-run` writes nothing and reports the files to
  index, the chunk count and a time estimate from embedding a three-file sample. Run it first and tell
  the user the estimate.
- A real run prints progress lines to stderr (`indexing 12/340 files, 410 chunks, ~6 min left`) while
  stdout stays one JSON object at the end. Run it in the background or with a generous timeout.
- Later runs only process added, changed or deleted files and take seconds.

## Uninstall

```bash
agency-index drop --index <repo-name>
agency-index uninstall-skill --scope repo      # or --scope user, or --dir <skills-root>
dotnet tool uninstall -g AgencyDotNet.Indexer
```

After upgrading the tool, `doctor` flags a skill that no longer matches the bundled one; re-run
`setup --yes` (or `install-skill` with the same scope) to refresh it.

See [Agency.Indexer](Projects/Agency.Indexer.md) for how the tool works.
