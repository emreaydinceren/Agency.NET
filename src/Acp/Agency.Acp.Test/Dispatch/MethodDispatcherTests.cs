using Agency.Acp.Dispatch;
using Newtonsoft.Json.Linq;

namespace Agency.Acp.Test.Dispatch;

/// <summary>
/// Behavioral tests for <see cref="MethodDispatcher"/>: routing, capability reporting, and
/// JSON-RPC error mapping.
/// </summary>
public sealed class MethodDispatcherTests
{
    /// <summary><c>initialize</c> reports protocol version 1, no auth methods, and load-session support of false.</summary>
    [Fact]
    public async Task DispatchAsync_Initialize_ReportsAgreedCapabilities()
    {
        string? response = await MethodDispatcher.DispatchAsync(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":1}}""",
            TestContext.Current.CancellationToken);

        Assert.NotNull(response);
        JObject envelope = JObject.Parse(response!);
        JObject result = (JObject)envelope["result"]!;
        Assert.Equal(1, result["protocolVersion"]!.Value<int>());
        Assert.Empty((JArray)result["authMethods"]!);
        Assert.False(((JObject)result["agentCapabilities"]!)["loadSession"]!.Value<bool>());
    }

    /// <summary>Methods v1 does not support each return -32601 (MethodNotFound).</summary>
    [Theory]
    [InlineData("session/load")]
    [InlineData("session/list")]
    [InlineData("session/resume")]
    [InlineData("session/set_mode")]
    [InlineData("authenticate")]
    [InlineData("logout")]
    public async Task DispatchAsync_UnsupportedMethod_ReturnsMethodNotFound(string method)
    {
        string request = "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"" + method + "\",\"params\":{}}";

        string? response = await MethodDispatcher.DispatchAsync(request, TestContext.Current.CancellationToken);

        Assert.NotNull(response);
        JObject envelope = JObject.Parse(response!);
        Assert.Equal(-32601, ((JObject)envelope["error"]!)["code"]!.Value<int>());
    }

    /// <summary>A line that is not valid JSON yields -32700 (ParseError).</summary>
    [Fact]
    public async Task DispatchAsync_UnparseableRequest_ReturnsParseError()
    {
        string? response = await MethodDispatcher.DispatchAsync("{not json", TestContext.Current.CancellationToken);

        Assert.NotNull(response);
        JObject envelope = JObject.Parse(response!);
        Assert.Equal(-32700, ((JObject)envelope["error"]!)["code"]!.Value<int>());
    }

    /// <summary>Params that fail to bind to the method's expected shape yield -32602 (InvalidParams).</summary>
    [Fact]
    public async Task DispatchAsync_MalformedParams_ReturnsInvalidParams()
    {
        // protocolVersion is a ushort alias; a non-numeric value cannot be converted.
        string request = "{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"not-a-number\"}}";

        string? response = await MethodDispatcher.DispatchAsync(request, TestContext.Current.CancellationToken);

        Assert.NotNull(response);
        JObject envelope = JObject.Parse(response!);
        Assert.Equal(-32602, ((JObject)envelope["error"]!)["code"]!.Value<int>());
    }
}
