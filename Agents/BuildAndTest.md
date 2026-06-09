# Build & Test

## Commands

```bash
# Build the full solution
dotnet build src/Agency.slnx

# Run all non-functional tests
dotnet test src/Agency.slnx --filter "Category!=Functional"

# Run functional tests via helper script (starts/stops the cache proxy automatically)
./src/scripts/Run-FunctionalTests.ps1

# Run functional tests manually (proxy must already be running — see below)
dotnet test src/Agency.slnx --filter "Category=Functional" -- RunConfiguration.MaxCpuCount=1

# Start local infrastructure (PostgreSQL + pgvector)
cd src && docker-compose up -d
```

## HTTP Cache Proxy

Functional tests route LLM requests through `Agency.Utils.HttpCacheProxy` at `http://localhost:12345`, which caches responses and forwards misses to `http://llm-host.example:1234` (LM Studio). This avoids redundant upstream calls and makes repeated test runs faster.

```bash
# Start the proxy manually (from the repo root)
dotnet run --project src/Utils/Agency.Utils.HttpCacheProxy --configuration Release
```

The proxy prints `[HIT]`/`[MISS]` for every request to stdout. Configuration is in `src/Utils/Agency.Utils.HttpCacheProxy/appsettings.json` (TTL: 10 min, traces/metrics off, logs to `./logs`).

`Run-FunctionalTests.ps1` starts and stops the proxy automatically. CI workflows do the same via the `🚀 Start HTTP Cache Proxy` step.

## Testing

- Always run the full test suite after changes: `dotnet test src/Agency.slnx --filter "Category!=Functional"`
- Functional tests require LM Studio running at `http://llm-host.example:1234` and the cache proxy running at `http://localhost:12345`.
- For PostgreSQL tests, ensure connection strings are configured via user secrets — check `dotnet user-secrets list` before assuming config is correct.
