namespace Agency.Harness.Hooks.Tests;
/// <summary>Verifies AgentHooks default state and init-only delegate assignment.</summary>
public sealed class AgentHooksTests
{
    /// <summary><see cref="AgentHooks.None"/> returns a non-<see langword="null"/> instance.</summary>
    [Fact]
    public void None_IsNotNull()
    {
        Assert.NotNull(AgentHooks.None);
    }

    /// <summary>Every hook delegate on <see cref="AgentHooks.None"/> is <see langword="null"/>.</summary>
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

    /// <summary>Object-initializer syntax can assign the <c>OnSessionStarted</c> delegate.</summary>
    [Fact]
    public void InitSyntax_AssignsOnSessionStarted()
    {
        var hooks = new AgentHooks
        {
            OnSessionStarted = (_, _) => Task.CompletedTask,
        };
        Assert.NotNull(hooks.OnSessionStarted);
    }

    /// <summary>Object-initializer syntax can assign the <c>OnPreToolUse</c> delegate.</summary>
    [Fact]
    public void InitSyntax_AssignsOnPreToolUse()
    {
        var hooks = new AgentHooks
        {
            OnPreToolUse = (_, _) => Task.FromResult(PreToolUseDecision.Allowed),
        };
        Assert.NotNull(hooks.OnPreToolUse);
    }

    /// <summary><see cref="AgentHooks.None"/> returns the same cached instance on repeated access.</summary>
    [Fact]
    public void None_IsSameReferenceEveryTime()
    {
        AgentHooks first = AgentHooks.None;
        AgentHooks second = AgentHooks.None;
        Assert.Same(first, second);
    }
}