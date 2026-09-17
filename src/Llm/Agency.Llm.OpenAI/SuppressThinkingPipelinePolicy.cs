using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;

namespace Agency.Llm.OpenAI;

/// <summary>
/// Injects <c>enable_thinking</c> and/or <c>thinking_budget_tokens</c> into every
/// chat-completion request body, controlling extended thinking on reasoning-capable models
/// (e.g. Qwen3 MoE) regardless of prompt-level directives such as <c>/no_think</c>. Either
/// value may be <see langword="null"/>, in which case that field is left out of the request
/// body entirely (spec P3: unspecified stays absent, never a claim).
/// </summary>
internal sealed class SuppressThinkingPipelinePolicy(bool? enableThinking, int? thinkingBudgetTokens) : PipelinePolicy
{
    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Inject(message);
        ProcessNext(message, pipeline, currentIndex);
    }

    public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Inject(message);
        await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
    }

    private void Inject(PipelineMessage message)
    {
        if (message.Request.Content is null)
        {
            return;
        }

        using MemoryStream ms = new();
        message.Request.Content.WriteTo(ms, CancellationToken.None);

        using JsonDocument doc = JsonDocument.Parse(ms.ToArray());
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        using MemoryStream outMs = new();
        using (Utf8JsonWriter writer = new(outMs))
        {
            writer.WriteStartObject();
            foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
            {
                prop.WriteTo(writer);
            }

            if (enableThinking is { } enableThinkingValue)
            {
                writer.WriteBoolean("enable_thinking", enableThinkingValue);
            }

            if (thinkingBudgetTokens is { } thinkingBudgetTokensValue)
            {
                writer.WriteNumber("thinking_budget_tokens", thinkingBudgetTokensValue);
            }

            writer.WriteEndObject();
        }

        message.Request.Content = BinaryContent.Create(BinaryData.FromBytes(outMs.ToArray()));
    }
}
