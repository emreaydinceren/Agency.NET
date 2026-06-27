# Configuration Manual

This document is the single reference for every configuration key in the Agency solution — where it lives, what it does, and which projects consume it.

Run `src\SetupLocal.ps1` from the repo root to be prompted for secrets interactively. The script writes them into the correct `dotnet user-secrets` vaults automatically.

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
| `Agent:LLmClients[].ApiKey` | string | required | API key; use `"lm-studio"` for LM Studio which ignores the value. |
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
      "BaseUrl": "http://llm-host.example:1234/v1",
      "ApiKey": "lm-studio",
      "Timeout": "00:10:00",
      "SuppressThinking": true
    },
    {
      "Name": "LocalVia-Claude-API",
      "ClientType": "Claude",
      "BaseUrl": "http://llm-host.example:1234",
      "ApiKey": "lm-studio"
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
  "BaseUrl": "http://llm-host.example:1234/v1",
  "ApiKey": "lm-studio",
  "ModelId": "local-embedding-model",
  "Dimensions": 1024
}
```

**Used by:** [Agency.Harness.Console](#agencyharnessconsole), [Agency.Embeddings.OpenAI.Test](#agencyembeddingsopenaitest)

---

### ConnectionStrings

Database connection strings for memory storage and the vector store. These are kept in `appsettings.json` for local defaults and overridden via **user secrets** (vault `AgencySecrets`) in CI or production.

| Key | Default | Purpose |
|---|---|---|
| `ConnectionStrings:PostgreSql` | `Host=localhost;Port=5432;Database=dev_db;Username=dev_user;Password=dev_password` | Memory store (Postgres provider) and pgvector search. |
| `ConnectionStrings:Sqlite` | `Data Source=agency-memory.db` | Memory store (SQLite provider). |
| `ConnectionStrings:VectorStoreSqlite` | `Data Source=agency-vectorstore.db` | Vector store (SQLite provider). |
| `ConnectionStrings:VectorStorePostgreSql` | `Host=localhost;Port=5432;Database=dev_db;Username=dev_user;Password=dev_password` | Vector store (Postgres provider). |

```json
"ConnectionStrings": {
  "PostgreSql": "Host=localhost;Port=5432;Database=dev_db;Username=dev_user;Password=dev_password",
  "Sqlite": "Data Source=agency-memory.db",
  "VectorStoreSqlite": "Data Source=agency-vectorstore.db",
  "VectorStorePostgreSql": "Host=localhost;Port=5432;Database=dev_db;Username=dev_user;Password=dev_password"
}
```

> **Note:** `PostgreSql` and `VectorStorePostgreSql` share the same value in the dev defaults — they intentionally point at the same local Postgres instance and must be updated together; in production they may be set to different servers to split memory and vector storage.

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

Path values support two portability tokens:
- `${RepoRoot}` — resolved to the nearest ancestor directory containing `.git`.
- `${Configuration}` — resolved to `Debug` or `Release` from the output path.

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

> **Exception to the "Default" column convention (line 13):** The three `Enabled` keys are absent from the shipped `appsettings.json`, so their effective defaults come from the C# type-level initialiser (`true`) rather than from `appsettings.json`. This is one of two documented exceptions to that convention — the other is `Loop`. A fresh `dotnet run` therefore writes traces, metrics, and Serilog output to `./logs` from first launch.

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
    "BaseUrl": "http://llm-host.example:1234/v1",
    "Model": "google/gemma-4-e2b"
  },
  "Claude": {
    "BaseUrl": "http://llm-host.example:1234",
    "Model": "google/gemma-4-e2b"
  }
}
```

To point the tests at a local LM Studio instance, override the `BaseUrl` keys in `appsettings.Development.json` — they are machine-specific but not sensitive:

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
    "BaseUrl": "http://llm-host.example:1234/v1",
    "ApiKey": "lm-studio",
    "Model": "google/gemma-4-e2b"
  },
  "Claude": {
    "BaseUrl": "http://llm-host.example:1234",
    "ApiKey": "lm-studio",
    "Model": "google/gemma-4-e2b"
  }
}
```

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
    "BaseUrl": "http://llm-host.example:1234/v1",
    "ApiKey": "lm-studio",
    "ChatModel": "local-model",
    "EmbeddingModel": "local-embedding-model"
  }
}
```

**Used by:** [Agency.Memory.Functional.Test](#agencymemoryfunctionaltest)

---

## User Secrets Vaults

Running `dotnet user-secrets` stores values outside the repository so they are never committed. The Agency solution uses two vaults.

### AgencySecrets (shared test vault)

Multiple test projects share one `UserSecretsId = "AgencySecrets"`. Secrets written to any of them land in the same file. Run the setup script once and all affected projects inherit the values.

Projects in this vault: `Agency.Sql.Postgres.Test`, `Agency.Llm.Test`, `Agency.VectorStore.Sql.Postgres.Test`, `Agency.KeyValueStore.Sql.Postgres.Test`, `Agency.Memory.Sql.Postgres.Test`, `Agency.Memory.Functional.Test`.

| Secret key | Purpose |
|---|---|
| `ConnectionStrings:PostgreSql` | Production-quality Postgres connection string. |
| `LlmTest:OpenAI:ApiKey` | OpenAI API key for LLM functional tests. |
| `LlmTest:Claude:ApiKey` | Anthropic API key for LLM functional tests. |

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
