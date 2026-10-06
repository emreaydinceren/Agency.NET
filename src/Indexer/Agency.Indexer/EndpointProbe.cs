using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Agency.Embeddings.OpenAI;

namespace Agency.Indexer;

/// <summary>
/// Plain HTTP probes of an OpenAI-compatible endpoint, used by <c>doctor</c> and <c>setup</c> to list its models and
/// to measure a model's vector length instead of asking the user for it.
/// </summary>
internal static class EndpointProbe
{
    /// <summary>Returns the ids of the models the endpoint lists at <c>GET {BaseUrl}/models</c>.</summary>
    public static async Task<IReadOnlyList<string>> ListModelsAsync(HttpClient http, EmbeddingOptions options, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint(options, "models"));
        AddAuthorization(request, options);
        using HttpResponseMessage response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return document.RootElement.GetProperty("data").EnumerateArray()
            .Select(model => model.GetProperty("id").GetString() ?? "")
            .Where(id => id.Length > 0)
            .ToList();
    }

    /// <summary>Embeds one probe string and returns the length of the vector the model produced.</summary>
    public static async Task<int> MeasureDimensionsAsync(HttpClient http, EmbeddingOptions options, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint(options, "embeddings"))
        {
            Content = new StringContent(JsonSerializer.Serialize(new { model = options.ModelId, input = "probe" }), Encoding.UTF8, "application/json"),
        };
        AddAuthorization(request, options);
        using HttpResponseMessage response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return document.RootElement.GetProperty("data")[0].GetProperty("embedding").GetArrayLength();
    }

    private static Uri Endpoint(EmbeddingOptions options, string path) => new($"{options.BaseUrl!.TrimEnd('/')}/{path}");

    private static void AddAuthorization(HttpRequestMessage request, EmbeddingOptions options)
    {
        if (!string.IsNullOrEmpty(options.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        }
    }
}
