using System.Net;
using System.Net.Sockets;
using System.Text;
using Agency.Llm.Common;
using Agency.Llm.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Agency.Llm.Test;

/// <summary>
/// Verifies that two <see cref="LlmClientOptions"/> built with <c>with {}</c> and different
/// thinking settings produce different outbound request bodies. This is the per-session
/// effort mechanism for D4 — deliberately not <c>reasoning_effort</c>, which is rejected by
/// the spec as model-dependent in both presence and level.
/// </summary>
public sealed class ThinkingOptionsTests
{
    private const string ChatCompletionBody =
        "{\"id\":\"chatcmpl-1\",\"object\":\"chat.completion\",\"created\":1700000000,\"model\":\"test-model\"," +
        "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}]," +
        "\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}";

    /// <summary>
    /// Two <see cref="LlmClientOptions"/> differing only via <c>with {}</c> in
    /// <see cref="LlmClientOptions.EnableThinking"/> / <see cref="LlmClientOptions.ThinkingBudgetTokens"/>
    /// produce different outbound request bodies, and neither ever emits <c>reasoning_effort</c>.
    /// </summary>
    [Fact]
    public async Task DifferentThinkingOptions_ProduceDifferentRequestBodies()
    {
        var ct = TestContext.Current.CancellationToken;
        var port = GetFreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();

        var bodies = new List<string>();
        var serverTask = Task.Run(async () =>
        {
            for (int i = 0; i < 2; i++)
            {
                var ctx = await listener.GetContextAsync().WaitAsync(ct);
                using var reader = new StreamReader(ctx.Request.InputStream);
                bodies.Add(await reader.ReadToEndAsync(ct));

                var bytes = Encoding.UTF8.GetBytes(ChatCompletionBody);
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes, ct);
                ctx.Response.OutputStream.Close();
                ctx.Response.Close();
            }
        }, ct);

        try
        {
            var baseUrl = $"http://localhost:{port}/v1";
            var optionsA = new LlmClientOptions
            {
                ApiKey = "test-key",
                BaseUrl = baseUrl,
                EnableThinking = true,
                ThinkingBudgetTokens = 1024,
            };
            var optionsB = optionsA with { EnableThinking = false, ThinkingBudgetTokens = 0 };

            await SendOneMessageAsync(optionsA, ct);
            await SendOneMessageAsync(optionsB, ct);

            await serverTask;
        }
        finally
        {
            listener.Stop();
        }

        Assert.Equal(2, bodies.Count);
        Assert.NotEqual(bodies[0], bodies[1]);
        Assert.Contains("\"enable_thinking\":true", bodies[0]);
        Assert.Contains("\"enable_thinking\":false", bodies[1]);
        Assert.DoesNotContain("reasoning_effort", bodies[0]);
        Assert.DoesNotContain("reasoning_effort", bodies[1]);
    }

    private static async Task SendOneMessageAsync(LlmClientOptions options, CancellationToken ct)
    {
        var client = new OpenAIClient(Options.Create(options)).CreateChatClient();
        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")],
            new ChatOptions { ModelId = "test-model" },
            ct);
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
