using System.Net;
using System.Text;
using Agency.Harness.Hooks.Configuration;
using Agency.Harness.Hooks.Configuration.Handlers;

namespace Agency.Harness.Test.Hooks.Configuration.Handlers;

/// <summary>
/// Tests for <see cref="HttpHookHandler"/>, covering response-status interpretation (2xx deny/rewrite,
/// 5xx as a non-blocking error), request timeout handling, and the outgoing payload/headers.
/// </summary>
public sealed class HttpHookHandlerTests
{
    // ── helpers ──────────────────────────────────────────────────────────────

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;
        private readonly TimeSpan _delay;
        public HttpRequestMessage? LastRequest { get; private set; }

        internal StubHandler(HttpStatusCode status, string body, TimeSpan delay = default)
        {
            _response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            _delay = delay;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            this.LastRequest = request;
            if (this._delay > TimeSpan.Zero)
            {
                await Task.Delay(this._delay, cancellationToken);
            }
            return this._response;
        }
    }

    private static HttpHookHandler MakeHandler(
        StubHandler stub, string url = "http://test/hook",
        Dictionary<string, string>? headers = null, int timeout = 30)
    {
        HttpClient client = new HttpClient(stub);
        HookHandlerConfig cfg = new HookHandlerConfig
        {
            Type = HookHandlerKind.Http,
            Url = url,
            Headers = headers,
            Timeout = timeout
        };
        return new HttpHookHandler(cfg, client);
    }

    private static HookPayload MakePayload() => new HookPayload { HookEventName = "PreToolUse" };

    // ── tests ─────────────────────────────────────────────────────────────────

    /// <summary>A 2xx response body containing deny JSON produces an output with that JSON populated.</summary>
    [Fact]
    public async Task Http_2xxDenyJson_ProducesDeny()
    {
        const string body = """{"hookSpecificOutput":{"permissionDecision":"deny"}}""";
        StubHandler stub = new StubHandler(HttpStatusCode.OK, body);
        HttpHookHandler handler = MakeHandler(stub);

        HookHandlerOutput output = await handler.InvokeAsync(MakePayload(), CancellationToken.None);

        Assert.Equal(0, output.ExitCode);
        Assert.True(output.Json.HasValue);
    }

    /// <summary>A 2xx response body containing rewrite JSON (a <c>tool_input</c> object) produces an output with that JSON populated.</summary>
    [Fact]
    public async Task Http_2xxRewriteJson_ProducesRewrite()
    {
        const string body = """{"tool_input":{"key":"rewritten"}}""";
        StubHandler stub = new StubHandler(HttpStatusCode.OK, body);
        HttpHookHandler handler = MakeHandler(stub);

        HookHandlerOutput output = await handler.InvokeAsync(MakePayload(), CancellationToken.None);

        Assert.Equal(0, output.ExitCode);
        Assert.True(output.Json.HasValue);
    }

    /// <summary>A 5xx server error response maps to <c>HookExitCodes.NonBlockingError</c> rather than throwing.</summary>
    [Fact]
    public async Task Http_5xx_ProducesNonBlockingError()
    {
        StubHandler stub = new StubHandler(HttpStatusCode.InternalServerError, "");
        HttpHookHandler handler = MakeHandler(stub);

        HookHandlerOutput output = await handler.InvokeAsync(MakePayload(), CancellationToken.None);

        Assert.Equal(HookExitCodes.NonBlockingError, output.ExitCode);
    }

    /// <summary>A request that exceeds the configured timeout maps to <c>HookExitCodes.NonBlockingError</c> rather than throwing.</summary>
    [Fact]
    public async Task Http_Timeout_NonBlocking()
    {
        StubHandler stub = new StubHandler(HttpStatusCode.OK, "{}", delay: TimeSpan.FromSeconds(5));
        HttpHookHandler handler = MakeHandler(stub, timeout: 1);

        HookHandlerOutput output = await handler.InvokeAsync(MakePayload(), CancellationToken.None);

        Assert.Equal(HookExitCodes.NonBlockingError, output.ExitCode);
    }

    /// <summary>The handler sends the payload as an HTTP POST and includes each configured custom header on the outgoing request.</summary>
    [Fact]
    public async Task Http_SendsPayloadAndHeaders()
    {
        const string body = "{}";
        StubHandler stub = new StubHandler(HttpStatusCode.OK, body);
        Dictionary<string, string> headers = new Dictionary<string, string> { ["X-Source"] = "test" };
        HttpHookHandler handler = MakeHandler(stub, headers: headers);

        await handler.InvokeAsync(MakePayload(), CancellationToken.None);

        Assert.NotNull(stub.LastRequest);
        Assert.Equal(HttpMethod.Post, stub.LastRequest!.Method);
        Assert.True(stub.LastRequest.Headers.Contains("X-Source"));
    }
}
