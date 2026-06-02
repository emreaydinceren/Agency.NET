
using System.Text.Json;
using Agency.Harness.Contexts;
using Agency.Harness.Hooks;

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

    [Fact]
    public async Task Dangerous_RmRf_ReturnsDeny()
    {
        PreToolUseHookContext ctx = MakeCtx("ExecutePowershell", "rm -rf /");
        PreToolUseDecision result = await BlockListHooks.Dangerous.OnPreToolUse!(ctx, CancellationToken.None);
        Assert.IsType<PreToolUseDecision.Deny>(result);
    }

    [Fact]
    public async Task Dangerous_DropTable_ReturnsDeny_CaseInsensitive()
    {
        PreToolUseHookContext ctx = MakeCtx("Bash", "DROP TABLE users");
        PreToolUseDecision result = await BlockListHooks.Dangerous.OnPreToolUse!(ctx, CancellationToken.None);
        Assert.IsType<PreToolUseDecision.Deny>(result);
    }

    [Fact]
    public async Task Dangerous_SafeCommand_ReturnsAllow()
    {
        PreToolUseHookContext ctx = MakeCtx("ExecutePowershell", "npm test");
        PreToolUseDecision result = await BlockListHooks.Dangerous.OnPreToolUse!(ctx, CancellationToken.None);
        Assert.IsType<PreToolUseDecision.Allow>(result);
    }

    [Fact]
    public async Task Dangerous_NonBashTool_ReturnsAllow()
    {
        PreToolUseHookContext ctx = MakeCtx("ReadFile", "rm -rf /");
        PreToolUseDecision result = await BlockListHooks.Dangerous.OnPreToolUse!(ctx, CancellationToken.None);
        Assert.IsType<PreToolUseDecision.Allow>(result);
    }

    [Fact]
    public async Task Dangerous_MissingCommandProperty_ReturnsAllow()
    {
        PreToolUseHookContext ctx = MakeCtx("ExecutePowershell", null);
        PreToolUseDecision result = await BlockListHooks.Dangerous.OnPreToolUse!(ctx, CancellationToken.None);
        Assert.IsType<PreToolUseDecision.Allow>(result);
    }

    [Fact]
    public async Task Dangerous_DenyReasonContainsDangerousPattern()
    {
        PreToolUseHookContext ctx = MakeCtx("Bash", "rm -rf /tmp");
        PreToolUseDecision result = await BlockListHooks.Dangerous.OnPreToolUse!(ctx, CancellationToken.None);
        PreToolUseDecision.Deny deny = Assert.IsType<PreToolUseDecision.Deny>(result);
        Assert.Contains("rm -rf", deny.Reason);
    }
}