# Configuration Manual

This document is the single reference for every configuration key in the Agency solution — where it lives, what it does, and which projects consume it.

Run `src\SetupLocal.ps1` from the repo root to be prompted for secrets interactively. The script writes them into the correct `dotnet user-secrets` vaults automatically.

---

## Shared Configuration and Placeholder Resolution

Agency projects that share a common value (e.g. the LM Studio host or the API key) define it once in `src/shared-appsettings.json` and reference it with `${Section:Key}` tokens in their own `appsettings.json`. Secrets such as the Postgres connection string are **not** in that file — they live in the `AgencySecrets` user-secrets vault and are referenced by the same placeholder syntax. Two extension methods in [`Agency.Configuration`](Projects/Agency.Configuration.md) wire this up.

> **Scope:** `Agency.Harness.Console` wires `AddSharedConfiguration` / `AddPlaceholderResolver` for the runtime host. The functional-test projects (`Agency.Llm.Test`, `Agency.Harness.Test`, `Agency.Embeddings.OpenAI.Test`, `Agency.Memory.Functional.Test`) also wire the same two extensions in their configuration builders, but reference a **second** shared file — [`shared-test-appsettings.json`](#shared-test-appsettingsjson) — that carries the test-proxy endpoint and test API key. The runtime shared file stays free of test-only concerns.

### shared-appsettings.json

`src/shared-appsettings.json` sits at the solution root and carries non-secret values common to multiple projects. The shipped default defines the LM Studio client endpoints and the shared API key:

```json
{
  "LLmClients": {
    "OpenAI": { "BaseUrl": "http://llm.test:1234/v1" },
    "Claude": { "BaseUrl": "http://llm.test:1234" },
    "ApiKey": "lm-studio"
  }
}
```

These keys live under a dedicated top-level section (`LLmClients`) and are referenced — not redefined — by each consumer's `appsettings.json`. The Postgres connection string is deliberately **absent** here: it is a secret, sourced from the [`AgencySecrets`](#agencysecrets-shared-test-vault) user-secrets vault as `ConnectionStrings:PostgreSql`, and reached through the same `${ConnectionStrings:PostgreSql}` placeholder.

Each consuming project links the file in its `.csproj` so it is copied next to `appsettings.json` in the output directory at runtime:

```xml
<Content Include="..\..\shared-appsettings.json" Link="shared-appsettings.json" Pack="false">
  <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
</Content>
```

Any key in the fully merged configuration — including environment variables and user secrets — can serve as a resolution target.

### shared-test-appsettings.json

`src/shared-test-appsettings.json` is a **second**, test-only shared file. It carries the values that every functional-test project shares — the offline HTTP-cache **proxy** endpoint (distinct from the runtime LM Studio host), the test API key, and the default test model — without polluting the runtime `shared-appsettings.json`:

```json
{
  "TestProxy": {
    "OpenAI": { "BaseUrl": "http://proxy.test:12345/v1" },
    "Claude": { "BaseUrl": "http://proxy.test:12345" },
    "ApiKey": "lm-studio",
    "Model": "google/gemma-4-e2b"
  }
}
```

Each functional-test project links the file and references it by placeholder (`${TestProxy:OpenAI:BaseUrl}`, `${TestProxy:ApiKey}`, `${TestProxy:Model}`). The test config builders register it alongside the resolver:

```csharp
new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddSharedConfiguration("shared-test-appsettings.json")  // test-proxy endpoint / key / model
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile($"appsettings.{environmentName}.json", optional: true)
    .AddUserSecrets<T>(optional: true)
    .AddEnvironmentVariables()
    .AddPlaceholderResolver()                                // LAST
    .Build();
```

`Agency.Memory.Functional.Test` resolves `ConnectionStrings:PostgreSql` from the `AgencySecrets` vault (`UserSecretsId=AgencySecrets` + `AddUserSecrets("AgencySecrets")`), so it does **not** link the runtime `shared-appsettings.json` — it references nothing else from there. The Console host loads `shared-test-appsettings.json` **only under `DOTNET_ENVIRONMENT=Test`**, where its `appsettings.Test.json` references the `${TestProxy:…}` tokens; in production those tokens are never present so the file is not needed. The Console additionally loads the `AgencySecrets` vault in every environment (Host auto-loads user secrets only in Development) so `${ConnectionStrings:PostgreSql}` always resolves.

### Placeholder notation

A placeholder has the form `${Section:Key}`, where the embedded key uses the standard IConfiguration colon (`:`) path separator. The resolver expands each token to the value of that key in the **fully merged** configuration. Resolution is recursive: a resolved value that itself contains tokens is expanded in turn (depth-limited to 32).

**Colon required.** Only tokens that contain a `:` are treated as configuration references. A bare token such as `${RepoRoot}` or `${Configuration}` (see [Mcp](#mcp)) is left **verbatim** — those belong to `McpConfigResolver`, which expands them later against runtime paths. This lets both systems share the `${…}` syntax without the placeholder resolver claiming or failing on the MCP tokens.

**Canonical example** — `appsettings.json` references the shared client endpoints and API key:

```json
{
  "Agent": {
    "LLmClients": [
      { "BaseUrl": "${LLmClients:OpenAI:BaseUrl}", "ApiKey": "${LLmClients:ApiKey}" },
      { "BaseUrl": "${LLmClients:Claude:BaseUrl}", "ApiKey": "${LLmClients:ApiKey}" }
    ]
  },
  "Embedding": {
    "BaseUrl": "${LLmClients:OpenAI:BaseUrl}",
    "ApiKey": "${LLmClients:ApiKey}"
  }
}
```

With the shipped shared file, the OpenAI `BaseUrl` resolves to `http://llm.test:1234/v1`, the Claude `BaseUrl` to `http://llm.test:1234`, and both `ApiKey` values to `lm-studio`.

To emit a literal `${...}` string without resolution, escape the dollar sign by doubling it: `$${LLmClients:ApiKey}` → `${LLmClients:ApiKey}`.

### Wiring in Program.cs

```csharp
var builder = Host.CreateApplicationBuilder(args);
// Host has already registered appsettings.json, env vars, user secrets, and CLI args.

builder.Configuration.AddSharedConfiguration();   // inserts shared-appsettings.json at the FRONT (lowest precedence)
builder.Configuration.AddPlaceholderResolver();   // LAST — snapshots the merged config and expands ${…} tokens
```

Both methods extend `IConfigurationBuilder` and live in the `Microsoft.Extensions.Configuration` namespace — no extra `using` directive is needed alongside the standard host setup.

**Call order rules:**

- Call `AddSharedConfiguration()` after the host builder is created. It **inserts the shared file at the front** of the source list — the *lowest* precedence — so the standard host sources (appsettings, env vars, user secrets, CLI) all override it. Call ordering relative to those sources therefore does not matter; only the resolver must come last.
- Call `AddPlaceholderResolver()` **once, as the last configuration step** — after all sources are registered and before any values are read. It takes a point-in-time snapshot of all merged key/value pairs and publishes the expanded values as the highest-priority source. Every subsequent read of `IConfiguration` — including `IOptions<T>` binding via `Configure<T>` or `BindConfiguration` — sees the resolved values.

### Environment-variable override

Because the shared file is the lowest-precedence source and env vars are in the merged snapshot when `AddPlaceholderResolver()` runs, overriding a shared value requires only one change:

```bash
LLmClients__OpenAI__BaseUrl=http://my-other-host:1234/v1 dotnet run
```

Every `${LLmClients:OpenAI:BaseUrl}` token in `appsettings.json` resolves against the overridden value, so the new host propagates automatically to the OpenAI client and the embedding endpoint.

### Error behaviour

| Condition | Result |
|---|---|
| Colon-bearing placeholder references an absent key | `InvalidOperationException` at startup, before any service registrations. (A bare token like `${RepoRoot}` does **not** trigger this — it is passed through.) |
| Resolution chain contains a cycle | `InvalidOperationException` with the full chain (e.g. `A -> B -> A`). |
| Chain exceeds 32 levels | `InvalidOperationException` with the depth limit and the owning key. |

---

## Configurations

Each section below describes one logical configuration group. "Example config" shows the key in `appsettings.json`. Secrets that must not be checked in are instead set via `dotnet user-secrets` and are marked **secret**.

> **"Default" column** — values shown are from the shipped `appsettings.json`, not the C# type-level defaults (which are typically `null` or `string.Empty`).

---

### Agent

Controls which LLM clients are available, which one is active by default, and general agent loop behaviour.

| Key | Type | Default | Notes |
|---|---|---|---|
| `Agent:DefaultClientName` | string | `"LocalVia-OpenAI-API"` | Must match a `Name` in `LLmClients`. |
| `Agent:DefaultModel` | string | `"google/gemma-4-e2b"` | Model id passed to the chosen client. |
| `Agent:ProgressiveDiscovery` | bool | `true` | When true, MCP tool schemas are withheld until the model calls `tool_help`. |
| `Agent:LogToolPayloads` | bool | `false` | Opt-in verbose logging of full tool inputs and outputs. |
| `Agent:UserId` | string | *(auto-generated)* | Stable user identity for memory partitioning. Written back to `appsettings.json` on first run if absent. |
| `Agent:TurnTimeoutSeconds` | int? | `null` | Cancels a single agent turn after N seconds. |
| `Agent:ContextWindowSize` | int? | `null` | Injected into the system prompt as a context-window budget hint for the model. |
| `Agent:LLmClients` | array | see below | One entry per LLM endpoint. Each entry has the fields below. |
| `Agent:LLmClients[].Name` | string | required | Identifier used by `DefaultClientName` and `/model`. |
| `Agent:LLmClients[].ClientType` | string | required | `"OpenAI"` or `"Claude"`. |
| `Agent:LLmClients[].BaseUrl` | string | required | Full HTTP base URL of the endpoint. |
| `Agent:LLmClients[].ApiKey` | string | required | API key; shipped value references the shared `${LLmClients:ApiKey}` (`"lm-studio"`, which LM Studio ignores). |
| `Agent:LLmClients[].Timeout` | TimeSpan? | `null` | Per-request HTTP timeout, e.g. `"00:10:00"`. |
| `Agent:LLmClients[].SuppressThinking` | bool | `false` | Forces `enable_thinking: false` — needed for some Qwen3 deployments. |
| `Agent:LLmClients[].MaxRetries` | int? | `null` | HTTP retry count. |

```json
"Agent": {
  "DefaultClientName": "LocalVia-OpenAI-API",
  "DefaultModel": "google/gemma-4-e2b",
  "ProgressiveDiscovery": true,
  "LogToolPayloads": false,
  "LLmClients": [
    {
      "Name": "LocalVia-OpenAI-API",
      "ClientType": "OpenAI",
      "BaseUrl": "${LLmClients:OpenAI:BaseUrl}",
      "ApiKey": "${LLmClients:ApiKey}",
      "Timeout": "00:10:00",
      "SuppressThinking": true
    },
    {
      "Name": "LocalVia-Claude-API",
      "ClientType": "Claude",
      "BaseUrl": "${LLmClients:Claude:BaseUrl}",
      "ApiKey": "${LLmClients:ApiKey}"
    }
  ]
}
```

**Used by:** [Agency.Harness.Console](#agencyharnessconsole), [Agency.Harness.Test](#agencyharnestest)

---

### Embedding

Configures the OpenAI-compatible embeddings endpoint used to vectorise documents and queries. Activating this section also enables the vector store, ingestion, and semantic-search tool in Harness.Console — independently of whether memory is on.

| Key | Type | Default | Notes |
|---|---|---|---|
| `Embedding:BaseUrl` | string? | `null` | Absence disables the entire embeddings / vector-store data plane. |
| `Embedding:ModelId` | string? | `null` | Embedding model name sent in the request. |
| `Embedding:ApiKey` | string? | `null` | Required by the OpenAI SDK; LM Studio ignores the value. |
| `Embedding:Dimensions` | int? | `null` | Output vector width. Used at schema-initialisation time by vector stores. |

```json
"Embedding": {
  "BaseUrl": "${LLmClients:OpenAI:BaseUrl}",
  "ApiKey": "${LLmClients:ApiKey}",
  "ModelId": "local-embedding-model",
  "Dimensions": 1024
}
```

**Used by:** [Agency.Harness.Console](#agencyharnessconsole), [Agency.Embeddings.OpenAI.Test](#agencyembeddingsopenaitest)

---

### ConnectionStrings

Database connection strings for memory storage and the vector store. The Postgres string is a **secret**: it lives in the `AgencySecrets` user-secrets vault as `ConnectionStrings:PostgreSql` — never in a committed `appsettings`/`shared-appsettings` file — and `appsettings.json` references it by placeholder. The Console loads that vault in every environment so the placeholder always resolves.

| Key | Default | Purpose |
|---|---|---|
| `ConnectionStrings:PostgreSql` | _(none — from `AgencySecrets` user secret)_ | Memory store (Postgres provider) and pgvector search. **Sourced from the `AgencySecrets` vault**, not any appsettings file. |
| `ConnectionStrings:Sqlite` | `Data Source=agency-memory.db` | Memory store (SQLite provider). |
| `ConnectionStrings:VectorStoreSqlite` | `Data Source=agency-vectorstore.db` | Vector store (SQLite provider). |
| `ConnectionStrings:VectorStorePostgreSql` | `${ConnectionStrings:PostgreSql}` | Vector store (Postgres provider). References the secret Postgres string by placeholder. |

`appsettings.json` (the `PostgreSql` key is supplied by the `AgencySecrets` user secret):

```json
"ConnectionStrings": {
  "Sqlite": "Data Source=agency-memory.db",
  "VectorStoreSqlite": "Data Source=agency-vectorstore.db",
  "VectorStorePostgreSql": "${ConnectionStrings:PostgreSql}"
}
```

> **Note:** `VectorStorePostgreSql` resolves to the same value as `PostgreSql` via the placeholder, so by default both point at the same local Postgres instance. To split memory and vector storage onto different servers in production, give `VectorStorePostgreSql` a literal string instead of the placeholder (or override `ConnectionStrings:PostgreSql` and let both follow).

To override the PostgreSQL string without committing credentials, run:

```powershell
dotnet user-secrets set -p src\Sql\Agency.Sql.Postgres.Test `
  "ConnectionStrings:PostgreSql" "Host=<host>;Port=5432;Username=<user>;Password=<pass>;Database=<db>"
```

All projects that share Postgres credentials read from the `AgencySecrets` vault.

**Used by:** [Agency.Harness.Console](#agencyharnessconsole), [Agency.Memory.Functional.Test](#agencymemoryfunctionaltest), Agency.Sql.Postgres.Test, Agency.Memory.Sql.Postgres.Test, Agency.VectorStore.Sql.Postgres.Test, Agency.KeyValueStore.Sql.Postgres.Test

---

### Memory

Enables or disables the long-term conversational memory subsystem inside Harness.Console. Turning this on starts the Distiller, Consolidator, and Hygiene background services and requires an embeddings endpoint and a backing database.

| Key | Type | Default | Notes |
|---|---|---|---|
| `Memory:Enabled` | bool | `false` | When false the memory pipeline is not registered. Embeddings / vector store still work. |
| `Memory:Provider` | string | `"postgres"` | `"postgres"` or `"sqlite"`. Selects the backing store for the memory schema. |

```json
"Memory": {
  "Enabled": false,
  "Provider": "postgres"
}
```

**Used by:** [Agency.Harness.Console](#agencyharnessconsole)

---

### VectorStore

Selects the storage backend for the document ingestion and semantic-search data plane. Active only when `Embedding:BaseUrl` is set.

| Key | Type | Default | Notes |
|---|---|---|---|
| `VectorStore:Provider` | string | `"sqlite"` | `"sqlite"` uses `ConnectionStrings:VectorStoreSqlite`; `"postgres"` uses `ConnectionStrings:VectorStorePostgreSql`. |

```json
"VectorStore": {
  "Provider": "sqlite"
}
```

**Used by:** [Agency.Harness.Console](#agencyharnessconsole)

---

### Ingestion

Controls how documents are split into chunks when ingested via `/add-file` or `/add-folder`.

| Key | Type | Default | Notes |
|---|---|---|---|
| `Ingestion:ChunkSize` | int | `512` | Maximum tokens per chunk. |
| `Ingestion:ChunkOverlap` | int | `64` | Overlap tokens between consecutive chunks. |
| `Ingestion:SearchPattern` | string | `"*.md"` | Default glob used when ingesting a directory. |

```json
"Ingestion": {
  "ChunkSize": 512,
  "ChunkOverlap": 64,
  "SearchPattern": "*.md"
}
```

**Used by:** [Agency.Harness.Console](#agencyharnessconsole)

---

### Retrieval

Controls how many results are returned by the `semantic_search` tool.

| Key | Type | Default | Notes |
|---|---|---|---|
| `Retrieval:TopK` | int | `5` | Number of nearest-neighbour results returned per search. |

```json
"Retrieval": {
  "TopK": 5
}
```

**Used by:** [Agency.Harness.Console](#agencyharnessconsole)

---

### Mcp

Declares MCP (Model Context Protocol) servers whose tools are injected into the agent's tool registry. Startup is skipped entirely when `DOTNET_ENVIRONMENT=Test`.

Path values support three portability tokens, expanded by `McpConfigResolver` *after* the host is built (not by the placeholder resolver — all three are bare, colon-less tokens, so the [placeholder resolver](#placeholder-notation) passes them through untouched):
- `${RepoRoot}` — resolved to the nearest ancestor directory containing `.git`.
- `${Configuration}` — resolved to `Debug` or `Release` from the output path.
- `${GitHubToken}` — resolved to the `GitHub:PersonalAccessToken` configuration value (sourced from the `AgencySecrets` vault, same as `ConnectionStrings:PostgreSql`). Read directly via `IConfiguration`, not the placeholder resolver, so a missing secret never fails config build. **Optional:** when absent, `McpConfigResolver` removes the containing `EnvironmentVariables` entry entirely instead of substituting an empty string — this lets an ambient OS environment variable of the same name (e.g. one `RunConsole.ps1` sets before launch) still reach the subprocess, preserving Docker's own `-e GITHUB_PERSONAL_ACCESS_TOKEN` passthrough behavior as a fallback.

The shipped `github` server illustrates the optional-secret pattern:

```json
{
  "Name": "github",
  "Transport": "Stdio",
  "Command": "docker",
  "Arguments": [ "run", "-i", "--rm", "-e", "GITHUB_PERSONAL_ACCESS_TOKEN", "ghcr.io/github/github-mcp-server" ],
  "EnvironmentVariables": {
    "GITHUB_PERSONAL_ACCESS_TOKEN": "${GitHubToken}"
  }
}
```

To set the token, add it to the same always-loaded `AgencySecrets` vault used for the Postgres connection string:

```powershell
dotnet user-secrets set -p src\Sql\Agency.Sql.Postgres.Test "GitHub:PersonalAccessToken" "<token>"
```

| Key | Type | Notes |
|---|---|---|
| `Mcp:Servers[].Name` | string | Display name and key for `ToolNamesByServer`. |
| `Mcp:Servers[].Transport` | `"Stdio"` \| `"Http"` | Stdio spawns a child process; Http POSTs to a URL. |
| `Mcp:Servers[].Command` | string | Stdio only. Executable to run. |
| `Mcp:Servers[].Arguments` | string[] | Stdio only. CLI arguments. |
| `Mcp:Servers[].EnvironmentVariables` | object | Stdio only. Extra environment for the child process. |
| `Mcp:Servers[].Url` | string | Http only. MCP endpoint URL. |

```json
"Mcp": {
  "Servers": [
    {
      "Name": "memory",
      "Transport": "Stdio",
      "Command": "dotnet",
      "Arguments": [
        "${RepoRoot}/src/Mcp/Agency.Mcp.Memory/bin/${Configuration}/net10.0/Agency.Mcp.Memory.dll"
      ],
      "EnvironmentVariables": {
        "Memory__Provider": "sqlite",
        "Memory__ConnectionString": "Data Source=agency-mcp-memory.db"
      }
    },
    {
      "Name": "notion",
      "Transport": "Stdio",
      "Command": "npx",
      "Arguments": ["-y", "@notionhq/notion-mcp-server"]
    }
  ]
}
```

**Used by:** [Agency.Harness.Console](#agencyharnessconsole)

---

### Skills

Configures where skill directories are discovered and whether skills can run shell commands.

| Key | Type | Default | Notes |
|---|---|---|---|
| `Skills:Directories` | string[] | `[]` | Extra skill root paths. Defaults fall back to `./.agency/skills` then `~/.agency/skills`. |
| `Skills:DisableShellExecution` | bool | `false` | When true, `!`cmd`` and fenced ```` ```! ```` directives are shown as text but never executed. |

```json
"Skills": {
  "Directories": [],
  "DisableShellExecution": false
}
```

**Used by:** [Agency.Harness.Console](#agencyharnessconsole)

---

### Permissions

Controls whether tool calls require user approval before execution. Rules use glob syntax; unresolved calls can be configured to `Ask` the user or silently `Deny`.

| Key | Type | Default | Notes |
|---|---|---|---|
| `Permissions:Enabled` | bool | `true` | When false the permission evaluator is not registered and all calls are allowed. |
| `Permissions:Allow` | string[] | `["ReadFile"]` | Tool names or glob patterns that are always allowed. |
| `Permissions:Deny` | string[] | `[]` | Tool names or glob patterns that are always denied. |
| `Permissions:OnUnresolved` | `"Ask"` \| `"Deny"` | `"Ask"` | What to do when no rule matches. Use `"Deny"` for headless / CI runs. |
| `Permissions:ToolInputKeys` | object | `{}` | Maps tool name → input field name used as the display key in approval prompts. |
| `Permissions:LocalRulesPath` | string? | `null` | Path to a `permissions.local.json` file where "Allow always / Deny always" grants are persisted. |

```json
"Permissions": {
  "Enabled": true,
  "Allow": ["ReadFile"],
  "Deny": [],
  "OnUnresolved": "Ask",
  "ToolInputKeys": {},
  "LocalRulesPath": null
}
```

**Used by:** [Agency.Harness.Console](#agencyharnessconsole)

---

### Hooks

Defines operator-level lifecycle hooks that run external processes or POST to HTTP endpoints at agent loop events. Hooks are the middle layer in the three-source composition: Baseline (memory pipeline) → **Configured (this section)** → User (caller-supplied).

The top-level key is an event name; each event holds a list of matcher groups.

| Event name | Fires when |
|---|---|
| `PreToolUse` | Before a tool is invoked. Can deny by exiting with code `2`. |
| `PostToolUse` | After a tool result is received. |
| `PostToolBatch` | After all tools in one parallel batch complete. |
| `AssistantTurn` | After each LLM response message. |
| `UserPromptSubmit` | Before the user message enters the loop. |
| `PreIteration` | Before each LLM call. |
| `SessionStart` / `SessionEnd` | Session lifecycle. |
| `Stop` | When the agent loop halts. |

Each handler has:

| Key | Type | Notes |
|---|---|---|
| `Matcher` | string? | Tool-name filter. `null`/`"*"` = all; `"a\|b"` = exact set; anything else is a regex. |
| `Hooks[].Type` | `"Command"` \| `"Http"` | Handler kind. |
| `Hooks[].Command` | string | Command handler: executable path. |
| `Hooks[].Args` | string[] | Command handler: CLI arguments. |
| `Hooks[].Timeout` | int? | Seconds before the handler is killed. Default 30. |
| `Hooks[].Url` | string | Http handler: POST endpoint. |
| `Hooks[].Headers` | object | Http handler: extra request headers. |

```json
"Hooks": {
  "PreToolUse": [
    {
      "Matcher": "execute_powershell",
      "Hooks": [
        { "Type": "Command", "Command": "python", "Args": ["hooks/audit.py"], "Timeout": 10 }
      ]
    }
  ],
  "PostToolUse": [
    {
      "Hooks": [
        { "Type": "Http", "Url": "http://localhost:9090/hook", "Timeout": 5 }
      ]
    }
  ]
}
```

**Used by:** [Agency.Harness.Console](#agencyharnessconsole)

---

### OpenTelemetry

Configures file-based traces, metrics, and structured logs exported by Harness.Console. All three pipelines are independently toggled. Files land under `FileExport:OutputDirectory` and roll daily.

| Key | Type | Default | Notes |
|---|---|---|---|
| `OpenTelemetry:ServiceName` | string | `"Agency.Harness.Console"` | OTel `service.name` resource attribute. Per-installation preference; set via `SetupLocal.ps1`. |
| `OpenTelemetry:FileExport:OutputDirectory` | string | `"./logs"` | Directory created at startup if absent. |
| `OpenTelemetry:FileExport:Traces:Enabled` | bool | `true` | Exports spans to `traces-yyyy-MM-dd.log`. ON by default; set to `false` via user-secrets to disable. |
| `OpenTelemetry:FileExport:Traces:FilePrefix` | string | `"traces"` | Log file name prefix. Per-installation preference. |
| `OpenTelemetry:FileExport:Traces:SamplingRatio` | double | `1.0` | 0.0–1.0. Per-installation preference. |
| `OpenTelemetry:FileExport:Metrics:Enabled` | bool | `true` | Exports metrics to `metrics-yyyy-MM-dd.log`. ON by default; set to `false` via user-secrets to disable. |
| `OpenTelemetry:FileExport:Metrics:ExportIntervalMs` | int | `15000` | Periodic export cycle in milliseconds. Per-installation preference. |
| `OpenTelemetry:FileExport:Logs:Enabled` | bool | `true` | Exports Serilog output to `app-yyyy-MM-dd-HHmmss.log`. ON by default; set to `false` via user-secrets to disable. |
| `OpenTelemetry:FileExport:Logs:FilePrefix` | string | `"app"` | Log file name prefix. Per-installation preference. |
| `OpenTelemetry:FileExport:Logs:MinimumLevel` | string | `"Information"` | Serilog minimum level: `Verbose`/`Debug`/`Information`/`Warning`/`Error`/`Fatal`. Per-installation preference. |

> **Exception to the "Default" column convention (line 140):** The three `Enabled` keys are absent from the shipped `appsettings.json`, so their effective defaults come from the C# type-level initialiser (`true`) rather than from `appsettings.json`. This is one of two documented exceptions to that convention — the other is `Loop`. A fresh `dotnet run` therefore writes traces, metrics, and Serilog output to `./logs` from first launch.

```json
"OpenTelemetry": {
  "ServiceName": "Agency.Harness.Console",
  "FileExport": {
    "OutputDirectory": "./logs"
  }
}
```

To disable individual telemetry signals per-installation via user-secrets (e.g. to turn off trace and log export):

```powershell
dotnet user-secrets set -p src\Harness\Agency.Harness.Console `
  "OpenTelemetry:FileExport:Traces:Enabled" "false"
dotnet user-secrets set -p src\Harness\Agency.Harness.Console `
  "OpenTelemetry:FileExport:Logs:Enabled" "false"
```

**Used by:** [Agency.Harness.Console](#agencyharnessconsole)

---

### Loop

Configures the Loop Kit goalkeeper — the independent done-check that runs after every agent turn when a goal is armed. `AddAgencyLoop` binds this section. The Console host additionally registers `GoalState` as a scoped DI service, wires `enable_goalkeeper`/`disable_goalkeeper` into the `ToolContext`, and constructs the `LoopRunner`/`Goalkeeper` per session in `ConsoleChatSession.CreateLoopRunner`.

| Key | Type | Default | Notes |
|---|---|---|---|
| `Loop:GoalkeeperClientName` | string? | `null` | Client name from `Agent:LLmClients`. Should differ from the worker client to reduce self-preference bias. |
| `Loop:GoalkeeperModel` | string? | `null` | Model id for the goalkeeper (cheap, fast model recommended). |
| `Loop:MaxTurns` | int | `12` | Default hard turn ceiling. Armed goals override this. |
| `Loop:Budget` | decimal? | `null` | Default USD spend ceiling. |
| `Loop:TokenBudget` | long? | `null` | Default total-token ceiling. |
| `Loop:WallClockSeconds` | int? | `null` | Default per-loop wall-clock timeout. |
| `Loop:GoalkeeperRubric` | string? | `null` | Extra strictness text appended to the goalkeeper system prompt. |

```json
"Loop": {
  "GoalkeeperClientName": "LocalVia-OpenAI-API",
  "GoalkeeperModel": "google/gemma-4-e2b",
  "MaxTurns": 12
}
```

**Used by:** [Agency.Harness.Console](#agencyharnessconsole)

---

### Logging

Standard .NET logging levels for the `ILogger` pipeline.

| Key | Default |
|---|---|
| `Logging:LogLevel:Default` | `"Information"` |
| `Logging:LogLevel:Microsoft` | `"Warning"` |
| `Logging:LogLevel:System` | `"Warning"` |

```json
"Logging": {
  "LogLevel": {
    "Default": "Information",
    "Microsoft": "Warning",
    "System": "Warning"
  }
}
```

**Used by:** All test projects.

---

### LlmTest

Test-only configuration for functional LLM tests in `Agency.Llm.Test`. API keys are always **secrets** stored in the `AgencySecrets` vault.

| Key | Type | Notes |
|---|---|---|
| `LlmTest:OpenAI:BaseUrl` | string | Endpoint for OpenAI-compatible tests. Default points at the local test proxy. |
| `LlmTest:OpenAI:Model` | string | Model id used in functional tests. |
| `LlmTest:OpenAI:ApiKey` | string | **Secret.** Set via `SetupLocal.ps1`. |
| `LlmTest:Claude:BaseUrl` | string | Endpoint for Claude API tests. |
| `LlmTest:Claude:Model` | string | Model id used in functional tests. |
| `LlmTest:Claude:ApiKey` | string | **Secret.** Set via `SetupLocal.ps1`. |

```json
"LlmTest": {
  "OpenAI": {
    "BaseUrl": "${TestProxy:OpenAI:BaseUrl}",
    "Model": "${TestProxy:Model}"
  },
  "Claude": {
    "BaseUrl": "${TestProxy:Claude:BaseUrl}",
    "Model": "${TestProxy:Model}"
  }
}
```

The `BaseUrl` and `Model` values reference [`shared-test-appsettings.json`](#shared-test-appsettingsjson); with the shipped file they resolve to the proxy endpoint `http://proxy.test:12345(/v1)` and model `google/gemma-4-e2b`.

To point the tests at a local LM Studio instance, override the `BaseUrl` keys in `appsettings.Development.json` (or `TestProxy:OpenAI:BaseUrl` in `shared-test-appsettings.json`) — they are machine-specific but not sensitive:

```json
{
  "LlmTest": {
    "OpenAI": { "BaseUrl": "http://<your-host>:1234/v1" },
    "Claude":  { "BaseUrl": "http://<your-host>:1234"   }
  }
}
```

To set the actual secrets:

```powershell
dotnet user-secrets set -p src\Llm\Agency.Llm.Test "LlmTest:OpenAI:ApiKey" "<key>"
dotnet user-secrets set -p src\Llm\Agency.Llm.Test "LlmTest:Claude:ApiKey" "<key>"
```

**Used by:** [Agency.Llm.Test](#agencyllmtest)

---

### AgentTest

Test-only configuration for functional agent tests in `Agency.Harness.Test`. Mirrors LlmTest but scoped to the harness test project.

| Key | Notes |
|---|---|
| `AgentTest:OpenAI:BaseUrl` | OpenAI-compatible endpoint. |
| `AgentTest:OpenAI:ApiKey` | API key. |
| `AgentTest:OpenAI:Model` | Model id. |
| `AgentTest:Claude:BaseUrl` | Claude API endpoint. |
| `AgentTest:Claude:ApiKey` | API key. |
| `AgentTest:Claude:Model` | Model id. |

```json
"AgentTest": {
  "OpenAI": {
    "BaseUrl": "${TestProxy:OpenAI:BaseUrl}",
    "ApiKey": "${TestProxy:ApiKey}",
    "Model": "${TestProxy:Model}"
  },
  "Claude": {
    "BaseUrl": "${TestProxy:Claude:BaseUrl}",
    "ApiKey": "${TestProxy:ApiKey}",
    "Model": "${TestProxy:Model}"
  }
}
```

All three values reference [`shared-test-appsettings.json`](#shared-test-appsettingsjson).

**Used by:** [Agency.Harness.Test](#agencyharnestest)

---

### MemoryFunctional

Test-only configuration for functional memory pipeline tests in `Agency.Memory.Functional.Test`.

| Key | Notes |
|---|---|
| `MemoryFunctional:LmStudio:BaseUrl` | LM Studio OpenAI-compatible endpoint. |
| `MemoryFunctional:LmStudio:ApiKey` | API key. |
| `MemoryFunctional:LmStudio:ChatModel` | Chat model id. |
| `MemoryFunctional:LmStudio:EmbeddingModel` | Embedding model id. |

```json
"MemoryFunctional": {
  "LmStudio": {
    "BaseUrl": "${TestProxy:OpenAI:BaseUrl}",
    "ApiKey": "${TestProxy:ApiKey}",
    "ChatModel": "local-model",
    "EmbeddingModel": "local-embedding-model"
  }
}
```

`BaseUrl` and `ApiKey` reference [`shared-test-appsettings.json`](#shared-test-appsettingsjson); `ChatModel` / `EmbeddingModel` stay literal because they differ from the other test projects. The project's `ConnectionStrings:PostgreSql` is no longer carried in its own `appsettings.json` — it is inherited from the runtime [`shared-appsettings.json`](#shared-appsettingsjson) (overridable via the `AgencySecrets` user-secret).

**Used by:** [Agency.Memory.Functional.Test](#agencymemoryfunctionaltest)

---

## User Secrets Vaults

Running `dotnet user-secrets` stores values outside the repository so they are never committed. The Agency solution uses two vaults.

### AgencySecrets (shared test vault)

Multiple test projects share one `UserSecretsId = "AgencySecrets"`. Secrets written to any of them land in the same file. Run the setup script once and all affected projects inherit the values.

Projects in this vault: `Agency.Sql.Postgres.Test`, `Agency.Llm.Test`, `Agency.VectorStore.Sql.Postgres.Test`, `Agency.KeyValueStore.Sql.Postgres.Test`, `Agency.Memory.Sql.Postgres.Test`, `Agency.Memory.Functional.Test`. The runtime `Agency.Harness.Console` also reads `ConnectionStrings:PostgreSql` from this vault — not via its `UserSecretsId` (which is [`agency-harness-console`](#agency-harness-console-harnessconsole-vault)) but through an explicit `AddUserSecrets("AgencySecrets")` in `Program.cs`, so the one canonical Postgres secret serves both the app and the tests.

| Secret key | Purpose |
|---|---|
| `ConnectionStrings:PostgreSql` | Production-quality Postgres connection string. |
| `LlmTest:OpenAI:ApiKey` | OpenAI API key for LLM functional tests. |
| `LlmTest:Claude:ApiKey` | Anthropic API key for LLM functional tests. |
| `GitHub:PersonalAccessToken` | GitHub PAT for the `github` MCP server in `Agency.Harness.Console`. **Optional** — see [Mcp](#mcp). |

### agency-harness-console (Harness.Console vault)

Used exclusively by `Agency.Harness.Console`. None of these values are sensitive credentials — they are per-installation preferences stored in user-secrets purely to keep them out of the committed `appsettings.json`.

The `SetupLocal.ps1` script prompts for all of the `OpenTelemetry` values interactively and writes them here. You can also set them directly:

```powershell
dotnet user-secrets set -p src\Harness\Agency.Harness.Console "OpenTelemetry:ServiceName" "MyInstallation"
dotnet user-secrets set -p src\Harness\Agency.Harness.Console "OpenTelemetry:FileExport:Traces:Enabled" "true"
```

| Key | Purpose |
|---|---|
| `OpenTelemetry:ServiceName` | Per-installation service name label in exported telemetry. |
| `OpenTelemetry:FileExport:Traces:Enabled` | Toggle trace file export. |
| `OpenTelemetry:FileExport:Traces:FilePrefix` | Trace log file name prefix. |
| `OpenTelemetry:FileExport:Traces:SamplingRatio` | Fraction of spans sampled (0.0–1.0). |
| `OpenTelemetry:FileExport:Metrics:Enabled` | Toggle metrics file export. |
| `OpenTelemetry:FileExport:Metrics:ExportIntervalMs` | Periodic export cycle in milliseconds. |
| `OpenTelemetry:FileExport:Logs:Enabled` | Toggle Serilog file export. |
| `OpenTelemetry:FileExport:Logs:FilePrefix` | Log file name prefix. |
| `OpenTelemetry:FileExport:Logs:MinimumLevel` | Serilog minimum level. |

> **`Agent:UserId`** is not set via this vault. It is auto-generated by `UserIdConfiguration` on first run and written directly back into `appsettings.json`, where it persists across restarts. It will appear in your local `appsettings.json` after the first launch but should not be committed.

---

## Projects

Each project's configuration keys are listed with a short description of why the project uses them.

---

### Agency.Harness.Console

The interactive REPL terminal. This is the only runtime executable in the solution and carries the most configuration.

`appsettings.json` → base defaults for a local LM Studio instance.
`appsettings.Test.json` → overrides for the functional-test environment (frozen clock, test proxy URL).

| Config | Why it is used |
|---|---|
| [Agent](#agent) | Defines available LLM clients, the active model, and loop behaviour. |
| [Embedding](#embedding) | When present, enables the vector store, ingestion pipeline, and `semantic_search` tool. |
| [ConnectionStrings](#connectionstrings) | Provides database addresses for both the memory store and the vector store. |
| [Memory](#memory) | Turns the background distiller/consolidator/hygiene services on or off. |
| [VectorStore](#vectorstore) | Chooses between SQLite and Postgres for the document store. |
| [Ingestion](#ingestion) | Sets chunk size and file pattern for `/add-file` and `/add-folder`. |
| [Retrieval](#retrieval) | Sets how many results `semantic_search` returns per query. |
| [Mcp](#mcp) | Declares external MCP servers whose tools are injected into the agent. |
| [Skills](#skills) | Points to skill directories and optionally disables shell execution. |
| [Permissions](#permissions) | Controls which tool calls are pre-approved, denied, or require user confirmation. |
| [Hooks](#hooks) | Wires operator-defined command or HTTP handlers to agent lifecycle events. |
| [OpenTelemetry](#opentelemetry) | Exports traces, metrics, and logs to rolling files under `./logs`. |
| [Loop](#loop) | Configures the goalkeeper client and default turn/budget caps for goal-driven loops. |
| [Logging](#logging) | Standard .NET log level thresholds. |

---

### Agency.Llm.Test

Functional tests for the Claude and OpenAI LLM client adapters. Tests run against a local LM Studio instance or real cloud APIs depending on the environment.

`appsettings.json` → points at the LM Studio test proxy.
`appsettings.CI.json` → empty overrides (CI uses the HTTP cache proxy; no live endpoint needed).
`appsettings.Development.json` → empty placeholder for local developer overrides.

| Config | Why it is used |
|---|---|
| [LlmTest](#llmtest) | Provides base URLs and models for OpenAI and Claude adapter tests. API keys come from the `AgencySecrets` user-secrets vault. |

---

### Agency.Embeddings.OpenAI.Test

Functional tests for the `EmbeddingGenerator` that calls an OpenAI-compatible embeddings endpoint.

`appsettings.json` → points at the LM Studio test proxy.
`appsettings.Development.json` → empty placeholder for local developer overrides.

| Config | Why it is used |
|---|---|
| [Embedding](#embedding) | Provides the endpoint, model, and API key for embedding generation tests. |

---

### Agency.Harness.Test

Functional tests for the `Agent` / `ChatSession` harness loop.

`appsettings.json` → test proxy endpoint and a fixed test model.

| Config | Why it is used |
|---|---|
| [AgentTest](#agenttest) | Provides LLM client endpoints and models for harness loop tests. |

---

### Agency.Memory.Functional.Test

End-to-end functional tests for the memory pipeline (distillation → consolidation → retrieval) against a real database and a local LM Studio instance.

`appsettings.json` → local Postgres connection and LM Studio endpoint.

| Config | Why it is used |
|---|---|
| [ConnectionStrings](#connectionstrings) | Points to the local dev Postgres instance for the memory schema. |
| [MemoryFunctional](#memoryfunctional) | Provides the LM Studio chat and embedding model endpoints for the pipeline tests. |
