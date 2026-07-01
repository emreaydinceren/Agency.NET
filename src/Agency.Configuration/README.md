# Agency.Configuration

Shared configuration layer and `${Section:Key}` placeholder resolution for the Agency AI Toolkit.

## Install

```
dotnet add package Agency.Configuration
```

## Types

- **`AgencyConfigurationBuilderExtensions.AddSharedConfiguration`** — inserts a shared `appsettings.json`-style file at the lowest precedence, so common values (host URLs, feature flags) can be defined once and still be overridden by the standard host sources.
- **`AgencyConfigurationBuilderExtensions.AddPlaceholderResolver`** — expands `${Section:Key}` tokens across the merged configuration, with cycle detection and a recursion depth guard.

## Usage

```csharp
var builder = Host.CreateApplicationBuilder(args);   // registers appsettings, env vars, secrets, CLI
builder.Configuration.AddSharedConfiguration();      // inserts shared-appsettings.json at the FRONT (lowest precedence)
builder.Configuration.AddPlaceholderResolver();      // LAST — expands ${Section:Key} tokens
```

Part of the [Agency AI Toolkit](https://github.com/emre/Agency) — an open-source .NET agentic AI toolkit.
