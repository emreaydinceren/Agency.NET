namespace Agency.Agentic.Hooks.Tests;

using Agency.Agentic.Contexts;
using Agency.Agentic.Hooks;
using Agency.Agentic.Test.Fakes;

/// <summary>Verifies that Agent accepts AgentHooks? without breaking the existing API.</summary>
public sealed class AgentHooksConstructorTests
{
    private static FakeChatClient MakeLlm()
    {
        var llm = new FakeChatClient();
        llm.EnqueueResponse(new ChatResponse([new ChatMessage(ChatRole.Assistant, "done")])
        {
            Usage = new UsageDetails { InputTokenCount = 5, OutputTokenCount = 5 },
            FinishReason = ChatFinishReason.Stop,
        });
        return llm;
    }

    private static Context MakeContext() =>
        new() { Query = new QueryContext { Prompt = "test" } };

    [Fact]
    public void Agent_ConstructedWithNullHooks_DoesNotThrow()
    {
        FakeChatClient llm = MakeLlm();
        var ex = Record.Exception(() => new Agent(llm, "model", hooks: null));
        Assert.Null(ex);
    }

    [Fact]
    public void Agent_ConstructedWithNoneHooks_DoesNotThrow()
    {
        FakeChatClient llm = MakeLlm();
        var ex = Record.Exception(() => new Agent(llm, "model", hooks: AgentHooks.None));
        Assert.Null(ex);
    }

    [Fact]
    public void Agent_ConstructedWithHooks_DoesNotThrow()
    {
        FakeChatClient llm = MakeLlm();
        var hooks = new AgentHooks { OnStop = (_, _) => Task.CompletedTask };
        var ex = Record.Exception(() => new Agent(llm, "model", hooks: hooks));
        Assert.Null(ex);
    }

    [Fact]
    public async Task Agent_ConstructedWithoutHooksParam_RunsNormally()
    {
        FakeChatClient llm = MakeLlm();
        var agent = new Agent(llm, "model");
        var events = new List<AgentEvent>();
        await foreach (AgentEvent evt in agent.RunAsync(MakeContext(), TestContext.Current.CancellationToken))
        {
            events.Add(evt);
        }
        AgentResultEvent result = Assert.IsType<AgentResultEvent>(events[^1]);
        Assert.Equal(AgentResultStatus.Success, result.Status);
    }
}