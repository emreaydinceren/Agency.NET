using Agency.Acp.Dispatch;
using Agency.Acp.Sessions;
using Agency.Acp.Test.Fakes;
using Agency.Harness;
using Agency.Llm.Common;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;

namespace Agency.Acp.Test.Sessions;

/// <summary>
/// Behavioral tests proving the hop that was missing (spec §1.2): a <c>session/new</c> whose
/// params carry <c>_meta.systemPrompt</c> must produce a session whose submitted system prompt
/// carries that identity — driven through the dispatcher, not the parser (spec §6.2, §8.1).
/// </summary>
public sealed class SessionIdentityTests
{
    private const string DefaultIdentityLine = "You are an autonomous agent operating inside the Agency runtime.";

    private static ChatResponse TextResponse(string text) =>
        new([new ChatMessage(ChatRole.Assistant, text)])
        {
            Usage = new UsageDetails { InputTokenCount = 5, OutputTokenCount = 3 },
            FinishReason = ChatFinishReason.Stop,
        };

    /// <summary>
    /// Wires a real <see cref="SessionFactory"/> backed by a <see cref="FakeAgentFactory"/> and
    /// registers it with <see cref="MethodDispatcher"/>, so tests can drive real
    /// <c>session/new</c>/<c>session/prompt</c> dispatch. Every session's <see cref="Agent"/> is
    /// served by its own <see cref="FakeChatClient"/>, collected into <paramref name="clients"/> in
    /// creation order so a test can inspect exactly what each session submitted.
    /// </summary>
    private static void ConfigureDispatcher(List<FakeChatClient> clients)
    {
        var services = new ServiceCollection();
        services.AddScoped<IAgentFactory>(_ => new FakeAgentFactory((_, model) =>
        {
            var client = new FakeChatClient();
            client.EnqueueResponse(TextResponse($"hi from {model}"));
            clients.Add(client);
            return new Agent(client, model ?? "model-a");
        }));
        ServiceProvider provider = services.BuildServiceProvider();

        var processOptions = new AgentOptions { DefaultModel = "model-a", DefaultClientName = "c" };
        var sessionFactory = new SessionFactory(
            provider.GetRequiredService<IServiceScopeFactory>(),
            processOptions,
            _ => Task.FromResult<IReadOnlyList<Model>>([new Model("model-a", "A")]));

        MethodDispatcher.Configure(new SessionRegistry(), sessionFactory);
        MethodDispatcher.ConfigureClient(new FakeAcpClientProxy());
    }

    private static async Task<string> CreateSessionAsync(string? metaJson, CancellationToken ct)
    {
        string paramsJson = metaJson is null
            ? """{"cwd":"/a"}"""
            : "{\"cwd\":\"/a\",\"_meta\":" + metaJson + "}";
        string request = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"session/new\",\"params\":" + paramsJson + "}";

        string? response = await MethodDispatcher.DispatchAsync(request, ct);
        Assert.NotNull(response);
        JObject envelope = JObject.Parse(response!);
        Assert.Null(envelope["error"]);

        return (string)envelope["result"]!["sessionId"]!;
    }

    private static async Task DrivePromptAsync(string sessionId, CancellationToken ct)
    {
        string request = "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"session/prompt\",\"params\":{\"sessionId\":\"" + sessionId + "\",\"prompt\":[{\"type\":\"text\",\"text\":\"hi\"}]}}";
        string? response = await MethodDispatcher.DispatchAsync(request, ct);
        Assert.NotNull(response);
        Assert.Null(JObject.Parse(response!)["error"]);
    }

    /// <summary>
    /// Wires a dispatcher backed by a catalogue of <paramref name="modelIds"/> (default model is
    /// the first), so a test can drive <c>session/set_config_option</c> to switch models. Otherwise
    /// identical to <see cref="ConfigureDispatcher"/>.
    /// </summary>
    private static void ConfigureDispatcherWithModels(List<FakeChatClient> clients, params string[] modelIds)
    {
        var services = new ServiceCollection();
        services.AddScoped<IAgentFactory>(_ => new FakeAgentFactory((_, model) =>
        {
            string modelName = model ?? modelIds[0];
            var client = new FakeChatClient();
            client.EnqueueResponse(TextResponse($"hi from {modelName}"));
            clients.Add(client);
            return new Agent(client, modelName);
        }));
        ServiceProvider provider = services.BuildServiceProvider();

        var processOptions = new AgentOptions { DefaultModel = modelIds[0], DefaultClientName = "c" };
        var sessionFactory = new SessionFactory(
            provider.GetRequiredService<IServiceScopeFactory>(),
            processOptions,
            _ => Task.FromResult<IReadOnlyList<Model>>(modelIds.Select(id => new Model(id, id)).ToList()));

        MethodDispatcher.Configure(new SessionRegistry(), sessionFactory);
        MethodDispatcher.ConfigureClient(new FakeAcpClientProxy());
    }

    /// <summary>
    /// (a) <c>_meta.systemPrompt = {"append": "…"}</c> reaches the model: the submitted system
    /// prompt carries the identity and the default identity line does not appear. Asserted on the
    /// submitted prompt, not on <c>SessionState</c> — spec §15 requires observable behaviour, and a
    /// <c>SessionState</c> assertion would pass even if the value never reached the model, which is
    /// exactly the defect being fixed.
    /// </summary>
    [Fact]
    public async Task SessionNew_WithMetaSystemPrompt_IdentityReachesSubmittedPrompt()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var clients = new List<FakeChatClient>();
        ConfigureDispatcher(clients);

