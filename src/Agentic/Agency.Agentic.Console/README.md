# Agency.Agentic.Console

Interactive console host for the Agency AI Toolkit — a ready-to-run REPL for conversing with an `Agent`.

## Install

```
dotnet add package Agency.Agentic.Console
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
        services.AddScoped<IAgentFactory, AgentFactory>();
        services.AddTelemetry();
        services.AddConsoleServices();
    })
    .RunConsoleAsync();
```

Part of the [Agency AI Toolkit](https://github.com/emre/Agency) — an open-source .NET agentic AI toolkit.
