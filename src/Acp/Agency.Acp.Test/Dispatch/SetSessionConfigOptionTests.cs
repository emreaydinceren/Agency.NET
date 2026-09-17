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
/// Behavioral test for <c>session/set_config_option</c> (spec §6.2, §6.7, §16 G-4): a model change
/// rebuilds the client and <see cref="Agent"/> and calls <see cref="ChatSession.SetAgent"/>, which
/// preserves conversation history — the same path a model change takes.
/// </summary>
public sealed class SetSessionConfigOptionTests
{
    private static ChatResponse TextResponse(string text) =>
        new([new ChatMessage(ChatRole.Assistant, text)])
        {
            Usage = new UsageDetails { InputTokenCount = 5, OutputTokenCount = 3 },
            FinishReason = ChatFinishReason.Stop,
        };

    /// <summary>
    /// A mid-session model change (via the full <c>session/new</c> → <c>session/prompt</c> →
    /// <c>session/set_config_option</c> → <c>session/prompt</c> dispatch sequence) preserves history:
    /// the second turn, on the new model's client, receives the first turn's message alongside its
    /// own — and the old model's client is never called again.
    /// </summary>
    [Fact]
    public async Task SetConfigOption_ModelChange_PreservesHistory_AndNextTurnRunsOnNewModel()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        var clientsByModel = new Dictionary<string, FakeChatClient>(StringComparer.Ordinal);
        var services = new ServiceCollection();
        services.AddScoped<IAgentFactory>(_ => new FakeAgentFactory((_, model) =>
        {
            string modelName = model ?? "model-a";
            var client = new FakeChatClient();
            client.EnqueueResponse(TextResponse($"hi from {modelName}"));
            clientsByModel[modelName] = client;
            return new Agent(client, modelName);
        }));
        ServiceProvider provider = services.BuildServiceProvider();

        var processOptions = new AgentOptions { DefaultModel = "model-a", DefaultClientName = "c" };
        var sessionFactory = new SessionFactory(
            provider.GetRequiredService<IServiceScopeFactory>(),
            processOptions,
            _ => Task.FromResult<IReadOnlyList<Model>>([new Model("model-a", "A"), new Model("model-b", "B")]));

        MethodDispatcher.Configure(new SessionRegistry(), sessionFactory);
        MethodDispatcher.ConfigureClient(new FakeAcpClientProxy());

        // 1. session/new — starts on the default model, model-a.
        string? newResponse = await MethodDispatcher.DispatchAsync(
            """{"jsonrpc":"2.0","id":1,"method":"session/new","params":{"cwd":"/a"}}""", ct);
        var sessionId = (string)JObject.Parse(newResponse!)["result"]!["sessionId"]!;

        // 2. session/prompt — first turn, on model-a.
        string promptRequest1 = "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"session/prompt\",\"params\":{\"sessionId\":\"" + sessionId + "\",\"prompt\":[{\"type\":\"text\",\"text\":\"hello\"}]}}";
        string? promptResponse1 = await MethodDispatcher.DispatchAsync(promptRequest1, ct);
        Assert.Equal("end_turn", (string)JObject.Parse(promptResponse1!)["result"]!["stopReason"]!);

        // 3. session/set_config_option — switch to model-b.
        string setConfigRequest = "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"session/set_config_option\",\"params\":{\"sessionId\":\"" + sessionId + "\",\"configId\":\"model\",\"type\":\"select\",\"value\":\"model-b\"}}";
        string? setConfigResponse = await MethodDispatcher.DispatchAsync(setConfigRequest, ct);
        Assert.Null(JObject.Parse(setConfigResponse!)["error"]);

        // 4. session/prompt — second turn, now on model-b.
        string promptRequest2 = "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"session/prompt\",\"params\":{\"sessionId\":\"" + sessionId + "\",\"prompt\":[{\"type\":\"text\",\"text\":\"again\"}]}}";
        string? promptResponse2 = await MethodDispatcher.DispatchAsync(promptRequest2, ct);
        Assert.Equal("end_turn", (string)JObject.Parse(promptResponse2!)["result"]!["stopReason"]!);

        FakeChatClient clientA = clientsByModel["model-a"];
        FakeChatClient clientB = clientsByModel["model-b"];

        // The next turn ran on the new model...
        Assert.Equal(1, clientB.CallCount);
        // ...and the old model's client was never called again.
        Assert.Equal(1, clientA.CallCount);

        // History survived the model swap: model-b's single call sees both the first turn's
        // "hello" and the second turn's "again".
        IReadOnlyList<ChatMessage> secondCallMessages = clientB.ReceivedMessages[0];
        Assert.Contains(secondCallMessages, m => m.Text?.Contains("hello", StringComparison.Ordinal) == true);
        Assert.Contains(secondCallMessages, m => m.Text?.Contains("again", StringComparison.Ordinal) == true);
    }
}
