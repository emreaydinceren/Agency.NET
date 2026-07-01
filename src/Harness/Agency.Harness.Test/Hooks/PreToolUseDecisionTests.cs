using System.Text.Json;

namespace Agency.Harness.Hooks.Tests;

/// <summary>Unit tests for the <see cref="PreToolUseDecision"/> discriminated union.</summary>
public sealed class PreToolUseDecisionTests
{
    /// <summary>A new <see cref="PreToolUseDecision.Allow"/> instance is never <see langword="null"/>.</summary>
    [Fact]
    public void Allow_IsNotNull()
    {
        Assert.NotNull(new PreToolUseDecision.Allow());
    }

    /// <summary><see cref="PreToolUseDecision.Deny"/> stores the reason passed to its constructor.</summary>
    [Fact]
    public void Deny_StoresReason()
    {
        var deny = new PreToolUseDecision.Deny("blocked");
        Assert.Equal("blocked", deny.Reason);
    }

    /// <summary><see cref="PreToolUseDecision.Rewrite"/> stores the replacement input <see cref="JsonElement"/> passed to its constructor.</summary>
    [Fact]
    public void Rewrite_StoresNewInput()
    {
        var element = JsonSerializer.SerializeToElement(new Dictionary<string, object?>());
        var rewrite = new PreToolUseDecision.Rewrite(element);
        Assert.Equal(element.GetRawText(), rewrite.NewInput.GetRawText());
    }

    /// <summary>The <see cref="PreToolUseDecision.Allowed"/> singleton is an instance of <see cref="PreToolUseDecision.Allow"/>.</summary>
    [Fact]
    public void Allowed_Singleton_IsAllow()
    {
        Assert.IsType<PreToolUseDecision.Allow>(PreToolUseDecision.Allowed);
    }

    /// <summary>A switch expression over <see cref="PreToolUseDecision"/> is exhaustive across the Allow, Deny, and Rewrite arms without a discard pattern.</summary>
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