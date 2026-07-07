# Build & Test

## Commands

```bash
# Build the full solution
dotnet build src/Agency.slnx

# Run all non-functional tests
dotnet test src/Agency.slnx --filter "Category!=Functional"

# Run functional tests via helper script
./src/scripts/Run-FunctionalTests.ps1

# Run functional tests manually (proxy at runner-host.example:12345 must be running)
dotnet test src/Agency.slnx --filter "Category=Functional" -- RunConfiguration.MaxCpuCount=1

# Start local infrastructure (PostgreSQL + pgvector)
cd src && docker-compose up -d
```

## Testing

- Always run the full test suite after changes: `dotnet test src/Agency.slnx --filter "Category!=Functional"`
- Functional tests require the HTTP cache proxy running at `http://runner-host.example:12345` (from the Agency.HttpCacheProxy repo) and LM Studio at `http://llm.test:1234`.
- For PostgreSQL tests, ensure connection strings are configured via user secrets — check `dotnet user-secrets list` before assuming config is correct.
