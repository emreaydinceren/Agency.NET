# Agency.Configuration

#configuration #appsettings #placeholder #shared-config #infrastructure

## What It Is

`Agency.Configuration` is the shared configuration infrastructure for the Agency AI Toolkit. It provides two `IConfigurationBuilder` extension methods that:

1. **merge a solution-level `shared-appsettings.json`** into the normal configuration tree, and
2. **expand `${Section:Key}` placeholder tokens** in every configuration value at startup, resolving them against the fully-merged configuration.

Together these let a value that is used in many places — or across many projects — be written **once** and referenced everywhere by name.

**Namespace:** Extension methods live in `Microsoft.Extensions.Configuration` (so they surface on `builder.Configuration` without an extra `using`); internal implementation types live in `Agency.Configuration`.

## Problems It Solves

Before this library, `appsettings.json` carried the same literal value in several places, and every test project kept its own copy:

- The LM Studio host `http://llm.test:1234` appeared **three times in one file** — `Agent:LLmClients[0]:BaseUrl` (with `/v1`), `Agent:LLmClients[1]:BaseUrl`, and `Embedding:BaseUrl` — and again across the **six** `appsettings.json` files in the solution.
- Changing the endpoint meant a find-and-replace across many files, with the literal `/v1` suffix making naïve replacement error-prone.
- .NET's configuration system natively **layers** sources (later sources override earlier ones), which solves cross-file *override*, but it has **no native interpolation** — you cannot splice one value into the middle of another (`${host}/v1`). That gap is exactly what this library fills.

What it deliberately does **not** do: it is not a general templating engine, a secrets manager, or a reload-on-change mechanism. It is the smallest piece that removes the duplication. Shared values live under dedicated top-level keys (e.g. `LLmClients:OpenAI:BaseUrl`) that the app's own sections reference by placeholder — keeping the one canonical copy out of any single consuming section. A placeholder can equally point at a key that arrives from **user secrets** rather than the shared file: `ConnectionStrings:PostgreSql` is supplied by the `AgencySecrets` user-secrets vault (see below), and `${ConnectionStrings:PostgreSql}` resolves against it exactly the same way — the resolver does not care which source a key came from, only that it is present in the merged configuration.

## API Surface

```csharp
using Microsoft.Extensions.Configuration;

// Merge the shared JSON file (provides canonical keys such as LLmClients:OpenAI:BaseUrl).
// Inserts the source at the FRONT of the builder (lowest precedence) so host sources override it.
IConfigurationBuilder AddSharedConfiguration(
    this IConfigurationBuilder builder,
    string path = "shared-appsettings.json",
    bool optional = true);

// Expand ${Section:Key} tokens in every merged value.
// Call LAST — after all sources are registered and before any reads.
IConfigurationBuilder AddPlaceholderResolver(this IConfigurationBuilder builder);
```

Wiring (from `Agency.Harness.Console/Program.cs`):

```csharp
var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddSharedConfiguration();   // insert shared-appsettings.json at the front (lowest precedence)
builder.Configuration.AddPlaceholderResolver();   // LAST — expand ${…} tokens
```

