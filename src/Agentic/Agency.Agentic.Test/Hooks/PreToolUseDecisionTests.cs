using System.Text.Json;
using Agency.Agentic.Hooks;

namespace Agency.Agentic.Hooks.Tests;

/// <summary>Unit tests for the <see cref="PreToolUseDecision"/> discriminated union.</summary>
public sealed class PreToolUseDecisionTests
{
    [Fact]
    public void Allow_IsNotNull()
    {
        Assert.NotNull(new PreToolUseDecision.Allow());
    }

    [Fact]
    public void Deny_StoresReason()
    {
        var deny = new PreToolUseDecision.Deny("blocked");
        Assert.Equal("blocked", deny.Reason);
    }

    [Fact]
    public void Rewrite_StoresNewInput()
    {
        var element = JsonSerializer.SerializeToElement(new Dictionary<string, object?>());
        var rewrite = new PreToolUseDecision.Rewrite(element);
        Assert.Equal(element.GetRawText(), rewrite.NewInput.GetRawText());
    }

    [Fact]
    public void Allowed_Singleton_IsAllow()
    {
        Assert.IsType<PreToolUseDecision.Allow>(PreToolUseDecision.Allowed);
    }

    [Fact]
    public void SwitchExpression_ExhaustiveOnAllThreeArms()
    {
        static string Describe(PreToolUseDecision d) => d switch
        {
            PreToolUseDecision.Allow => "allow",
            PreToolUseDecision.Deny deny => $"deny:{deny.Reason}",
            PreToolUseDecision.Rewrite => "rewrite",
            // No discard arm — compiler verifies exhaustiveness
        };

        Assert.Equal("allow", Describe(new PreToolUseDecision.Allow()));
        Assert.Equal("deny:test", Describe(new PreToolUseDecision.Deny("test")));
        Assert.Equal("rewrite", Describe(new PreToolUseDecision.Rewrite(JsonSerializer.SerializeToElement(1))));
    }
}