using Agency.Acp.Dispatch;
using Agency.Acp.Sessions;
using Agency.Acp.Test.Fakes;
using Agency.Harness;
using Agency.Llm.Common;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;

namespace Agency.Acp.Test.Dispatch;

/// <summary>
/// Pins the G6 contract (spec §8.1): "an unknown model id is never an error." G6 used to be covered
/// by a <c>session/new</c> requested-model test, but that path was deliberately deleted — a
/// client-requested model at <c>session/new</c> is gone; <c>session/set_config_option</c> is now the
/// sole model-selection path (spec §6.2). That left the guarantee true by construction (nothing in
/// <see cref="MethodDispatcher"/>'s <c>session/set_config_option</c> handler validates a "model"
/// value against the catalogue) but untested. These three tests are G6's replacement: they drive
/// real <c>session/new</c> + <c>session/set_config_option</c> requests through
/// <see cref="MethodDispatcher.DispatchAsync"/> and pin the actual wire-level behavior for an unknown
/// model id, an unknown effort id, and an unknown config id — including the asymmetry between the
/// first two (an unknown model round-trips verbatim; an unknown effort does not, because the
/// response's effort <c>currentValue</c> is unconditionally the first entry of the effort ladder,
/// not a function of what was actually stored — see <see cref="SetConfigOption_UnknownEffortId_IsNotAnError_ButCurrentValueIsCoercedToOff"/>).
/// </summary>
public sealed class G6ContractTests
{
    private static AgentOptions BuildProcessOptions() => new()
    {
        DefaultModel = "model-a",
        DefaultClientName = "c",
        LLmClients = [new LlmClientOptions { Name = "c", ClientType = "Claude" }],
    };

    private static (SessionFactory Factory, FakeAcpClientProxy Proxy) ConfigureDispatcher()
    {
        var services = new ServiceCollection();
        services.AddScoped<IAgentFactory>(_ => new FakeAgentFactory((_, model) =>
            new Agent(new FakeChatClient(), model ?? "model-a")));
        ServiceProvider provider = services.BuildServiceProvider();

        var sessionFactory = new SessionFactory(
            provider.GetRequiredService<IServiceScopeFactory>(),
            BuildProcessOptions(),
            _ => Task.FromResult<IReadOnlyList<Model>>([new Model("model-a", "A")]));

        MethodDispatcher.Configure(new SessionRegistry(), sessionFactory);
        var proxy = new FakeAcpClientProxy();
        MethodDispatcher.ConfigureClient(proxy);

        return (sessionFactory, proxy);
    }

    private static async Task<string> StartSessionAsync(CancellationToken ct)
    {
        string? newResponse = await MethodDispatcher.DispatchAsync(
            """{"jsonrpc":"2.0","id":1,"method":"session/new","params":{"cwd":"/a"}}""", ct);
        return (string)JObject.Parse(newResponse!)["result"]!["sessionId"]!;
    }

    /// <summary>
    /// G6, model row: <c>session/set_config_option</c> with <c>configId: "model"</c> and a value that
    /// names no real model (<c>totally/not-a-real-model-9999</c>) is accepted — no JSON-RPC error —
    /// and the response's <c>configOptions[].currentValue</c> for the "model" entry echoes the value
    /// back verbatim. Nothing in the handler validates a "model" value against
    /// <see cref="SessionState.Catalogue"/> (<c>MethodDispatcher.cs</c>'s <c>case "model":</c> stores
    /// it unconditionally, and <see cref="SessionFactory.BuildConfigOptions"/> reports back whatever
    /// was stored) — this is the open-set half of the model/effort asymmetry.
    /// </summary>
    [Fact]
    public async Task SetConfigOption_UnknownModelId_IsNotAnError_AndCurrentValueEchoesVerbatim()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        ConfigureDispatcher();
        string sessionId = await StartSessionAsync(ct);

        const string unknownModelId = "totally/not-a-real-model-9999";
        string request = "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"session/set_config_option\",\"params\":{\"sessionId\":\"" + sessionId + "\",\"configId\":\"model\",\"type\":\"select\",\"value\":\"" + unknownModelId + "\"}}";
        string? response = await MethodDispatcher.DispatchAsync(request, ct);

