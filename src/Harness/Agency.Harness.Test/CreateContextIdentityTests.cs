using Agency.Harness.Contexts;

namespace Agency.Harness.Test;

/// <summary>
/// Tests for the <see cref="Agent.CreateContext(string, Agency.Harness.Contexts.ToolContext?, Agency.Harness.Contexts.EnvironmentalContext?, Agency.Harness.Contexts.UserSpecificContext?, System.TimeProvider?, Agency.Harness.Contexts.SkillContext?, Agency.Harness.Contexts.SessionContext?, string?, string?)"/> overload that accepts an
/// <c>identityPrompt</c> (spec §6.4): identity must be threaded into
/// <see cref="QueryContext.IdentityPrompt"/> via a new, full-arity overload, and the existing
/// 8-parameter overload must keep compiling and behaving exactly as before (§14.2 — overloads,
/// not an optional parameter, to avoid a binary-breaking <c>*REMOVED*</c> entry).
/// </summary>
public sealed class CreateContextIdentityTests
{
    /// <summary>The new 9-arg overload sets <see cref="QueryContext.IdentityPrompt"/> from its <c>identityPrompt</c> argument.</summary>
    [Fact]
    public void CreateContext_NineArgOverload_SetsIdentityPrompt()
    {
        Context ctx = Agent.CreateContext("p", null, null, null, null, null, null, null, "You are Ana");

        Assert.Equal("You are Ana", ctx.Query.IdentityPrompt);
    }

    /// <summary>
    /// The existing call shape <c>Agent.CreateContext("p")</c> must still compile verbatim and
    /// yield a <see langword="null"/> <see cref="QueryContext.IdentityPrompt"/>. This is the
    /// back-compat gate: if this assertion ever needs editing, an optional parameter was added
    /// instead of an overload.
    /// </summary>
    [Fact]
    public void CreateContext_ExistingCallShape_StillCompilesAndYieldsNullIdentityPrompt()
    {
        Context ctx = Agent.CreateContext("p");

        Assert.Null(ctx.Query.IdentityPrompt);
    }

    /// <summary><see cref="QueryContext.Prompt"/> and <see cref="QueryContext.InstructionsBlock"/> are unaffected by the new <c>identityPrompt</c> parameter.</summary>
    [Fact]
    public void CreateContext_NineArgOverload_PromptAndInstructionsBlockRoundTripAlongsideIdentity()
    {
        Context ctx = Agent.CreateContext("p", null, null, null, null, null, null, "instructions", "You are Ana");

        Assert.Equal("p", ctx.Query.Prompt);
        Assert.Equal("instructions", ctx.Query.InstructionsBlock);
        Assert.Equal("You are Ana", ctx.Query.IdentityPrompt);
    }
}
