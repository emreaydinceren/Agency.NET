
using System.Text.Json;
using Agency.Harness.Contexts;
using Agency.Harness.Test.Fakes;

namespace Agency.Harness.Hooks.Tests;
/// <summary>Verifies OnPreToolUse and OnPostToolUse hook behaviour.</summary>
public sealed class AgentToolHookTests
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

    // ── OnPreToolUse — Allow ───────────────────────────────────────────────────

    /// <summary>When <c>OnPreToolUse</c> returns <see cref="PreToolUseDecision.Allow"/>, the requested tool is invoked.</summary>
    [Fact]
    public async Task OnPreToolUse_Allow_ToolIsInvoked()
    {
        var tool = new FakeTool("search");
        var registry = new ToolRegistry([tool]);
        var llm = new FakeChatClient();
        llm.EnqueueResponse(ToolCallResponse("id-1", "search"));
        llm.EnqueueResponse(TextResponse("done"));
        var hooks = new AgentHooks
        {
            OnPreToolUse = (_, _) => Task.FromResult(PreToolUseDecision.Allowed),
        };
        var agent = new Agent(llm, "model", hooks: hooks);
        await RunToCompletion(agent, MakeContext(new ToolContext { Registry = registry }));
        Assert.Equal(1, tool.InvokeCount);
    }

    /// <summary>The <c>OnPreToolUse</c> hook context's <c>ToolName</c> matches the tool the LLM requested.</summary>
    [Fact]
    public async Task OnPreToolUse_Allow_ReceivesCorrectToolName()
    {
        string? capturedName = null;
        var tool = new FakeTool("search");
        var registry = new ToolRegistry([tool]);
        var llm = new FakeChatClient();
        llm.EnqueueResponse(ToolCallResponse("id-1", "search"));
        llm.EnqueueResponse(TextResponse("done"));
        var hooks = new AgentHooks
        {
            OnPreToolUse = (ctx, _) =>
            {
                capturedName = ctx.ToolName;
                return Task.FromResult(PreToolUseDecision.Allowed);
            },
        };
        var agent = new Agent(llm, "model", hooks: hooks);
        await RunToCompletion(agent, MakeContext(new ToolContext { Registry = registry }));
        Assert.Equal("search", capturedName);
    }

    /// <summary>Leaving <c>OnPreToolUse</c> unset (<see langword="null"/>) does not block tool invocation.</summary>
    [Fact]
    public async Task OnPreToolUse_Null_ToolIsInvokedNormally()
    {
        var tool = new FakeTool("search");
        var registry = new ToolRegistry([tool]);
        var llm = new FakeChatClient();
        llm.EnqueueResponse(ToolCallResponse("id-1", "search"));
        llm.EnqueueResponse(TextResponse("done"));
        var agent = new Agent(llm, "model", hooks: new AgentHooks { OnPreToolUse = null });
        await RunToCompletion(agent, MakeContext(new ToolContext { Registry = registry }));
        Assert.Equal(1, tool.InvokeCount);
    }

    // ── OnPreToolUse — Deny ────────────────────────────────────────────────────

    /// <summary>When <c>OnPreToolUse</c> returns <see cref="PreToolUseDecision.Deny"/>, the tool is never invoked.</summary>
    [Fact]
    public async Task OnPreToolUse_Deny_ToolIsNotInvoked()
    {
        var tool = new FakeTool("search");
        var registry = new ToolRegistry([tool]);
        var llm = new FakeChatClient();
        llm.EnqueueResponse(ToolCallResponse("id-1", "search"));
        llm.EnqueueResponse(TextResponse("understood"));
        var hooks = new AgentHooks
        {
            OnPreToolUse = (_, _) => Task.FromResult<PreToolUseDecision>(new PreToolUseDecision.Deny("blocked")),
        };
        var agent = new Agent(llm, "model", hooks: hooks);
        await RunToCompletion(agent, MakeContext(new ToolContext { Registry = registry }));
        Assert.Equal(0, tool.InvokeCount);
    }

    /// <summary>A denied tool call surfaces a <c>"[Blocked]"</c>-tagged tool-role message back to the LLM on the next turn.</summary>
    [Fact]
    public async Task OnPreToolUse_Deny_BlockedResultAppearsInConversation()
    {
        var tool = new FakeTool("search");
        var registry = new ToolRegistry([tool]);
        var llm = new FakeChatClient();
        llm.EnqueueResponse(ToolCallResponse("id-1", "search"));
        llm.EnqueueResponse(TextResponse("understood"));
        var hooks = new AgentHooks
        {
            OnPreToolUse = (_, _) => Task.FromResult<PreToolUseDecision>(new PreToolUseDecision.Deny("not allowed")),
        };
        var agent = new Agent(llm, "model", hooks: hooks);
        await RunToCompletion(agent, MakeContext(new ToolContext { Registry = registry }));
        // The second LLM call must see a Tool-role message containing "[Blocked]"
        IReadOnlyList<ChatMessage> turn2 = llm.ReceivedMessages[1];
        ChatMessage toolMsg = turn2.Last(m => m.Role == ChatRole.Tool);
        FunctionResultContent result = Assert.IsType<FunctionResultContent>(toolMsg.Contents[0]);
        Assert.Contains("[Blocked]", result.Result?.ToString());
    }

    /// <summary>After a tool call is denied, the agent loop continues and still reaches a successful <see cref="AgentResultEvent"/>.</summary>
    [Fact]
    public async Task OnPreToolUse_Deny_AgentLoopContinues()
    {
        var tool = new FakeTool("search");
        var registry = new ToolRegistry([tool]);
        var llm = new FakeChatClient();
        llm.EnqueueResponse(ToolCallResponse("id-1", "search"));
        llm.EnqueueResponse(TextResponse("I understand it was blocked"));
        var hooks = new AgentHooks
        {
            OnPreToolUse = (_, _) => Task.FromResult<PreToolUseDecision>(new PreToolUseDecision.Deny("blocked")),
        };
        var agent = new Agent(llm, "model", hooks: hooks);
        List<AgentEvent> events = await RunToCompletion(agent, MakeContext(new ToolContext { Registry = registry }));
        AgentResultEvent result = Assert.IsType<AgentResultEvent>(events[^1]);
        Assert.Equal(AgentResultStatus.Success, result.Status);
    }

    // ── OnPreToolUse — Rewrite ─────────────────────────────────────────────────

    /// <summary>When <c>OnPreToolUse</c> returns <see cref="PreToolUseDecision.Rewrite"/>, the tool receives the rewritten input rather than the LLM's original arguments.</summary>
    [Fact]
    public async Task OnPreToolUse_Rewrite_ToolReceivesNewInput()
    {
        JsonElement? capturedInput = null;
        var tool = new FakeTool("cmd", input =>
        {
            capturedInput = input;
            return new ToolResult("ok");
        });
        var registry = new ToolRegistry([tool]);
        var llm = new FakeChatClient();
        // LLM requests tool call with original args
        var originalArgs = new Dictionary<string, object?> { ["command"] = "original" };
        var contents = new List<AIContent>
        {
            new FunctionCallContent("id-1", "cmd", originalArgs),
        };
        llm.EnqueueResponse(new ChatResponse([new ChatMessage(ChatRole.Assistant, contents)])
        {
            Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 },
            FinishReason = ChatFinishReason.ToolCalls,
        });
        llm.EnqueueResponse(TextResponse("done"));
        var rewrittenArgs = new Dictionary<string, object?> { ["command"] = "rewritten" };
        JsonElement rewrittenElement = JsonSerializer.SerializeToElement(rewrittenArgs);
        var hooks = new AgentHooks
        {
            OnPreToolUse = (_, _) => Task.FromResult<PreToolUseDecision>(new PreToolUseDecision.Rewrite(rewrittenElement)),
        };
        var agent = new Agent(llm, "model", hooks: hooks);
        await RunToCompletion(agent, MakeContext(new ToolContext { Registry = registry }));
        Assert.NotNull(capturedInput);
        Assert.True(capturedInput.Value.TryGetProperty("command", out JsonElement cmd));
        Assert.Equal("rewritten", cmd.GetString());
    }

    // ── OnPostToolUse ──────────────────────────────────────────────────────────

    /// <summary><c>OnPostToolUse</c> fires exactly once after a tool call completes.</summary>
    [Fact]
    public async Task OnPostToolUse_FiresAfterToolCompletion()
    {
        int count = 0;
        var tool = new FakeTool("t");
        var registry = new ToolRegistry([tool]);
        var llm = new FakeChatClient();
        llm.EnqueueResponse(ToolCallResponse("id-1", "t"));
        llm.EnqueueResponse(TextResponse("done"));
        var hooks = new AgentHooks
        {
            OnPostToolUse = (_, _) => { count++; return Task.CompletedTask; },
        };
        var agent = new Agent(llm, "model", hooks: hooks);
        await RunToCompletion(agent, MakeContext(new ToolContext { Registry = registry }));
        Assert.Equal(1, count);
    }

    /// <summary>The <c>OnPostToolUse</c> hook context carries the invoked tool's name and its result content.</summary>
    [Fact]
    public async Task OnPostToolUse_ReceivesCorrectToolNameAndResult()
    {
        string? capturedName = null;
        string? capturedContent = null;
        var tool = new FakeTool("calc", _ => new ToolResult("42"));
        var registry = new ToolRegistry([tool]);
        var llm = new FakeChatClient();
        llm.EnqueueResponse(ToolCallResponse("id-1", "calc"));
        llm.EnqueueResponse(TextResponse("done"));
        var hooks = new AgentHooks
        {
            OnPostToolUse = (ctx, _) =>
            {
                capturedName = ctx.ToolName;
                capturedContent = ctx.Result.Content;
                return Task.CompletedTask;
            },
        };
        var agent = new Agent(llm, "model", hooks: hooks);
        await RunToCompletion(agent, MakeContext(new ToolContext { Registry = registry }));
        Assert.Equal("calc", capturedName);
        Assert.Equal("42", capturedContent);
    }

    /// <summary><c>OnPostToolUse</c> still fires when the tool throws, and its result context reports <c>IsError</c> as <see langword="true"/>.</summary>
    [Fact]
    public async Task OnPostToolUse_FiresEvenOnToolError()
    {
        bool hookFired = false;
        bool? capturedIsError = null;
        var tool = new FakeTool("broken", _ => throw new InvalidOperationException("exploded"));
        var registry = new ToolRegistry([tool]);
        var llm = new FakeChatClient();
        llm.EnqueueResponse(ToolCallResponse("id-1", "broken"));
        llm.EnqueueResponse(TextResponse("handled the error"));
        var hooks = new AgentHooks
        {
            OnPostToolUse = (ctx, _) =>
            {
                hookFired = true;
                capturedIsError = ctx.Result.IsError;
                return Task.CompletedTask;
            },
        };
        var agent = new Agent(llm, "model", hooks: hooks);
        await RunToCompletion(agent, MakeContext(new ToolContext { Registry = registry }));
        Assert.True(hookFired);
        Assert.True(capturedIsError);
    }
}