        JObject envelope = JObject.Parse(response!);
        Assert.Null(envelope["error"]);

        JArray configOptions = (JArray)envelope["result"]!["configOptions"]!;
        JObject modelOption = (JObject)configOptions.Single(o => (string)o["id"]! == "model");
        Assert.Equal(unknownModelId, (string)modelOption["currentValue"]!);
    }

    /// <summary>
    /// G6, effort row: <c>session/set_config_option</c> with <c>configId: "effort"</c> and a value
    /// off the ladder (<c>not-a-level</c>) is also accepted — no JSON-RPC error — but the response's
    /// <c>configOptions[].currentValue</c> for the "effort" entry does not echo it back; it reads
    /// <c>"off"</c>. Tracing the mechanism: <c>MethodDispatcher.cs</c>'s <c>case "effort":</c> stores
    /// the raw value verbatim on <see cref="SessionState.EffortId"/>, same as "model" — so the
    /// coercion is not in the dispatcher. It is downstream, in
    /// <see cref="SessionFactory.BuildConfigOptions"/> (<c>SessionFactory.cs</c>, the "effort"
    /// <c>SessionConfigSelect</c> block): <c>CurrentValue = effortOptions[0].Value</c> is hardcoded to
    /// the ladder's first entry and never reads <see cref="SessionState.EffortId"/> at all — so this
    /// happens for <em>any</em> effort value, not only unrecognized ones, and "off" is what it reads
    /// only because "off" happens to be first in both the Claude and OpenAI ladders
    /// (<see cref="SessionFactory"/>'s private <c>BuildEffortLadder</c>). The value that actually
    /// reaches client construction is separately controlled by
    /// <see cref="SessionFactory.BuildEffortTransform"/>, which for an unrecognized id falls through
    /// its switch to the <c>null</c> arm — no thinking transform is applied, which is its own
    /// fail-soft behavior distinct from the reported "off" transform.
    /// </summary>
    [Fact]
    public async Task SetConfigOption_UnknownEffortId_IsNotAnError_ButCurrentValueIsCoercedToOff()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        ConfigureDispatcher();
        string sessionId = await StartSessionAsync(ct);

        string request = "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"session/set_config_option\",\"params\":{\"sessionId\":\"" + sessionId + "\",\"configId\":\"effort\",\"type\":\"select\",\"value\":\"not-a-level\"}}";
        string? response = await MethodDispatcher.DispatchAsync(request, ct);

        JObject envelope = JObject.Parse(response!);
        Assert.Null(envelope["error"]);

        JArray configOptions = (JArray)envelope["result"]!["configOptions"]!;
        JObject effortOption = (JObject)configOptions.Single(o => (string)o["id"]! == "effort");
        Assert.Equal("off", (string)effortOption["currentValue"]!);
    }

    /// <summary>
    /// G6's complement: an unrecognized <c>configId</c> itself (<c>"nonsense"</c> — neither "model"
    /// nor "effort") is the one shape of <c>session/set_config_option</c> input that <em>is</em> a
    /// JSON-RPC error — <c>MethodDispatcher.cs</c>'s <c>default:</c> arm of the <c>configId</c> switch
    /// throws <c>InvalidParams</c> with message <c>Unknown config option 'nonsense'.</c>. This is the
    /// contrast case: G6 is specifically about an unknown *value* for a known config id, not an
    /// unknown config id itself.
    /// </summary>
    [Fact]
    public async Task SetConfigOption_UnknownConfigId_IsAnError()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        ConfigureDispatcher();
        string sessionId = await StartSessionAsync(ct);

        string request = "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"session/set_config_option\",\"params\":{\"sessionId\":\"" + sessionId + "\",\"configId\":\"nonsense\",\"type\":\"select\",\"value\":\"anything\"}}";
        string? response = await MethodDispatcher.DispatchAsync(request, ct);

        JObject envelope = JObject.Parse(response!);
        JObject error = (JObject)envelope["error"]!;
        Assert.Equal(-32602, error["code"]!.Value<int>());
        Assert.Equal("Unknown config option 'nonsense'.", error["message"]!.Value<string>());
    }
}
