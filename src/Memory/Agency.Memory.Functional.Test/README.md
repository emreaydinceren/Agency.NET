# Agency.Memory.Functional.Test

End-to-end functional tests for the Agency long-term memory pipeline.
All tests are tagged `[Trait("Category", "Functional")]` and are excluded from the default test run.

## Prerequisites

### Postgres (required for all tests)

Start the Docker Compose stack from the `src/` directory:

```bash
cd src && docker-compose up -d
```

This starts PostgreSQL 18 + pgvector on `localhost:5432`.

Default connection string (configured in `appsettings.json`):

```
Host=localhost;Port=5432;Database=dev_db;Username=dev_user;Password=dev_password
```

Override via user secrets (`AgencySecrets`) or environment variable
`CONNECTIONSTRINGS__POSTGRESQL`.

### LM Studio (required for G.1 only; others use stubs)

G.1 (`EndToEnd_FactWrittenInSessionN_RecalledInSessionNPlus1`) requires a running
LM Studio instance with:

- A chat completion model loaded (e.g., Llama-3.2-3B or equivalent)
- A text embedding model loaded (e.g., `text-embedding-3-small` compatible)
- The server accessible at `http://llm.test:1234` (default)

Configure the endpoint and model names in `appsettings.json` or user secrets:

```json
{
  "MemoryFunctional": {
    "LmStudio": {
      "BaseUrl": "http://llm.test:1234/v1",
      "ApiKey": "lm-studio",
      "ChatModel": "your-model-name",
      "EmbeddingModel": "your-embedding-model-name"
    }
  }
}
```

## Running Tests

```powershell
# Run all functional tests (requires Postgres + LM Studio):
dotnet test src/Memory/Agency.Memory.Functional.Test --filter "Category=Functional"

# Run a single scenario group (e.g. Capture & Recall):
dotnet test src/Memory/Agency.Memory.Functional.Test --filter "Category=Functional&Group=Capture"

# Run hygiene-group tests only:
dotnet test src/Memory/Agency.Memory.Functional.Test --filter "Category=Functional&Group=Hygiene"

# Skip latency-sensitive (Profile=Latency) tests that are flake-prone on slow LLM days:
dotnet test src/Memory/Agency.Memory.Functional.Test --filter "Category=Functional&Profile!=Latency"

# Run only Postgres-required tests (no LM Studio needed):
# G.2 (ForgetMe), G.3 (Latency), G.4 (Crash Recovery), G.5 (Consolidator)
dotnet test src/Memory/Agency.Memory.Functional.Test --filter "Category=Functional&FullyQualifiedName!~EndToEnd_FactWrittenInSessionN"

# Exclude ALL functional tests (CI default):
dotnet test src/Agency.slnx --filter "Category!=Functional"
```

> **CI note:** The functional suite is not part of the default CI run. A nightly job
> (tracked as a follow-up) executes it against the shared LM Studio host. Do not expect
> green-on-PR for E2E tests.

## Tests

| Task | Class | Requires |
|------|-------|----------|
| G.1 | `EndToEndRecallTests` | Postgres + LM Studio |
| G.2 | `EndToEndForgetMeTests` | Postgres only |
| G.3 | `EndToEndLatencyTests` | Postgres only |
| G.4 | `EndToEndCrashRecoveryTests` | Postgres only |
| G.5 | `EndToEndConsolidatorTests` | Postgres only |

## Skip Behavior

Each test checks infrastructure availability at runtime and calls `Skip.If(...)` if
Postgres or LM Studio is not reachable. The test will appear as **Skipped** in the
test runner output — not failed — so CI remains green even when infrastructure is absent.
