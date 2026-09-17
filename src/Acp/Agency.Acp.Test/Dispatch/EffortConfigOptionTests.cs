using Agency.Acp.Dispatch;
using Agency.Acp.Sessions;
using Agency.Acp.Test.Fakes;
using Agency.Harness;
using Agency.Llm.Common;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;

namespace Agency.Acp.Test.Dispatch;

/// <summary>
/// Closes the effort gap flagged during D8 (spec §6.7, §16 G-4): choosing an effort tier via
/// <c>session/set_config_option</c> must actually reach client construction, not just record an id
/// on <see cref="SessionState.EffortId"/>. Drives the full dispatch sequence and inspects the
/// <see cref="LlmClientOptions"/> the (fake) <see cref="IAgentFactory"/> was actually asked to build
/// a client from.
/// </summary>
public sealed class EffortConfigOptionTests
{
    private static ChatResponse TextResponse(string text) =>
        new([new ChatMessage(ChatRole.Assistant, text)])
        {
            Usage = new UsageDetails { InputTokenCount = 5, OutputTokenCount = 3 },
            FinishReason = ChatFinishReason.Stop,
        };

    /// <summary>
    /// Two sessions on different Claude-dialect effort tiers produce different
    /// <see cref="LlmClientOptions"/> at client-construction time — <c>reasoning_effort</c> is never
    /// the mechanism (spec §6.7 rejects it); <c>ThinkingBudgetTokens</c>/<c>EnableThinking</c> are.
    /// </summary>
    [Fact]
    public async Task SetConfigOption_EffortChange_ThreadsIntoClientConstruction()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        var capturedOptions = new List<LlmClientOptions>();
        var services = new ServiceCollection();
        services.AddScoped<IAgentFactory>(_ => new FakeAgentFactory((_, model, configureClientOptions) =>
        {
            LlmClientOptions baseline = new() { Name = "c", ClientType = "Claude" };
            LlmClientOptions effective = configureClientOptions?.Invoke(baseline) ?? baseline;
            capturedOptions.Add(effective);

            var client = new FakeChatClient();
            client.EnqueueResponse(TextResponse("ok"));
            return new Agent(client, model ?? "model-a");
        }));
        ServiceProvider provider = services.BuildServiceProvider();

        var processOptions = new AgentOptions
        {
            DefaultModel = "model-a",
            DefaultClientName = "c",
            LLmClients = [new LlmClientOptions { Name = "c", ClientType = "Claude" }],
        };
        var sessionFactory = new SessionFactory(
            provider.GetRequiredService<IServiceScopeFactory>(),
            processOptions,
            _ => Task.FromResult<IReadOnlyList<Model>>([new Model("model-a", "A")]));

        MethodDispatcher.Configure(new SessionRegistry(), sessionFactory);
        MethodDispatcher.ConfigureClient(new FakeAcpClientProxy());

        string? newResponse = await MethodDispatcher.DispatchAsync(
            """{"jsonrpc":"2.0","id":1,"method":"session/new","params":{"cwd":"/a"}}""", ct);
        var sessionId = (string)JObject.Parse(newResponse!)["result"]!["sessionId"]!;

        // Set effort to "low".
        string setLow = "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"session/set_config_option\",\"params\":{\"sessionId\":\"" + sessionId + "\",\"configId\":\"effort\",\"type\":\"select\",\"value\":\"low\"}}";
        string? setLowResponse = await MethodDispatcher.DispatchAsync(setLow, ct);
        Assert.Null(JObject.Parse(setLowResponse!)["error"]);

        // Set effort to "high".
        string setHigh = "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"session/set_config_option\",\"params\":{\"sessionId\":\"" + sessionId + "\",\"configId\":\"effort\",\"type\":\"select\",\"value\":\"high\"}}";
        string? setHighResponse = await MethodDispatcher.DispatchAsync(setHigh, ct);
        Assert.Null(JObject.Parse(setHighResponse!)["error"]);

        // First captured client is from session/new (no effort yet); the next two are the
        // "low" and "high" rebuilds.
        Assert.Equal(3, capturedOptions.Count);
        LlmClientOptions low = capturedOptions[1];
        LlmClientOptions high = capturedOptions[2];

        Assert.NotEqual(low.ThinkingBudgetTokens, high.ThinkingBudgetTokens);
        Assert.True(low.EnableThinking);
        Assert.True(high.EnableThinking);
        Assert.True(high.ThinkingBudgetTokens > low.ThinkingBudgetTokens);
    }

    /// <summary>
    /// A model change re-sends the currently-selected effort, unchanged, to the rebuilt client
    /// (spec U-4 / §6.7: "the effort ladder is re-sent for the rebuilt client, and is unchanged
    /// unless the surface changed").
    /// </summary>
    [Fact]
    public async Task SetConfigOption_ModelChange_ReappliesCurrentEffort()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        var capturedOptions = new List<LlmClientOptions>();
        var services = new ServiceCollection();
        services.AddScoped<IAgentFactory>(_ => new FakeAgentFactory((_, model, configureClientOptions) =>
        {
            LlmClientOptions baseline = new() { Name = "c", ClientType = "Claude" };
            LlmClientOptions effective = configureClientOptions?.Invoke(baseline) ?? baseline;
            capturedOptions.Add(effective);

            var client = new FakeChatClient();
            client.EnqueueResponse(TextResponse("ok"));
            return new Agent(client, model ?? "model-a");
        }));
        ServiceProvider provider = services.BuildServiceProvider();

        var processOptions = new AgentOptions
        {
            DefaultModel = "model-a",
            DefaultClientName = "c",
            LLmClients = [new LlmClientOptions { Name = "c", ClientType = "Claude" }],
        };
        var sessionFactory = new SessionFactory(
            provider.GetRequiredService<IServiceScopeFactory>(),
            processOptions,
            _ => Task.FromResult<IReadOnlyList<Model>>([new Model("model-a", "A"), new Model("model-b", "B")]));

        MethodDispatcher.Configure(new SessionRegistry(), sessionFactory);
        MethodDispatcher.ConfigureClient(new FakeAcpClientProxy());

        string? newResponse = await MethodDispatcher.DispatchAsync(
            """{"jsonrpc":"2.0","id":1,"method":"session/new","params":{"cwd":"/a"}}""", ct);
        var sessionId = (string)JObject.Parse(newResponse!)["result"]!["sessionId"]!;

        string setHigh = "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"session/set_config_option\",\"params\":{\"sessionId\":\"" + sessionId + "\",\"configId\":\"effort\",\"type\":\"select\",\"value\":\"high\"}}";
        await MethodDispatcher.DispatchAsync(setHigh, ct);

        string setModel = "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"session/set_config_option\",\"params\":{\"sessionId\":\"" + sessionId + "\",\"configId\":\"model\",\"type\":\"select\",\"value\":\"model-b\"}}";
        await MethodDispatcher.DispatchAsync(setModel, ct);

        LlmClientOptions afterHigh = capturedOptions[1];
        LlmClientOptions afterModelSwap = capturedOptions[2];

        Assert.Equal(afterHigh.ThinkingBudgetTokens, afterModelSwap.ThinkingBudgetTokens);
        Assert.Equal(afterHigh.EnableThinking, afterModelSwap.EnableThinking);
    }
}
