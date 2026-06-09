# Agency.Utils.HttpCacheProxy

A lightweight HTTP caching reverse proxy used to speed up functional tests that call LLM endpoints. Instead of hitting the upstream model server on every test run, the proxy stores responses in memory and replays them on subsequent runs with the same request.

## How it works

Every inbound request is keyed on `Method + PathAndQuery + SHA256(body)`. On a cache **miss**, the request is forwarded to the configured upstream and the response is stored. On a **hit**, the stored response is returned immediately without touching the upstream. Every request prints `[HIT]` or `[MISS]` to stdout along with the HTTP method, path, status code, and elapsed time.

```
[MISS] POST /v1/chat/completions → 200 (3421ms)
[HIT ] POST /v1/chat/completions → 200 (0ms)
```

The cache is in-process (`ConcurrentDictionary`) and lives for the lifetime of the process. TTL is checked on read — expired entries are evicted lazily.

### Persistent filesystem cache (optional)

An optional second tier persists each cached response as a JSON blob in a checked-in directory, so responses survive across runs, machines, and CI. When `FileCache` is enabled the proxy eager-loads every blob at startup (logging `Loaded N cached responses`) and writes each new entry through to disk; **persisted entries never expire** (infinite TTL). This is what lets the functional-test CI step run offline — the checked-in `cache/` blobs serve every LLM/embedding request without an upstream. See [`cache/README.md`](cache/README.md) for how the cache is generated, why misses happen, and how to regenerate it.

```json
"Proxy": {
  "FileCache": { "Enabled": true, "Directory": "cache" }
}
```

| Field | Description |
|---|---|
| `FileCache.Enabled` | Turn the persistent tier on (default `false` in code; `true` in `appsettings.json`) |
| `FileCache.Directory` | Blob directory; relative paths resolve against the content root (the project directory under `dotnet run`) |

## Configuration

`appsettings.json` defines one or more named routes:

```json
{
  "Proxy": {
    "Routes": [
      {
        "Name": "lmstudio",
        "LocalPort": 12345,
        "PathPrefix": "/",
        "TargetUrl": "http://llm-host.example:1234",
        "Cache": { "Enabled": true, "TtlSeconds": 600 },
        "LogRequestBody": false,
        "LogResponseBody": false,
        "TimeoutSeconds": 60
      }
    ]
  }
}
```

| Field | Description |
|---|---|
| `LocalPort` | Port Kestrel listens on for this route |
| `PathPrefix` | Only requests whose path starts with this prefix are matched; longer prefixes take priority |
| `TargetUrl` | Upstream base URL; requests are forwarded with their original path and query string appended |
| `Cache.TtlSeconds` | `0` means entries never expire; default is `600` (10 min) |
| `LogRequestBody` / `LogResponseBody` | Emit the first 1 KB of request/response bodies to the log (debug aid; off by default) |
| `TimeoutSeconds` | Per-request upstream timeout; default `60` |

Multiple routes on different ports can be defined in the same process.

## Running

**As an installed tool** (recommended for use outside the repo):

```powershell
dotnet tool install -g Agency.Utils.HttpCacheProxy
agency-cache-proxy
```

**From source** (used by CI and `Run-FunctionalTests.ps1`):

```powershell
# Release build must exist (dotnet build --configuration Release)
dotnet run --project src/Utils/Agency.Utils.HttpCacheProxy --configuration Release
```

The proxy is ready when Kestrel prints its "Now listening on" line (or when port 12345 accepts a TCP connection, which is what the readiness loops in CI and the helper script check).

## Integration with functional tests

**Helper script** — starts the proxy, runs the tests, then kills the proxy:

```powershell
./src/scripts/Run-FunctionalTests.ps1
```

Pass extra `dotnet test` arguments after the script name:

```powershell
./src/scripts/Run-FunctionalTests.ps1 --logger trx
```

**CI workflows** — both `ci-main.yaml` and `ci-pr.yaml` have a `🚀 Start HTTP Cache Proxy` step that launches the proxy in the background before the `🧪 Run Functional Tests` step, then captures the proxy log as a job artifact via `📋 HTTP Cache Proxy log`.

**Test configuration** — functional tests point their LLM client base URL at `http://localhost:12345`. Because the cache key covers the full request body, deterministic prompts in tests always produce cache hits after the first run.

## Telemetry

OpenTelemetry traces, metrics, and structured logs are wired up but file export for traces and metrics is **off by default**. Log files are written to `./logs/` (relative to the working directory) with a `proxy-` prefix. To enable trace/metric export, set `OpenTelemetry.FileExport.Traces.Enabled` and `OpenTelemetry.FileExport.Metrics.Enabled` to `true` in `appsettings.json` or via environment variable overrides.
