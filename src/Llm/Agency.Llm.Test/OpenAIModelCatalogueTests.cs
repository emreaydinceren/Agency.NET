using System.Net;
using System.Net.Sockets;
using System.Text;
using Agency.Llm.Common;
using Agency.Llm.OpenAI;
using Microsoft.Extensions.Options;

namespace Agency.Llm.Test;

/// <summary>
/// Verifies that <see cref="OpenAIClient.GetModelsAsync"/> enriches the standard
/// OpenAI-compatible model list with richer metadata when a vendor-specific catalogue
/// endpoint (LM Studio's native <c>/api/v0/models</c>) is available, and degrades cleanly
/// to the plain result — with all metadata <see langword="null"/> — otherwise. Enrichment
/// must never be load-bearing (spec P2) and absence must never be reported as a claim (P3).
/// </summary>
public sealed class OpenAIModelCatalogueTests
{
    private const string StandardModelsBody =
        "{\"object\":\"list\",\"data\":[{\"id\":\"model-a\",\"object\":\"model\",\"created\":1700000000,\"owned_by\":\"organization\"}]}";

    private const string RichCatalogueBody =
        "{\"data\":[{\"id\":\"model-a\",\"object\":\"model\",\"type\":\"llm\",\"state\":\"loaded\",\"max_context_length\":4096}]}";

    /// <summary>A server answering only <c>/v1/models</c> yields models with all optional metadata null.</summary>
    [Fact]
    public async Task GetModelsAsync_OnlyStandardEndpoint_AllMetadataNull()
    {
        var models = await RunAgainstServerAsync(RespondStandardOnly, TestContext.Current.CancellationToken);

        var model = Assert.Single(models);
        Assert.Null(model.Kind);
        Assert.Null(model.ContextLength);
        Assert.Null(model.IsLoaded);
    }

    /// <summary>A server also answering the richer catalogue yields populated metadata.</summary>
    [Fact]
    public async Task GetModelsAsync_RichCatalogueAvailable_PopulatesMetadata()
    {
        var models = await RunAgainstServerAsync(RespondWithRichCatalogue, TestContext.Current.CancellationToken);

        var model = Assert.Single(models);
        Assert.Equal(ModelKind.Chat, model.Kind);
        Assert.Equal(4096, model.ContextLength);
        Assert.True(model.IsLoaded);
    }

    /// <summary>A 404 on the richer endpoint degrades to the plain result without throwing.</summary>
    [Fact]
    public async Task GetModelsAsync_RichCatalogue404_DegradesToStandardResult()
    {
        var models = await RunAgainstServerAsync(RespondStandardOnly, TestContext.Current.CancellationToken);

        var model = Assert.Single(models);
        Assert.Null(model.Kind);
        Assert.Null(model.ContextLength);
        Assert.Null(model.IsLoaded);
    }

    /// <summary>Malformed JSON from the richer endpoint degrades to the plain result without throwing.</summary>
    [Fact]
    public async Task GetModelsAsync_RichCatalogueMalformedJson_DegradesToStandardResult()
    {
        var models = await RunAgainstServerAsync(RespondWithMalformedRichCatalogue, TestContext.Current.CancellationToken);

        var model = Assert.Single(models);
        Assert.Null(model.Kind);
        Assert.Null(model.ContextLength);
        Assert.Null(model.IsLoaded);
    }

    // ── Server handlers ──────────────────────────────────────────────────────

    private static Task RespondStandardOnly(HttpListenerContext ctx) =>
        ctx.Request.Url!.AbsolutePath switch
        {
            "/v1/models" => WriteJsonAsync(ctx, 200, StandardModelsBody),
            _ => WriteJsonAsync(ctx, 404, "{}"),
        };

    private static Task RespondWithRichCatalogue(HttpListenerContext ctx) =>
        ctx.Request.Url!.AbsolutePath switch
        {
            "/v1/models" => WriteJsonAsync(ctx, 200, StandardModelsBody),
            "/api/v0/models" => WriteJsonAsync(ctx, 200, RichCatalogueBody),
            _ => WriteJsonAsync(ctx, 404, "{}"),
        };

    private static Task RespondWithMalformedRichCatalogue(HttpListenerContext ctx) =>
        ctx.Request.Url!.AbsolutePath switch
        {
            "/v1/models" => WriteJsonAsync(ctx, 200, StandardModelsBody),
            "/api/v0/models" => WriteJsonAsync(ctx, 200, "{not valid json"),
            _ => WriteJsonAsync(ctx, 404, "{}"),
        };

    // ── Test harness ─────────────────────────────────────────────────────────

    private static async Task WriteJsonAsync(HttpListenerContext ctx, int statusCode, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.StatusCode = statusCode;
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes, CancellationToken.None);
        ctx.Response.OutputStream.Close();
        ctx.Response.Close();
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<IReadOnlyList<Model>> RunAgainstServerAsync(
        Func<HttpListenerContext, Task> handleRequest,
        CancellationToken ct)
    {
        var port = GetFreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();

        var serverTask = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await listener.GetContextAsync().WaitAsync(ct).ConfigureAwait(false);
                }
                catch
                {
                    return;
                }

                await handleRequest(ctx).ConfigureAwait(false);
            }
        }, ct);

        try
        {
            var client = new OpenAIClient(Options.Create(new LlmClientOptions
            {
                ApiKey = "test-key",
                BaseUrl = $"http://localhost:{port}/v1",
            }));

            return await client.GetModelsAsync(ct);
        }
        finally
        {
            listener.Stop();
            await serverTask.ConfigureAwait(false);
        }
    }
}
