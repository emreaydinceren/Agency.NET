using Agency.Acp.Dispatch;
using Agency.Acp.Errors;
using Agency.Acp.Sessions;
using Agency.Acp.Test.Fakes;
using Agency.Harness;
using Agency.Llm.Common;
using dotacp.protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Agency.Acp.Test.Dispatch;

/// <summary>
/// Proves base spec P6 — "every request gets a reply" — for the failure mode the original
/// defect left open: a handler throwing something other than <see cref="Agency.Acp.Errors.AcpJsonRpcException"/>
/// or <see cref="Newtonsoft.Json.JsonException"/>. Before this contract existed, such an exception
/// escaped <see cref="MethodDispatcher.DispatchAsync"/> entirely, was swallowed by
/// <c>StdioTransport.InvokeHandlerAsync</c>'s bare <c>catch (Exception) { }</c>, and the client
/// received no reply, no error, and no crash — it simply waited forever.
/// </summary>
public sealed class DispatchErrorContractTests
{
    /// <summary>
    /// Wires a real <see cref="SessionFactory"/> backed by a <see cref="FakeAgentFactory"/> whose
    /// <c>CreateAgent</c> delegate throws <paramref name="toThrow"/>, and registers it with
    /// <see cref="MethodDispatcher"/>. This mirrors the realistic production trigger for an
    /// unmapped dispatch fault: <c>Agent:DefaultClientName</c> naming a client that isn't in
    /// <c>Agent:LLmClients</c> makes the harness's real <see cref="IAgentFactory.CreateAgent(string?, string?)"/>
    /// throw <see cref="InvalidOperationException"/> deep inside <c>session/new</c>'s handler — a
    /// path <see cref="MethodDispatcher.DispatchAsync"/> has no specific mapping for. No production
    /// test hook is added; this drives the fault through the same <c>session/new</c> seam
    /// <c>Agency.Acp.Test.Sessions.SessionCreationTests</c> and
    /// <c>Agency.Acp.Test.Sessions.SessionIdentityTests</c> already use.
    /// </summary>
    private static void ConfigureDispatcherWithThrowingAgentFactory(Exception toThrow, ILogger? logger = null)
    {
        var services = new ServiceCollection();
        services.AddScoped<IAgentFactory>(_ => new FakeAgentFactory((_, _) => throw toThrow));
        ServiceProvider provider = services.BuildServiceProvider();

        var processOptions = new AgentOptions { DefaultModel = "m", DefaultClientName = "c" };
        var sessionFactory = new SessionFactory(
            provider.GetRequiredService<IServiceScopeFactory>(),
            processOptions,
            _ => Task.FromResult<IReadOnlyList<Model>>([]));

        MethodDispatcher.Configure(new SessionRegistry(), sessionFactory, logger);
    }

