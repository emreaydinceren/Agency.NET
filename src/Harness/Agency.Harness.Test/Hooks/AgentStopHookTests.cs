
using Agency.Harness.Contexts;
using Agency.Harness.Test.Fakes;

namespace Agency.Harness.Hooks.Tests;
/// <summary>Verifies OnStop hook fires before AgentResultEvent is yielded.</summary>
public sealed class AgentStopHookTests
{
    private static Context MakeContext(ToolContext? tools = null) =>
        new() { Query = new QueryContext { Prompt = "test" }, Tools = tools ?? ToolContext.Empty };

    private static ChatResponse TextResponse(string text) =>
        new([new ChatMessage(ChatRole.Assistant, text)])
        {
            Usage = new UsageDetails { InputTokenCount = 5, OutputTokenCount = 5 },
            FinishReason = ChatFinishReason.Stop,
        };

    private static ChatResponse ToolCallResponse(string id, string name)
    {
        var contents = new List<AIContent> { new FunctionCallContent(id, name) };
        return new ChatResponse([new ChatMessage(ChatRole.Assistant, contents)])
        {
            Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 },
            FinishReason = ChatFinishReason.ToolCalls,
        };
    }

    private static async Task<List<AgentEvent>> RunToCompletion(Agent agent, Context ctx)
    {
        var events = new List<AgentEvent>();
        await foreach (AgentEvent evt in agent.RunAsync(ctx, TestContext.Current.CancellationToken))
        {
            events.Add(evt);
        }
        return events;
    }

    [Fact]
    public async Task OnStop_FiresOnce_ForNormalRun()
    {
        int count = 0;
        var llm = new FakeChatClient();
        llm.EnqueueResponse(TextResponse("done"));
        var hooks = new AgentHooks
        {
            OnStop = (_, _) => { count++; return Task.CompletedTask; },
        };
        var agent = new Agent(llm, "model", hooks: hooks);
        await RunToCompletion(agent, MakeContext());
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task OnStop_ReceivesAgentResultEvent()
    {
        AgentResultEvent? captured = null;
        var llm = new FakeChatClient();
        llm.EnqueueResponse(TextResponse("done"));
        var hooks = new AgentHooks
        {
            OnStop = (ctx, _) => { captured = ctx.Result; return Task.CompletedTask; },
        };
        var agent = new Agent(llm, "model", hooks: hooks);
        await RunToCompletion(agent, MakeContext());
        Assert.NotNull(captured);
    }

    [Fact]
    public async Task OnStop_ReceivesCorrectStatus_ForSuccessRun()
    {
        AgentResultStatus? capturedStatus = null;
        var llm = new FakeChatClient();
        llm.EnqueueResponse(TextResponse("done"));
        var hooks = new AgentHooks
        {
            OnStop = (ctx, _) => { capturedStatus = ctx.Result.Status; return Task.CompletedTask; },
        };
        var agent = new Agent(llm, "model", hooks: hooks);
        await RunToCompletion(agent, MakeContext());
        Assert.Equal(AgentResultStatus.Success, capturedStatus);
    }

    [Fact]
    public async Task OnStop_FiresForMaxStepsReached()
    {
        AgentResultStatus? capturedStatus = null;
        var tool = new FakeTool("looper");
        var registry = new ToolRegistry([tool]);
        var llm = new FakeChatClient();
        llm.EnqueueResponse(ToolCallResponse("id-1", "looper"));
        llm.EnqueueResponse(ToolCallResponse("id-2", "looper"));
        var hooks = new AgentHooks
        {
            OnStop = (ctx, _) => { capturedStatus = ctx.Result.Status; return Task.CompletedTask; },
        };
        var agent = new Agent(llm, "model",
            stopWhen: StopConditions.StepCountIs(2),
            hooks: hooks);
        await RunToCompletion(agent, MakeContext(new ToolContext { Registry = registry }));
        Assert.Equal(AgentResultStatus.MaxStepsReached, capturedStatus);
    }

    [Fact]
    public async Task OnStop_Null_DoesNotThrow()
    {
        var llm = new FakeChatClient();
        llm.EnqueueResponse(TextResponse("done"));
        var agent = new Agent(llm, "model", hooks: new AgentHooks { OnStop = null });
        Exception? ex = await Record.ExceptionAsync(async () => await RunToCompletion(agent, MakeContext()));
        Assert.Null(ex);
    }

    [Fact]
    public async Task OnStop_FiresBefore_AgentResultEventIsYielded()
    {
        // The hook must fire BEFORE the AgentResultEvent appears in the stream.
        var log = new List<string>();
        var llm = new FakeChatClient();
        llm.EnqueueResponse(TextResponse("done"));
        var hooks = new AgentHooks
        {
            OnStop = (_, _) => { log.Add("hook"); return Task.CompletedTask; },
        };
        var agent = new Agent(llm, "model", hooks: hooks);
        await foreach (AgentEvent evt in agent.RunAsync(MakeContext(), TestContext.Current.CancellationToken))
        {
            if (evt is AgentResultEvent)
            {
                log.Add("event");
            }
        }
        Assert.Equal(2, log.Count);
        Assert.Equal("hook", log[0]);
        Assert.Equal("event", log[1]);
    }
}