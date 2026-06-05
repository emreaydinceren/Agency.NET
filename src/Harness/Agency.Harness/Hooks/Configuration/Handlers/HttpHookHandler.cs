using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Agency.Harness.Hooks.Configuration;

namespace Agency.Harness.Hooks.Configuration.Handlers;

internal sealed class HttpHookHandler : IHookHandler
{
    private readonly HookHandlerConfig _cfg;
    private readonly HttpClient _client;
    private readonly ILogger? _logger;

    internal HttpHookHandler(HookHandlerConfig cfg, HttpClient client, ILogger? logger = null)
    {
        _cfg = cfg;
        _client = client;
        _logger = logger;
    }

    public async Task<HookHandlerOutput> InvokeAsync(HookPayload payload, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_cfg.Timeout ?? 30));

        using var req = new HttpRequestMessage(HttpMethod.Post, _cfg.Url);
        req.Content = JsonContent.Create(payload, options: HookPayload.SerializerOptions);

        foreach (var (key, value) in _cfg.Headers ?? [])
        {
            req.Headers.TryAddWithoutValidation(key, value);
        }

        HttpResponseMessage? resp;
        try
        {
            resp = await _client.SendAsync(req, cts.Token);
        }
        catch
        {
            return new HookHandlerOutput(HookExitCodes.NonBlockingError, null, null, null);
        }

        int exit = resp.IsSuccessStatusCode ? HookExitCodes.Ok : HookExitCodes.NonBlockingError;
        string body = await resp.Content.ReadAsStringAsync();
        JsonElement? json = TryParseLeadingJson(body);
        return new HookHandlerOutput(exit, json, body, null);
    }

    private static JsonElement? TryParseLeadingJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || !text.TrimStart().StartsWith('{'))
        {
            return null;
        }
        try
        {
            return JsonDocument.Parse(text).RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }
}
