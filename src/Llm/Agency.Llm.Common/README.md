# Agency.Llm.Common

LLM provider abstractions for the Agency AI Toolkit — shared interfaces for listing and selecting models.

## Install

```
dotnet add package Agency.Llm.Common
```

## Types

- **`IModelProvider`** — `GetModelsAsync()` returns available models from the configured provider.
- **`Model`** — record with `Id` and `Name`.

## Usage

```csharp
// Register a concrete provider (e.g. Agency.Llm.Claude or Agency.Llm.OpenAI)
services.AddSingleton<IModelProvider, ClaudeClient>();

// List available models
var models = await modelProvider.GetModelsAsync();
```

Part of the [Agency AI Toolkit](https://github.com/emre/Agency) — an open-source .NET agentic AI toolkit.
