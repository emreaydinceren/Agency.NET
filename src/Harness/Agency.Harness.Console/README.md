# Agency.Harness.Console

Interactive console host for the Agency AI Toolkit — a ready-to-run REPL for conversing with an `Agent`.

## Run the demo in 2 minutes

The fastest way to try the demo: from the `src` directory, run

```powershell
.\RunConsole.ps1
```

This is a friendly interactive script that asks a few questions — your LLM's base URL, model, and API key — then builds and launches the console REPL. Press Enter to accept sensible defaults.

Prerequisites:

- .NET 10 SDK
- An OpenAI-compatible LLM endpoint — e.g. a local [LM Studio](https://lmstudio.ai/) (`http://localhost:1234/v1`) or [Ollama](https://ollama.com/) (`http://localhost:11434/v1`), neither of which need an API key, or any cloud OpenAI-compatible endpoint with a key.

The demo can optionally expose GitHub tools to the agent via the official GitHub MCP server (needs Docker + a GitHub Personal Access Token). This is optional — if Docker or the token isn't present, the console still runs fine, just without those tools.

## Install

```
dotnet add package AgencyDotNet.Harness.Console
```

## Features

- Multi-turn REPL with Ctrl+C graceful interruption
- Per-response throughput and token stats
- Configurable LLM provider (Claude or OpenAI-compatible) via `appsettings.json`
- Welcome banner with provider and model information

## Usage

```csharp
// Typical entry point — wire up via HostBuilder
await Host.CreateDefaultBuilder(args)
    .ConfigureServices((ctx, services) =>
    {
        services.AddAgencyAgent();
        services.AddTelemetry();
        services.AddConsoleServices();
    })
    .RunConsoleAsync();
```

Part of the [Agency AI Toolkit](https://github.com/emreaydinceren/Agency.NET) — an open-source .NET agentic AI toolkit.
