
using Agency.Agentic.Hooks;

namespace Agency.Agentic.Hooks.Tests;
/// <summary>Verifies AgentHooks default state and init-only delegate assignment.</summary>
public sealed class AgentHooksTests
{
    [Fact]
    public void None_IsNotNull()
    {
        Assert.NotNull(AgentHooks.None);
    }

    [Fact]
    public void None_AllDelegatesAreNull()
    {
        AgentHooks hooks = AgentHooks.None;
        Assert.Null(hooks.OnSessionStarted);
        Assert.Null(hooks.OnPreToolUse);
        Assert.Null(hooks.OnPostToolUse);
        Assert.Null(hooks.OnAssistantTurn);
        Assert.Null(hooks.OnStop);
    }

    [Fact]
    public void InitSyntax_AssignsOnSessionStarted()
    {
        var hooks = new AgentHooks
        {
            OnSessionStarted = (_, _) => Task.CompletedTask,
        };
        Assert.NotNull(hooks.OnSessionStarted);
    }

    [Fact]
    public void InitSyntax_AssignsOnPreToolUse()
    {
        var hooks = new AgentHooks
        {
            OnPreToolUse = (_, _) => Task.FromResult(PreToolUseDecision.Allowed),
        };
        Assert.NotNull(hooks.OnPreToolUse);
    }

    [Fact]
    public void None_IsSameReferenceEveryTime()
    {
        AgentHooks first = AgentHooks.None;
        AgentHooks second = AgentHooks.None;
        Assert.Same(first, second);
    }
}