# Agency.Memory.Consolidator

Consolidator sub-agent background service for the Agency long-term memory system: merges, updates, and deletes memory records after distillation.

## Install

```
dotnet add package Agency.Memory.Consolidator
```

## Types

- **`ConsolidatorBackgroundService`** — hosted service that dequeues consolidation jobs and runs a reconciliation sub-agent to merge, update, or delete memory records.
- **`ConsolidatorTools`** — LLM-facing tools (`memory_merge`, `memory_update`, `memory_delete`, `memory_done`) used by the consolidation agent.

## Usage

```csharp
services.AddMemoryConsolidator(); // registers the consolidator hosted service
```

Part of the [Agency AI Toolkit](https://github.com/emreaydinceren/Agency.NET) — an open-source .NET agentic AI toolkit.
