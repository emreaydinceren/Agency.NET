# Agency.Memory.Distiller

Distiller background service for the Agency long-term memory system: episode extraction, inactivity timer, and agent tools.

## Install

```
dotnet add package Agency.Memory.Distiller
```

## Types

- **`DistillerBackgroundService`** — hosted service that listens for distillation triggers (goal completion, inactivity, session disposed) and extracts episodic memories from conversation history.
- **`EpisodeExtractionPrompt`** — prompt builder for the LLM episode extraction pass.

## Usage

```csharp
services.AddMemoryDistiller(); // registers the distiller hosted service
```

Part of the [Agency AI Toolkit](https://github.com/emreaydinceren/Agency.NET) — an open-source .NET agentic AI toolkit.
