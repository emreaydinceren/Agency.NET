using System.Net;
using System.Text;

namespace Agency.Embeddings.OpenAI.Test;

/// <summary>
/// Returns a preset JSON body for every HTTP request, allowing tests to simulate
/// LM Studio responses without a running server.
/// </summary>
internal sealed class StubHttpMessageHandler(string responseJson, HttpStatusCode statusCode = HttpStatusCode.OK)
    : HttpMessageHandler
{
    /// <summary>
    /// Returns the preset response body for any request.
    /// </summary>
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
        };

        return Task.FromResult(response);
    }

    /// <summary>Builds the OpenAI-format embeddings response JSON for the given float arrays.</summary>
    internal static string BuildEmbeddingsJson(params float[][] embeddings)
    {
        var dataItems = embeddings
            .Select((floats, index) =>
            {
                var values = string.Join(",", floats.Select(f => f.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
                return $"{{\"object\":\"embedding\",\"embedding\":[{values}],\"index\":{index}}}";
            });

        var data = string.Join(",", dataItems);
        var totalTokens = embeddings.Length * 5;

        return $$"""
                 {
                   "object": "list",
                   "data": [{{data}}],
                   "model": "text-embedding-qwen3-embedding-8b",
                   "usage": { "prompt_tokens": {{totalTokens}}, "total_tokens": {{totalTokens}} }
                 }
                 """;
    }
}