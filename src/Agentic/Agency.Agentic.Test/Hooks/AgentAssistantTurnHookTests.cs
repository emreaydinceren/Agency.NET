namespace Agency.Agentic.Hooks.Tests;

using Agency.Agentic.Contexts;
using Agency.Agentic.Hooks;
using Agency.Agentic.Test.Fakes;

/// <summary>Verifies OnAssistantTurn hook fires after each LLM response.</summary>
public sealed class AgentAssistantTurnHookTests
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
    public async Task OnAssistantTurn_FiresOnce_ForSingleTurnRun()
    {
        int count = 0;
        var llm = new FakeChatClient();
        llm.EnqueueResponse(TextResponse("hello"));
        var hooks = new AgentHooks
        {
            OnAssistantTurn = (_, _) => { count++; return Task.CompletedTask; },
        };
        var agent = new Agent(llm, "model", hooks: hooks);
        await RunToCompletion(agent, MakeContext());
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task OnAssistantTurn_FiresTwice_ForTwoTurnRun()
    {
        int count = 0;
        var tool = new FakeTool("t");
        var registry = new ToolRegistry([tool]);
        var llm = new FakeChatClient();
        llm.EnqueueResponse(ToolCallResponse("id-1", "t"));
        llm.EnqueueResponse(TextResponse("done"));
        var hooks = new AgentHooks
        {
            OnAssistantTurn = (_, _) => { count++; return Task.CompletedTask; },
        };
        var agent = new Agent(llm, "model", hooks: hooks);
        await RunToCompletion(agent, MakeContext(new ToolContext { Registry = registry }));
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task OnAssistantTurn_ReceivesAssistantMessage()
    {
        ChatRole? capturedRole = null;
        var llm = new FakeChatClient();
        llm.EnqueueResponse(TextResponse("hello"));
        var hooks = new AgentHooks
        {
            OnAssistantTurn = (ctx, _) => { capturedRole = ctx.Message.Role; return Task.CompletedTask; },
        };
        var agent = new Agent(llm, "model", hooks: hooks);
        await RunToCompletion(agent, MakeContext());
        Assert.Equal(ChatRole.Assistant, capturedRole);
    }

    [Fact]
    public async Task OnAssistantTurn_ReceivesMessageWithCorrectText()
    {
        string? capturedText = null;
        var llm = new FakeChatClient();
        llm.EnqueueResponse(TextResponse("expected content"));
        var hooks = new AgentHooks
        {
            OnAssistantTurn = (ctx, _) =>
            {
                capturedText = string.Concat(ctx.Message.Contents.OfType<TextContent>().Select(t => t.Text));
                return Task.CompletedTask;
            },
        };
        var agent = new Agent(llm, "model", hooks: hooks);
        await RunToCompletion(agent, MakeContext());
        Assert.Equal("expected content", capturedText);
    }

    [Fact]
    public async Task OnAssistantTurn_Null_DoesNotThrow()
    {
        var llm = new FakeChatClient();
        llm.EnqueueResponse(TextResponse("done"));
        var agent = new Agent(llm, "model", hooks: new AgentHooks { OnAssistantTurn = null });
        Exception? ex = await Record.ExceptionAsync(async () => await RunToCompletion(agent, MakeContext()));
        Assert.Null(ex);
    }
}