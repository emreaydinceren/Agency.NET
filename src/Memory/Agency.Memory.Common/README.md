# Agency.Memory.Common

Common types, interfaces, and options for the Agency long-term memory system.

## Install

```
dotnet add package Agency.Memory.Common
```

## Types

- **`IMemoryStore`** — core interface for storing and retrieving memory records.
- **`MemoryRecord`** — a single stored memory item with content, importance, and metadata.
- **`MemoryOptions`** — configuration for retention, importance thresholds, and ranking weights.
- **`RankingFormula`** — composite scoring of recency, importance, and session-match signals.

## Usage

```csharp
services.AddOptions<MemoryOptions>()
    .BindConfiguration("Memory");

// Implement IMemoryStore or use a provider package such as Agency.Memory.Sql.Postgres.
```

Part of the [Agency AI Toolkit](https://github.com/emreaydinceren/Agency.NET) — an open-source .NET agentic AI toolkit.