        string sessionId = await CreateSessionAsync(
            """{"systemPrompt":{"append":"You are Ana, who routes."}}""", ct);
        await DrivePromptAsync(sessionId, ct);

        string submittedPrompt = Assert.Single(clients[0].ReceivedSystemPrompts);
        Assert.Contains("You are Ana, who routes.", submittedPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain(DefaultIdentityLine, submittedPrompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// (b) <c>_meta</c> absent entirely: the default identity line appears and session creation
    /// still succeeds (spec §12 E-1 — <c>session/new</c> stays fail-soft).
    /// </summary>
    [Fact]
    public async Task SessionNew_WithoutMeta_DefaultIdentityAppearsAndSessionSucceeds()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var clients = new List<FakeChatClient>();
        ConfigureDispatcher(clients);

        string sessionId = await CreateSessionAsync(metaJson: null, ct);
        await DrivePromptAsync(sessionId, ct);

        string submittedPrompt = Assert.Single(clients[0].ReceivedSystemPrompts);
        Assert.Contains(DefaultIdentityLine, submittedPrompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>_meta.model</c> is no longer read (spec §6.2, §12 E-14, §14.5 G-2): a client sending it
    /// has no effect, and the session starts on <see cref="AgentOptions.DefaultModel"/> regardless.
    /// Huddle confirmed they never send this field and select a model afterwards via
    /// <c>session/set_config_option</c> — the sole model-selection path.
    /// </summary>
    [Fact]
    public async Task SessionNew_WithMetaModel_IsIgnored_SessionStartsOnDefaultModel()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var clients = new List<FakeChatClient>();
        ConfigureDispatcher(clients);

        string sessionId = await CreateSessionAsync("""{"model":"some/other-model"}""", ct);

        SessionState state = MethodDispatcher.Sessions.GetRequired(sessionId);
        Assert.Equal("model-a", state.ModelId);
    }

    /// <summary>
    /// Spec O-3, §7.3, §12 E-7 — the milestone case: two sessions on one dispatcher, each carrying
    /// its own <c>_meta.systemPrompt</c>, must not bleed. Each session's submitted system prompt
    /// carries its own identity and never the other's. No paired implementation: this asserts the
    /// immutability described in §7.3 — two <c>Context</c> instances, each with its own
    /// <c>init</c>-only <c>QueryContext</c>, share no mutable identity state.
    /// </summary>
    [Fact]
    public async Task SessionNew_TwoSessionsWithDifferentIdentities_DoNotBleed()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var clients = new List<FakeChatClient>();
        ConfigureDispatcher(clients);

        string anaSessionId = await CreateSessionAsync(
            """{"systemPrompt":{"append":"You are Ana, who routes."}}""", ct);
        string kaiSessionId = await CreateSessionAsync(
            """{"systemPrompt":{"append":"You are Kai, who writes code."}}""", ct);

        await DrivePromptAsync(anaSessionId, ct);
        await DrivePromptAsync(kaiSessionId, ct);

        string anaPrompt = Assert.Single(clients[0].ReceivedSystemPrompts);
        string kaiPrompt = Assert.Single(clients[1].ReceivedSystemPrompts);

        Assert.Contains("You are Ana, who routes.", anaPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("You are Kai, who writes code.", anaPrompt, StringComparison.Ordinal);

        Assert.Contains("You are Kai, who writes code.", kaiPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("You are Ana, who routes.", kaiPrompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// Spec §12 E-8, §7.2 — identity survives <c>session/set_config_option</c> changing the model,
    /// because <see cref="ChatSession.SetAgent"/> preserves the existing <c>Context</c> (and
    /// therefore <c>Context.Query.IdentityPrompt</c>); only the <see cref="Agent"/> driving
    /// subsequent turns is swapped. No paired implementation: this asserts existing
    /// <c>SetAgent</c> behaviour.
    /// </summary>
    [Fact]
    public async Task SessionNew_IdentitySurvivesModelChange()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var clients = new List<FakeChatClient>();
        ConfigureDispatcherWithModels(clients, "model-a", "model-b");

        string sessionId = await CreateSessionAsync(
            """{"systemPrompt":{"append":"You are Ana, who routes."}}""", ct);
        await DrivePromptAsync(sessionId, ct);

        string setConfigRequest = "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"session/set_config_option\",\"params\":{\"sessionId\":\"" + sessionId + "\",\"configId\":\"model\",\"type\":\"select\",\"value\":\"model-b\"}}";
        string? setConfigResponse = await MethodDispatcher.DispatchAsync(setConfigRequest, ct);
        Assert.Null(JObject.Parse(setConfigResponse!)["error"]);

        await DrivePromptAsync(sessionId, ct);

        Assert.Equal(2, clients.Count);
        string submittedPrompt = Assert.Single(clients[1].ReceivedSystemPrompts);
        Assert.Contains("You are Ana, who routes.", submittedPrompt, StringComparison.Ordinal);
    }
}
