
using Agency.Harness.Contexts;
using Agency.Harness.Test.Fakes;

namespace Agency.Harness.Hooks.Tests;
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

    /// <summary>A single-turn run fires <c>OnSessionStarted</c> exactly once.</summary>
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

    /// <summary>A run spanning multiple turns due to a tool call still fires <c>OnSessionStarted</c> only once, at session start.</summary>
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

    /// <summary>The hook context's <c>SessionId</c> is populated with a non-empty value.</summary>
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

    /// <summary>The hook context's <c>AgentContext</c> is the same <see cref="Context"/> instance passed into the run.</summary>
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

    /// <summary>Leaving <c>OnSessionStarted</c> unset (<see langword="null"/>) does not throw during a run.</summary>
    [Fact]
    public async Task OnSessionStarted_Null_DoesNotThrow()
    {
        var llm = new FakeChatClient();
        llm.EnqueueResponse(TextResponse("done"));
        var agent = new Agent(llm, "model", hooks: new AgentHooks { OnSessionStarted = null });
        Exception? ex = await Record.ExceptionAsync(() => RunToCompletion(agent, MakeContext()));
        Assert.Null(ex);
    }

    /// <summary>
    /// Verifies the documented pattern: an <c>OnSessionStarted</c> hook can append a fact to
    /// <see cref="Context.Knowledge"/>, and that fact reaches the system prompt the LLM receives.
    /// </summary>
    [Fact]
    public async Task OnSessionStarted_CanAppendKnowledgeFact_ReachesSystemPrompt()
    {
        const string fact = "Today's sprint: auth hardening";
        var llm = new FakeChatClient();
        llm.EnqueueResponse(TextResponse("done"));
        var hooks = new AgentHooks
        {
            OnSessionStarted = (ctx, _) =>
            {
                ctx.AgentContext.Knowledge = ctx.AgentContext.Knowledge with
                {
                    Facts = [.. ctx.AgentContext.Knowledge.Facts, fact],
                };
                return Task.CompletedTask;
            },
        };
        var agent = new Agent(llm, "model", hooks: hooks);
        await RunToCompletion(agent, MakeContext());
        Assert.Contains(llm.ReceivedSystemPrompts, p => p.Contains(fact));
    }

    /// <summary>
    /// Verifies a fact added at session start is re-injected on every iteration, not just the first —
    /// the "always fresh" guarantee of the per-iteration system prompt rebuild.
    /// </summary>
    [Fact]
    public async Task OnSessionStarted_AppendedFact_ReInjectedEveryIteration()
    {
        const string fact = "Domain rule: never expose internal IDs";
        var tool = new FakeTool("t");
        var registry = new ToolRegistry([tool]);
        var llm = new FakeChatClient();
        llm.EnqueueResponse(ToolCallResponse("id-1", "t"));
        llm.EnqueueResponse(TextResponse("done"));
        var hooks = new AgentHooks
        {
            OnSessionStarted = (ctx, _) =>
            {
                ctx.AgentContext.Knowledge = ctx.AgentContext.Knowledge with
                {
                    Facts = [.. ctx.AgentContext.Knowledge.Facts, fact],
                };
                return Task.CompletedTask;
            },
        };
        var agent = new Agent(llm, "model", hooks: hooks);
        await RunToCompletion(agent, MakeContext(tools: new ToolContext { Registry = registry }));
        Assert.Equal(2, llm.ReceivedSystemPrompts.Count);
        Assert.All(llm.ReceivedSystemPrompts, p => Assert.Contains(fact, p));
    }
}