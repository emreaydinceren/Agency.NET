
using Agency.Agentic.Contexts;
using Agency.Agentic.Hooks;
using Agency.Agentic.Test.Fakes;

namespace Agency.Agentic.Hooks.Tests;
/// <summary>Verifies OnSessionStarted hook fires exactly once per agent run.</summary>
public sealed class AgentOnSessionStartedHookTests
{
    private static Context MakeContext(string prompt = "Hello", ToolContext? tools = null) =>
        new()
        {
            Query = new QueryContext { Prompt = prompt },
            Tools = tools ?? ToolContext.Empty,
        };

    private static ChatResponse TextResponse(string text) =>
        new([new ChatMessage(ChatRole.Assistant, text)])
        {
            Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 },
            FinishReason = ChatFinishReason.Stop,
        };

    private static ChatResponse ToolCallResponse(string id, string name)
    {
        var contents = new List<AIContent> { new FunctionCallContent(id, name) };
        return new ChatResponse([new ChatMessage(ChatRole.Assistant, contents)])
        {
            Usage = new UsageDetails { InputTokenCount = 20, OutputTokenCount = 10 },
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
    public async Task OnSessionStarted_FiresOnce_ForSingleTurnRun()
    {
        int count = 0;
        var llm = new FakeChatClient();
        llm.EnqueueResponse(TextResponse("done"));
        var hooks = new AgentHooks
        {
            OnSessionStarted = (_, _) => { count++; return Task.CompletedTask; },
        };
        var agent = new Agent(llm, "model", hooks: hooks);
        await RunToCompletion(agent, MakeContext());
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task OnSessionStarted_FiresOnce_ForMultiTurnToolCallRun()
    {
        int count = 0;
        var tool = new FakeTool("t");
        var registry = new ToolRegistry([tool]);
        var llm = new FakeChatClient();
        llm.EnqueueResponse(ToolCallResponse("id-1", "t"));
        llm.EnqueueResponse(TextResponse("done"));
        var hooks = new AgentHooks
        {
            OnSessionStarted = (_, _) => { count++; return Task.CompletedTask; },
        };
        var agent = new Agent(llm, "model", hooks: hooks);
        await RunToCompletion(agent, MakeContext(tools: new ToolContext { Registry = registry }));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task OnSessionStarted_ReceivesNonEmptySessionId()
    {
        string? capturedId = null;
        var llm = new FakeChatClient();
        llm.EnqueueResponse(TextResponse("done"));
        var hooks = new AgentHooks
        {
            OnSessionStarted = (ctx, _) => { capturedId = ctx.SessionId; return Task.CompletedTask; },
        };
        var agent = new Agent(llm, "model", hooks: hooks);
        await RunToCompletion(agent, MakeContext());
        Assert.NotNull(capturedId);
        Assert.NotEmpty(capturedId);
    }

    [Fact]
    public async Task OnSessionStarted_ReceivesCorrectAgentContext()
    {
        Context? capturedCtx = null;
        var llm = new FakeChatClient();
        llm.EnqueueResponse(TextResponse("done"));
        var hooks = new AgentHooks
        {
            OnSessionStarted = (ctx, _) => { capturedCtx = ctx.AgentContext; return Task.CompletedTask; },
        };
        var agent = new Agent(llm, "model", hooks: hooks);
        Context agentCtx = MakeContext();
        await RunToCompletion(agent, agentCtx);
        Assert.Same(agentCtx, capturedCtx);
    }

    [Fact]
    public async Task OnSessionStarted_Null_DoesNotThrow()
    {
        var llm = new FakeChatClient();
        llm.EnqueueResponse(TextResponse("done"));
        var agent = new Agent(llm, "model", hooks: new AgentHooks { OnSessionStarted = null });
        Exception? ex = await Record.ExceptionAsync(() => RunToCompletion(agent, MakeContext()));
        Assert.Null(ex);
    }
}