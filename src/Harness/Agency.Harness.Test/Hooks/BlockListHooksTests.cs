
using System.Text.Json;
using Agency.Harness.Contexts;

namespace Agency.Harness.Hooks.Tests;
/// <summary>Verifies BlockListHooks.Dangerous blocks dangerous shell commands.</summary>
public sealed class BlockListHooksTests
{
    private static PreToolUseHookContext MakeCtx(string toolName, string? command)
    {
        Dictionary<string, object?> args = command is null
            ? []
            : new() { ["command"] = command };
        JsonElement input = JsonSerializer.SerializeToElement(args);
        Context ctx = new() { Query = new QueryContext { Prompt = "test" } };
        return new PreToolUseHookContext(toolName, input, ctx);
    }

    /// <summary>A shell command containing <c>rm -rf</c> is denied by <see cref="BlockListHooks.Dangerous"/>.</summary>
    [Fact]
    public async Task Dangerous_RmRf_ReturnsDeny()
    {
        PreToolUseHookContext ctx = MakeCtx("ExecutePowershell", "rm -rf /");
        PreToolUseDecision result = await BlockListHooks.Dangerous.OnPreToolUse!(ctx, CancellationToken.None);
        Assert.IsType<PreToolUseDecision.Deny>(result);
    }

    /// <summary>Pattern matching against dangerous commands is case-insensitive, so a mixed-case <c>DROP TABLE</c> is still denied.</summary>
    [Fact]
    public async Task Dangerous_DropTable_ReturnsDeny_CaseInsensitive()
    {
        PreToolUseHookContext ctx = MakeCtx("Bash", "DROP TABLE users");
        PreToolUseDecision result = await BlockListHooks.Dangerous.OnPreToolUse!(ctx, CancellationToken.None);
        Assert.IsType<PreToolUseDecision.Deny>(result);
    }

    /// <summary>A benign command such as <c>npm test</c> is allowed through by <see cref="BlockListHooks.Dangerous"/>.</summary>
    [Fact]
    public async Task Dangerous_SafeCommand_ReturnsAllow()
    {
        PreToolUseHookContext ctx = MakeCtx("ExecutePowershell", "npm test");
        PreToolUseDecision result = await BlockListHooks.Dangerous.OnPreToolUse!(ctx, CancellationToken.None);
        Assert.IsType<PreToolUseDecision.Allow>(result);
    }

    /// <summary>The dangerous-command pattern check only applies to shell-executing tools; a non-shell tool with the same text in its input is allowed.</summary>
    [Fact]
    public async Task Dangerous_NonBashTool_ReturnsAllow()
    {
        PreToolUseHookContext ctx = MakeCtx("ReadFile", "rm -rf /");
        PreToolUseDecision result = await BlockListHooks.Dangerous.OnPreToolUse!(ctx, CancellationToken.None);
        Assert.IsType<PreToolUseDecision.Allow>(result);
    }

    /// <summary>When the tool input has no <c>command</c> property to inspect, <see cref="BlockListHooks.Dangerous"/> allows the call.</summary>
    [Fact]
    public async Task Dangerous_MissingCommandProperty_ReturnsAllow()
    {
        PreToolUseHookContext ctx = MakeCtx("ExecutePowershell", null);
        PreToolUseDecision result = await BlockListHooks.Dangerous.OnPreToolUse!(ctx, CancellationToken.None);
        Assert.IsType<PreToolUseDecision.Allow>(result);
    }

    /// <summary>The deny reason returned for a blocked command includes the specific dangerous pattern that matched.</summary>
    [Fact]
    public async Task Dangerous_DenyReasonContainsDangerousPattern()
    {
        PreToolUseHookContext ctx = MakeCtx("Bash", "rm -rf /tmp");
        PreToolUseDecision result = await BlockListHooks.Dangerous.OnPreToolUse!(ctx, CancellationToken.None);
        PreToolUseDecision.Deny deny = Assert.IsType<PreToolUseDecision.Deny>(result);
        Assert.Contains("rm -rf", deny.Reason);
    }
}