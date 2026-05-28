# Agency.Memory.Hygiene

Hygiene sweeper background service for the Agency long-term memory system: TTL pruning and importance-based pruning.

## Install

```
dotnet add package Agency.Memory.Hygiene
```

## Types

- **`MemoryHygieneService`** — hosted background service that periodically deletes expired records (TTL) and low-importance records below the configured threshold.

## Usage

```csharp
services.AddMemoryHygiene(); // registers the hosted hygiene sweeper
```

Part of the [Agency AI Toolkit](https://github.com/emreaydinceren/Agency.NET) — an open-source .NET agentic AI toolkit.