    /// <summary>
    /// A handler fault that is neither <see cref="Agency.Acp.Errors.AcpJsonRpcException"/> nor
    /// <see cref="Newtonsoft.Json.JsonException"/> must still produce a JSON-RPC error response
    /// (spec P6) — never an escaping exception. The wire message names both the failing method and
    /// the concrete exception type, so a client-side log points straight at the cause.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_HandlerThrowsUnexpectedException_MapsToInternalErrorNamingMethodAndType()
    {
        ConfigureDispatcherWithThrowingAgentFactory(new InvalidOperationException("No LLM client named 'foo'"));

        string request = """{"jsonrpc":"2.0","id":1,"method":"session/new","params":{"cwd":"/a"}}""";
        string? response = await MethodDispatcher.DispatchAsync(request, TestContext.Current.CancellationToken);

        Assert.NotNull(response);
        JObject envelope = JObject.Parse(response!);
        JObject error = (JObject)envelope["error"]!;
        Assert.Equal(-32603, error["code"]!.Value<int>());
        string message = error["message"]!.Value<string>()!;
        Assert.Contains("session/new", message, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Load-bearing proof companion (Task 16): a handler throwing <see cref="AcpJsonRpcException"/>
    /// must keep mapping to its own carried <see cref="ErrorCode"/> and message — proving the new
    /// terminal <c>catch (Exception)</c> arm added in Task 15 does not shadow this existing,
    /// more-specific arm. The ordering is what is under test, not the trigger, so this reuses the
    /// same <c>session/new</c> seam as the unexpected-exception test above.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_HandlerThrowsAcpJsonRpcException_StillMapsToItsOwnCodeAndMessage()
    {
        ConfigureDispatcherWithThrowingAgentFactory(
            new AcpJsonRpcException(ErrorCode.InvalidParams, "custom invalid params message"));

        string request = """{"jsonrpc":"2.0","id":1,"method":"session/new","params":{"cwd":"/a"}}""";
        string? response = await MethodDispatcher.DispatchAsync(request, TestContext.Current.CancellationToken);

        Assert.NotNull(response);
        JObject envelope = JObject.Parse(response!);
        JObject error = (JObject)envelope["error"]!;
        Assert.Equal(-32602, error["code"]!.Value<int>());
        Assert.Equal("custom invalid params message", error["message"]!.Value<string>());
    }

    /// <summary>
    /// Load-bearing proof companion (Task 16): a handler throwing <see cref="JsonException"/> must
    /// keep mapping to <see cref="ErrorCode.InvalidParams"/> via the existing, more-specific arm —
    /// same rationale as the <see cref="AcpJsonRpcException"/> case above.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_HandlerThrowsJsonException_StillMapsToInvalidParams()
    {
        ConfigureDispatcherWithThrowingAgentFactory(new JsonException("malformed payload"));

        string request = """{"jsonrpc":"2.0","id":1,"method":"session/new","params":{"cwd":"/a"}}""";
        string? response = await MethodDispatcher.DispatchAsync(request, TestContext.Current.CancellationToken);

        Assert.NotNull(response);
        JObject envelope = JObject.Parse(response!);
        JObject error = (JObject)envelope["error"]!;
        Assert.Equal(-32602, error["code"]!.Value<int>());
    }

    /// <summary>
    /// A handler that throws <see cref="OperationCanceledException"/> while the
    /// <see cref="CancellationToken"/> <see cref="MethodDispatcher.DispatchAsync"/> was itself called
    /// with is already cancelled is a clean shutdown unwinding, not a fault (spec §10) — it must not be mapped to
    /// <c>InternalError</c>. Whether <see cref="MethodDispatcher.DispatchAsync"/> rethrows or
    /// returns <see langword="null"/> is an implementation choice; this asserts only the observable
    /// the contract cares about: no <c>-32603</c> (or any) error response reaches the wire.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_HandlerThrowsOperationCanceledExceptionWithAlreadyCancelledToken_ProducesNoErrorResponse()
    {
        ConfigureDispatcherWithThrowingAgentFactory(new OperationCanceledException("turn cancelled"));

        string request = """{"jsonrpc":"2.0","id":1,"method":"session/new","params":{"cwd":"/a"}}""";
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        string? response = null;
        try
        {
            response = await MethodDispatcher.DispatchAsync(request, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Rethrowing is an acceptable implementation choice (see summary) — either way, no error
            // response reaches the wire.
        }

        Assert.Null(response);
    }

    /// <summary>
    /// A notification (no <c>id</c>) whose handler throws must still produce no wire response
    /// (JSON-RPC forbids replying to a notification), but the fault must not vanish silently — it
    /// is logged (spec §14.3: a dispatch fault must be observable somewhere even when there is no
    /// id to attach a wire error to). Driven through the <see cref="MethodDispatcher.Configure"/>
    /// logger seam with a capturing <see cref="ILogger"/> double.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_NotificationHandlerThrows_ProducesNoResponseButLogsTheFault()
    {
        var logger = new CapturingLogger();
        ConfigureDispatcherWithThrowingAgentFactory(new InvalidOperationException("No LLM client named 'foo'"), logger);

        // No "id" field: a JSON-RPC notification, per DispatchAsync's own hasId check.
        string request = """{"jsonrpc":"2.0","method":"session/new","params":{"cwd":"/a"}}""";
        string? response = await MethodDispatcher.DispatchAsync(request, TestContext.Current.CancellationToken);

        Assert.Null(response);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("session/new", StringComparison.Ordinal));
    }
}
