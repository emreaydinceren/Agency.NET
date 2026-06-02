# Build & Test

## Commands

```bash
# Build the full solution
dotnet build src/Agency.slnx

# Run all non-functional tests
dotnet test src/Agency.slnx --filter "Category!=Functional"

# Run a single test project
dotnet test src/Agency.Embeddings.Test/Agency.Embeddings.Test.csproj
dotnet test src/Agency.Llm.Test/Agency.Llm.Test.csproj

# Run functional tests (requires LM Studio running at http://llm-host.example:1234)
dotnet test src/Agency.Llm.Test --filter "Category=Functional"

# Start local infrastructure (PostgreSQL + pgvector)
cd src && docker-compose up -d
```

## Testing

- Always run the full test suite after changes: `dotnet test`
- For PostgreSQL tests, ensure connection strings are configured via user secrets — check `dotnet user-secrets list` before assuming config is correct.