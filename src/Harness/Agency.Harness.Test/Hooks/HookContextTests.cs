
using System.Text.Json;
using Agency.Harness.Contexts;

namespace Agency.Harness.Hooks.Tests;
/// <summary>Verifies that hook context records round-trip their constructor arguments.</summary>
public sealed class HookContextTests
{
    private static Context MakeContext() =>
        new() { Query = new QueryContext { Prompt = "test" } };

    private static JsonElement MakeElement() =>
        JsonSerializer.SerializeToElement(new Dictionary<string, object?>());

    /// <summary><see cref="SessionStartedHookContext"/> exposes the session id and agent context it was constructed with.</summary>
    [Fact]
    public void SessionStartedHookContext_Properties()
    {
        Context ctx = MakeContext();
        var sut = new SessionStartedHookContext("sid-1", ctx);
        Assert.Equal("sid-1", sut.SessionId);
        Assert.Same(ctx, sut.AgentContext);
    }

    /// <summary><see cref="PreToolUseHookContext"/> exposes the tool name, input, and agent context it was constructed with.</summary>
    [Fact]
    public void PreToolUseHookContext_Properties()
    {
        Context ctx = MakeContext();
        JsonElement el = MakeElement();
        var sut = new PreToolUseHookContext("search", el, ctx);
        Assert.Equal("search", sut.ToolName);
        Assert.Equal(el.GetRawText(), sut.Input.GetRawText());
        Assert.Same(ctx, sut.AgentContext);
    }

    /// <summary><see cref="PostToolUseHookContext"/> exposes the tool name, input, result, and agent context it was constructed with.</summary>
    [Fact]
    public void PostToolUseHookContext_Properties()
    {
        Context ctx = MakeContext();
        JsonElement el = MakeElement();
        var result = new ToolResult("ok", false);
        var sut = new PostToolUseHookContext("search", el, result, ctx);
        Assert.Equal("search", sut.ToolName);
        Assert.Equal(el.GetRawText(), sut.Input.GetRawText());
        Assert.Equal("ok", sut.Result.Content);
        Assert.False(sut.Result.IsError);
        Assert.Same(ctx, sut.AgentContext);
    }

    /// <summary><see cref="AssistantTurnHookContext"/> exposes the assistant message and agent context it was constructed with.</summary>
    [Fact]
    public void AssistantTurnHookContext_Properties()
    {
        Context ctx = MakeContext();
        var msg = new ChatMessage(ChatRole.Assistant, "hello");
        var sut = new AssistantTurnHookContext(msg, ctx);
        Assert.Same(msg, sut.Message);
        Assert.Same(ctx, sut.AgentContext);
    }

    /// <summary><see cref="StopHookContext"/> exposes the run result and agent context it was constructed with.</summary>
    [Fact]
    public void StopHookContext_Properties()
    {
        Context ctx = MakeContext();
        var resultEvent = new AgentResultEvent(
            AgentResultStatus.Success, "done",
            new LlmTokenUsage(10, 5), 0.001m);
        var sut = new StopHookContext(resultEvent, ctx);
        Assert.Same(resultEvent, sut.Result);
        Assert.Same(ctx, sut.AgentContext);
    }

    /// <summary><see cref="SessionEndedHookContext"/> exposes the session id and agent context it was constructed with.</summary>
    [Fact]
    public void SessionEndedHookContext_Properties()
    {
        Context ctx = MakeContext();
        var sut = new SessionEndedHookContext("session-99", ctx);
        Assert.Equal("session-99", sut.SessionId);
        Assert.Same(ctx, sut.AgentContext);
    }
}