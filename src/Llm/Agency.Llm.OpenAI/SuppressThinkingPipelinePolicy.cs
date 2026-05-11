using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;

namespace Agency.Llm.OpenAI;

/// <summary>
/// Injects <c>enable_thinking: false</c> and <c>thinking_budget_tokens: 0</c> into every
/// chat-completion request body, preventing reasoning-capable models (e.g. Qwen3 MoE) from
/// entering extended thinking mode regardless of prompt-level directives such as
/// <c>/no_think</c>.
/// </summary>
internal sealed class SuppressThinkingPipelinePolicy : PipelinePolicy
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

    private static void Inject(PipelineMessage message)
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

            writer.WriteBoolean("enable_thinking", false);
            writer.WriteNumber("thinking_budget_tokens", 0);
            writer.WriteEndObject();
        }

        message.Request.Content = BinaryContent.Create(BinaryData.FromBytes(outMs.ToArray()));
    }
}
