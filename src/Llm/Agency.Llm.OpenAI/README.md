# Agency.Llm.OpenAI

OpenAI-compatible LLM provider for the Agency AI Toolkit. Works with OpenAI, Azure OpenAI, LM Studio, and any OpenAI-compatible endpoint.

## Install

```
dotnet add package AgencyDotNet.Llm.OpenAI
```

## Configuration

```json
{
  "OpenAI": {
    "BaseUrl": "https://api.openai.com/v1",
    "ApiKey": "sk-...",
    "Model": "gpt-4o"
  }
}
```

## Usage

```csharp
services.Configure<OpenAIOptions>(config.GetSection("OpenAI"));
services.AddSingleton<IModelProvider, OpenAIClient>();
services.AddSingleton(sp =>
    sp.GetRequiredService<OpenAIClient>().CreateChatClient());
```

Part of the [Agency AI Toolkit](https://github.com/emreaydinceren/Agency.NET) — an open-source .NET agentic AI toolkit.
