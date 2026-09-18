using Agency.Acp.Sessions;
using Agency.Acp.Test.Fakes;
using Agency.Harness;
using Agency.Llm.Common;
using dotacp.protocol;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Agency.Acp.Test.Sessions;

/// <summary>
/// Behavioral tests for <see cref="SessionFactory"/>'s session-creation algorithm (spec §8.1):
/// every failure mode it is required to degrade through rather than fail on.
/// </summary>
public sealed class SessionCreationTests
{
    private static ChatResponse TextResponse(string text) =>
        new([new ChatMessage(ChatRole.Assistant, text)]) { FinishReason = ChatFinishReason.Stop };

    private static SessionFactory BuildFactory(
        AgentOptions processOptions,
        Func<CancellationToken, Task<IReadOnlyList<Model>>> catalogueFetcher)
    {
        var services = new ServiceCollection();
        services.AddScoped<IAgentFactory>(_ => new FakeAgentFactory((_, model) =>
        {
            var client = new FakeChatClient();
            client.EnqueueResponse(TextResponse($"hi from {model}"));
            return new Agent(client, model ?? "unknown");
        }));
        ServiceProvider provider = services.BuildServiceProvider();

        return new SessionFactory(provider.GetRequiredService<IServiceScopeFactory>(), processOptions, catalogueFetcher);
    }

    /// <summary>(b) An unreachable MCP server yields a session with fewer tools, recorded in <c>FailedServers</c>, never thrown.</summary>
    [Fact]
    public async Task CreateAsync_UnreachableMcpServer_SucceedsWithFailureRecorded()
    {
        var processOptions = new AgentOptions { DefaultModel = "m", DefaultClientName = "c" };
        SessionFactory factory = BuildFactory(processOptions, _ => Task.FromResult<IReadOnlyList<Model>>([]));

        var request = new NewSessionRequest
        {
            Cwd = "/a",
            McpServers =
            [
                new McpServerHttp { Name = "unreachable", Url = "http://127.0.0.1:1/mcp" },
            ],
        };

        (SessionState state, _) = await factory.CreateAsync(request, null, TestContext.Current.CancellationToken);

        Assert.Empty(state.McpPool.Tools);
        Assert.True(state.McpPool.FailedServers.ContainsKey("unreachable"));
    }

    /// <summary>(c) A catalogue fetch that throws yields an empty catalogue and a live session.</summary>
    [Fact]
    public async Task CreateAsync_CatalogueFetchThrows_YieldsEmptyCatalogueAndLiveSession()
    {
        var processOptions = new AgentOptions { DefaultModel = "m", DefaultClientName = "c" };
        SessionFactory factory = BuildFactory(processOptions, _ => throw new InvalidOperationException("provider unreachable"));

        (SessionState state, NewSessionResponse response) = await factory.CreateAsync(
            new NewSessionRequest { Cwd = "/a" }, null, TestContext.Current.CancellationToken);

        Assert.Equal("m", state.ModelId);
        SessionConfigSelect modelOption = Assert.IsType<SessionConfigSelect>(
            Assert.Single(response.ConfigOptions, o => (string)o.Id == "model"));
        Assert.True(modelOption.Options.TryGetSessionConfigSelectOption(out SessionConfigSelectOption[]? options));
        Assert.Empty(options!);
    }

    /// <summary>(d) Models with <c>Kind == Embedding</c> are excluded; <c>Kind == null</c> models are retained (spec P3).</summary>
    [Fact]
    public async Task CreateAsync_FiltersEmbeddingModels_ButRetainsUnknownKind()
    {
        var processOptions = new AgentOptions { DefaultModel = "chat-model", DefaultClientName = "c" };
        IReadOnlyList<Model> catalogue =
        [
            new Model("chat-model", "Chat") { Kind = ModelKind.Chat },
            new Model("embed-model", "Embed") { Kind = ModelKind.Embedding },
            new Model("unknown-model", "Unknown"), // Kind == null
        ];
        SessionFactory factory = BuildFactory(processOptions, _ => Task.FromResult(catalogue));

        (_, NewSessionResponse response) = await factory.CreateAsync(
            new NewSessionRequest { Cwd = "/a" }, null, TestContext.Current.CancellationToken);

        SessionConfigSelect modelOption = Assert.IsType<SessionConfigSelect>(
            Assert.Single(response.ConfigOptions, o => (string)o.Id == "model"));
        Assert.True(modelOption.Options.TryGetSessionConfigSelectOption(out SessionConfigSelectOption[]? options));
        var values = options!.Select(o => (string)o.Value).ToArray();

        Assert.Contains("chat-model", values);
        Assert.Contains("unknown-model", values);
        Assert.DoesNotContain("embed-model", values);
    }
}
