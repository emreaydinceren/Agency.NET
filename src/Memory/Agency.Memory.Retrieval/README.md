# Agency.Memory.Retrieval

Retrieval engine for the Agency long-term memory system. Implements gated vector search, composite ranking, and context injection for the agent hot path.

## Install

```
dotnet add package AgencyDotNet.Memory.Retrieval
```

## Types

- **`MemoryRetriever`** — runs a semantic similarity query against `IMemoryStore`, applies the composite ranking formula (recency × importance × session-match), and injects the top-K records into the agent's system prompt.

## Usage

```csharp
services.AddMemoryRetrieval(); // registers MemoryRetriever for use by MemoryHookFactory
```

Part of the [Agency AI Toolkit](https://github.com/emreaydinceren/Agency.NET) — an open-source .NET agentic AI toolkit.
