# Agency.Llm.Common

#llm #abstractions #options #tools #models

## What It Is

Agency.Llm.Common is the shared contract library that defines provider-agnostic types for configuring LLM clients, describing available models, and declaring the tool-calling surface used across the agentic pipeline.

**Namespace:** `Agency.Llm.Common` · `Agency.Llm.Common.Tools`

## API Surface

### `IModelProvider`

```csharp
// File: src/Llm/Agency.Llm.Common/IModelProvider.cs
using Agency.Llm.Common;

public interface IModelProvider
{
    /// <summary>Returns all models available through this provider.</summary>
    Task<IReadOnlyList<Model>> GetModelsAsync(CancellationToken cancellationToken = default);
}
```

### `Model`

```csharp
// File: src/Llm/Agency.Llm.Common/Model.cs
using Agency.Llm.Common;

public sealed record Model(string Id, string Name);
```

### `LlmClientOptions`

```csharp
// File: src/Llm/Agency.Llm.Common/LlmClientOptions.cs
using Agency.Llm.Common;

public record class LlmClientOptions
{
    public string    Name             { get; set; } = string.Empty;
    public string    ClientType       { get; set; } = string.Empty;
    public string    ApiKey           { get; set; } = string.Empty;
    public string?   BaseUrl          { get; set; }
    public int?      MaxRetries       { get; set; }
    public TimeSpan? Timeout          { get; set; }
    public bool      SuppressThinking { get; set; } = false;
}
```

### Tool types (`Agency.Llm.Common.Tools`)

```csharp
// File: src/Llm/Agency.Llm.Common/Tools/ToolTypes.cs
using System.Text.Json;
using Agency.Llm.Common.Tools;

/// <summary>JSON-schema description of a tool exposed to the LLM.</summary>
public sealed record ToolDefinition(string Name, string Description, JsonElement InputSchema);

/// <summary>The result returned by a tool invocation.</summary>
public sealed record ToolResult(string Content, bool IsError = false);

/// <summary>A callable tool that can be registered with an <see cref="IToolRegistry"/>.</summary>
public interface ITool
{
    ToolDefinition Definition { get; }
    Task<ToolResult> InvokeAsync(JsonElement input, CancellationToken ct);
}

/// <summary>Catalogue of available tools; supports per-tool enable/disable by system or user.</summary>
public interface IToolRegistry
{
    IReadOnlyList<ToolDefinition> ListDefinitions();
    IReadOnlyList<(bool Enabled, ToolDefinition Definition)> ListAllDefinitions();

    void DisabledToolBySystem(string name);
    void EnableToolBySystem(string name);
    void DisableToolByUser(string name);
    void EnableToolByUser(string name);

    Task<ToolResult> InvokeAsync(string name, JsonElement input, CancellationToken ct);
}
```

## How It Works

`Agency.Llm.Common` is a pure type library — it contains no runtime logic. Provider implementations ([[Agency.Llm.Claude]] and [[Agency.Llm.OpenAI]]) reference this project and expose `LlmClientOptions` subclasses bound via `IOptions<T>`. The `IModelProvider` interface is also implemented by both providers and exposed via their registered services.

The `Tools` sub-namespace contains the tool-calling contract. [[Agency.Harness]] drives the agentic loop by calling `IToolRegistry.InvokeAsync` after the LLM returns a tool-use request, and passes `IToolRegistry.ListDefinitions()` to the provider on each turn.

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.Llm.Claude]] | Implements `IModelProvider`; consumes `LlmClientOptions`, `ToolDefinition`, `ToolResult` |
| [[Agency.Llm.OpenAI]] | Implements `IModelProvider`; consumes `LlmClientOptions`, `ToolDefinition`, `ToolResult` |
| [[Agency.Harness]] | Depends on `ITool`, `IToolRegistry`, `ToolDefinition`, `ToolResult`, `Model` |
| [[Agency.Harness.Console]] | References `LlmClientOptions` and `Model` for CLI configuration |

## Design Notes

- **No runtime dependencies** — the `.csproj` is intentionally empty; `Agency.Llm.Common` has zero NuGet package references, keeping the shared contract portable and fast to compile.
- **Dual enable/disable authority** — `IToolRegistry` distinguishes system-controlled disable (`DisabledToolBySystem` / `EnableToolBySystem`) from user-controlled disable (`DisableToolByUser` / `EnableToolByUser`), so a tool is only included in `ListDefinitions()` when both authorities agree it is enabled.
- **`LlmClientOptions` is provider-neutral** — the record covers fields common to any HTTP-based LLM API (key, base URL, retries, timeout); provider projects extend it with provider-specific fields rather than duplicating the base properties.
- **`SuppressThinking` is an escape hatch** — when `true`, provider clients inject `enable_thinking: false` and `thinking_budget_tokens: 0` into every request body, unconditionally suppressing extended thinking regardless of prompt-level directives. This is intended for reasoning-capable models (e.g. Qwen3) where thinking must be disabled at the infrastructure level.
