# Agency.Llm.Claude

Anthropic Claude LLM provider for the Agency AI Toolkit.

## Install

```
dotnet add package Agency.Llm.Claude
```

## Configuration

```json
{
  "Claude": {
    "ApiKey": "sk-ant-...",
    "Model": "claude-sonnet-4-6"
  }
}
```

## Usage

```csharp
services.Configure<ClaudeOptions>(config.GetSection("Claude"));
services.AddSingleton<IModelProvider, ClaudeClient>();
services.AddSingleton(sp =>
    sp.GetRequiredService<ClaudeClient>().CreateChatClient());
```

Part of the [Agency AI Toolkit](https://github.com/emre/Agency) — an open-source .NET agentic AI toolkit.