`shared-appsettings.json` (solution root, linked into each consumer's output):

```json
{
  "LLmClients": {
    "OpenAI": { "BaseUrl": "http://llm.test:1234/v1" },
    "Claude": { "BaseUrl": "http://llm.test:1234" },
    "ApiKey": "lm-studio"
  }
}
```

The Postgres connection string is **not** in this file — it is a secret, supplied by the `AgencySecrets` user-secrets vault as `ConnectionStrings:PostgreSql`. The Console loads that vault explicitly (`Program.cs`) so the placeholder below resolves in every environment, not just Development.

`appsettings.json` then references both the shared file and the secret:

```json
"Agent": {
  "LLmClients": [
    { "BaseUrl": "${LLmClients:OpenAI:BaseUrl}", "ApiKey": "${LLmClients:ApiKey}" },
    { "BaseUrl": "${LLmClients:Claude:BaseUrl}", "ApiKey": "${LLmClients:ApiKey}" }
  ]
},
"Embedding": { "BaseUrl": "${LLmClients:OpenAI:BaseUrl}", "ApiKey": "${LLmClients:ApiKey}" },
"ConnectionStrings": { "VectorStorePostgreSql": "${ConnectionStrings:PostgreSql}" }
```

## How It Works

### Placeholder syntax

- **Delimiters / separator:** `${Section:Key}` — the `:` path separator is the standard `IConfiguration` key separator, so any config path is addressable (e.g. `${Agent:LLmClients:0:Name}`).
- **Colon required (bare tokens pass through):** only tokens containing a `:` are treated as configuration references. A bare token such as `${RepoRoot}` or `${Configuration}` is left **verbatim** — it belongs to another substitution system (`McpConfigResolver` expands those later against runtime paths). This lets both systems share the `${…}` syntax without the resolver claiming or failing on the MCP tokens. (Consequence: a single-segment, top-level config key is not addressable by placeholder; reference it under a section.)
- **Embedded & multiple tokens:** a token can sit inside a larger string and a value can contain several — `${LLmClients:Claude:BaseUrl}/v1`, `${A:X}-${B:Y}`.
- **Chained references:** if a resolved value itself contains tokens, they are expanded recursively.
- **Escape:** `$$` produces a literal `$`, so `$${X}` yields the literal text `${X}` with no lookup.
- **Case-insensitive:** keys are matched case-insensitively, matching `IConfiguration` semantics.

### Resolution pipeline

The resolver runs at the **`IConfiguration` layer**, not at options post-configuration. This matters because the host reads configuration both ways — strongly-typed (`AddOptions<AgentOptions>().BindConfiguration("Agent")`) **and** raw (`builder.Configuration["Agent:DefaultClientName"]`, `GetSection("Agent:LLmClients").Get<LlmClientOptions[]>()`). Only a provider-layer expansion covers both.

`AddPlaceholderResolver` uses a **snapshot strategy** (`AgencyConfigurationBuilderExtensions.cs`):

1. Obtain the merged configuration root. If `builder` already implements `IConfigurationRoot` (the host's `ConfigurationManager` does), cast directly; otherwise call `Build()`.
2. `root.AsEnumerable()` flattens every key/value pair; null-valued section/parent keys are filtered out.
3. The pairs are materialised **immediately** into an `OrdinalIgnoreCase` dictionary — the *seed* snapshot.
4. A `PlaceholderResolverSource` carrying that seed is appended as the **last** source.

At build time `PlaceholderResolverProvider.Load()` walks the seed and, for each entry, sets `Data[key] = PlaceholderExpander.Expand(value, key, seed)`. Because the provider is last, its expanded values win for every subsequent read. The expander resolves **only against the captured seed**, never the live root.

```
sources: shared file → appsettings.json → appsettings.{Env}.json → user-secrets → env vars → CLI
                                   │
                  AddPlaceholderResolver() snapshots the merge ▼
        seed (OrdinalIgnoreCase) ──► PlaceholderResolverProvider.Load()
                                   ▼  expands every value via PlaceholderExpander
        expanded source appended LAST  ──►  highest precedence on read
```

### Why the snapshot strategy (the `ConfigurationManager` trap)

The common "wrap-and-clear" placeholder pattern (`var tmp = builder.Build(); builder.Sources.Clear(); builder.Add(wrapper)`) **self-destructs** against the host's `ConfigurationManager`, whose `IConfigurationBuilder.Build()` returns `this`. Clearing its sources would empty the very root being wrapped, leaving an empty or self-referential configuration. Snapshotting sidesteps this entirely and is deterministic and unit-testable. A regression test (`PlaceholderResolverTests`) guards this behaviour over a real `ConfigurationManager`.

### Precedence and overrides

`AddSharedConfiguration` inserts the shared file at the **front** of the source list (lowest precedence), so the standard host sources layered on top of it — `appsettings.json`, env vars, CLI — override any shared default. Because the resolver snapshot is taken *after* all of those, an environment-variable override such as `LLmClients__OpenAI__BaseUrl=http://other-host:1234/v1` is already in the seed and therefore flows through every `${LLmClients:OpenAI:BaseUrl}` token automatically. This preserves normal .NET override precedence.

### Bare tokens and the MCP token system (colon rule)

Only tokens that contain the `:` path separator — i.e. `${Section:Key}` — are treated as configuration references. A **bare** token such as `${RepoRoot}` or `${Configuration}` is left **verbatim**: it is neither resolved nor treated as a missing key.

This exists so the resolver can coexist with `McpConfigResolver`, which uses the same `${…}` delimiters for its own runtime-path tokens (`${RepoRoot}` → the repo's `.git` ancestor, `${Configuration}` → `Debug`/`Release`). Those live in `appsettings.json` under `Mcp:Servers[].Arguments` and are expanded *later*, after the host is built, by `McpConfigResolver.Expand`. Because the placeholder resolver runs over every value at config-build time, without this rule it would fail-fast on `${RepoRoot}` before MCP ever ran. The colon rule partitions the shared syntax cleanly: config references are always sectioned (`Section:Key`); MCP tokens are always bare.

Consequence: a single-segment, top-level configuration key is not addressable by placeholder — reference values under a section. (To emit a literal `${X}` regardless, use the `$$` escape: `$${X}`.)

### Error behaviour (fail-fast)

`PlaceholderExpander` throws `InvalidOperationException` at startup for:

| Condition | Message shape |
|---|---|
| Missing key (colon-bearing token only) | `Configuration placeholder "${LLmClients:OpenAI:BaseUrl}" referenced by key "Embedding:BaseUrl" could not be resolved.` |
| Cycle (direct or indirect) | `Configuration placeholder cycle detected: A -> B -> A` |
| Depth > 32 | `…exceeded the maximum depth of 32 while resolving key "…".` |

Failing fast at composition time is consistent with the repo's `?? throw` / `ValidateOnStart` style — a bad reference breaks the build/run immediately rather than surfacing as a confusing runtime error later.

## Internal Types

| Type | Responsibility |
|---|---|
| `PlaceholderExpander` | Pure, stateless `${…}` expansion: regex scan, `$$` escape, colon rule (only `Section:Key` tokens resolve; bare tokens pass through), recursive chaining, cycle detection (resolution-path list), depth guard. No `IConfiguration` dependency — independently unit-testable. |
| `PlaceholderResolverSource` | `IConfigurationSource` holding the seed snapshot; builds the provider. |
| `PlaceholderResolverProvider` | `ConfigurationProvider` whose `Load()` expands the seed into `Data`. |

All three are `internal` and exposed to `Agency.Configuration.Test` via `InternalsVisibleTo`.

## Physical Sharing

`shared-appsettings.json` lives once at the **solution root** (`src/`) and is **linked** into each consuming project (no per-project copies):

```xml
<Content Include="..\..\shared-appsettings.json" Link="shared-appsettings.json" Pack="false">
  <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
</Content>
```

`Link` flattens it next to `appsettings.json` in the output, so `AddSharedConfiguration("shared-appsettings.json")` resolves relative to the content root. `Pack="false"` keeps host config out of NuGet packages (the repo sets `IsPackable=true` by default).

A second test-only file, `shared-test-appsettings.json`, is linked the same way into each functional-test project (and into the Console for the Test environment). It holds the `TestProxy` keys — the offline cache-proxy endpoint, test API key, and test model — so test values are shared once without mixing into the runtime `shared-appsettings.json`. See the [Configuration Manual](../Configuration%20Manual.md#shared-test-appsettingsjson).

## Dependencies

| Package | Purpose |
|---|---|
| `Microsoft.Extensions.Configuration` | `IConfigurationBuilder`, `IConfigurationSource`, `ConfigurationProvider`, `AsEnumerable` |
| `Microsoft.Extensions.Configuration.Json` | `AddJsonFile` used by `AddSharedConfiguration` |

No project references.

## Related

- [Configuration Manual](../Configuration%20Manual.md) — full documentation of placeholder syntax, `shared-appsettings.json`, `shared-test-appsettings.json`, wiring order, env-var overrides, and error behaviour.
- [[Agency.Harness.Console]] — the runtime consumer; calls both extension methods in `Program.cs` (and loads `shared-test-appsettings.json` under `DOTNET_ENVIRONMENT=Test`).
- Functional-test consumers — `Agency.Llm.Test`, `Agency.Harness.Test`, `Agency.Embeddings.OpenAI.Test`, and `Agency.Memory.Functional.Test` call the same extensions in their config builders and reference the test-only `shared-test-appsettings.json` (`${TestProxy:…}` tokens).
- [[Home]]
