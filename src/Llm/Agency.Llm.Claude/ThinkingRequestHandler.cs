using System.Text.Json;

namespace Agency.Llm.Claude;

/// <summary>
/// Injects an Anthropic <c>thinking: { type, budget_tokens }</c> block into every Messages
/// API request body, derived from <see cref="Agency.Llm.Common.LlmClientOptions.EnableThinking"/>
/// and <see cref="Agency.Llm.Common.LlmClientOptions.ThinkingBudgetTokens"/>. When
/// <c>EnableThinking</c> is explicitly <see langword="false"/>, thinking is disabled
/// (<c>{ "type": "disabled" }</c>); otherwise, when a budget is set, thinking is enabled with
/// that budget. When neither is set, nothing is injected (spec P3: unspecified stays absent).
/// </summary>
internal sealed class ThinkingRequestHandler(bool? enableThinking, int? thinkingBudgetTokens) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content is not null)
        {
            var body = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            using JsonDocument doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                using MemoryStream outMs = new();
                using (Utf8JsonWriter writer = new(outMs))
                {
                    writer.WriteStartObject();
                    foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
                    {
                        prop.WriteTo(writer);
                    }

                    if (enableThinking == false)
                    {
                        writer.WritePropertyName("thinking");
                        writer.WriteStartObject();
                        writer.WriteString("type", "disabled");
                        writer.WriteEndObject();
                    }
                    else if (thinkingBudgetTokens is { } budgetTokens)
                    {
                        writer.WritePropertyName("thinking");
                        writer.WriteStartObject();
                        writer.WriteString("type", "enabled");
                        writer.WriteNumber("budget_tokens", budgetTokens);
                        writer.WriteEndObject();
                    }

                    writer.WriteEndObject();
                }

                var headers = request.Content.Headers;
                request.Content = new ByteArrayContent(outMs.ToArray());
                foreach (var header in headers)
                {
                    request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }
        }

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
