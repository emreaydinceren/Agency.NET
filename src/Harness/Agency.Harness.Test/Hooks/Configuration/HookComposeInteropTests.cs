using System.Text.Json;
using Agency.Harness.Contexts;
using Agency.Harness.Hooks;
using Agency.Harness.Hooks.Configuration;
using Agency.Harness.Hooks.Configuration.Handlers;

namespace Agency.Harness.Test.Hooks.Configuration;

/// <summary>
/// Verifies Spec §1 Property 2 / §14.5: Deny wins when config hooks and compiled C# hooks are composed.
/// A config Allow cannot override a C# Deny, and a config Deny wins against a permissive C# hook.
/// </summary>
public sealed class HookComposeInteropTests
{
    // ── Fake infrastructure ──────────────────────────────────────────────────

    private sealed class CannedHandler(HookHandlerOutput output) : IHookHandler
    {
        public Task<HookHandlerOutput> InvokeAsync(HookPayload payload, CancellationToken ct)
            => Task.FromResult(output);
    }

    private sealed class CannedFactory(HookHandlerOutput output) : IHookHandlerFactory
    {
        public IHookHandler Create(HookHandlerConfig config) => new CannedHandler(output);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static HooksOptions BuildOptions(string matcher)
    {
        var options = new HooksOptions();
        options.Hooks[HookEventName.PreToolUse] =
        [
            new HookMatcherGroupConfig
            {
                Matcher = matcher,
                Hooks = [new HookHandlerConfig { Type = HookHandlerKind.Command, Command = "fake" }]
            }
        ];
        return options;
    }

    private static PreToolUseHookContext MakePreToolUseCtx(string toolName)
    {
        JsonElement input = JsonSerializer.SerializeToElement(
            new Dictionary<string, object?> { ["command"] = "rm -rf /" });
        Context ctx = new() { Query = new QueryContext { Prompt = "test" } };
        return new PreToolUseHookContext(toolName, input, ctx);
    }

    // ── Test 1 ───────────────────────────────────────────────────────────────

    /// <summary>
    /// A config hook that returns Allow cannot override a compiled C# hook that Denies.
    /// BlockListHooks.Dangerous blocks "ExecutePowershell" when command contains "rm -rf".
    /// Composed result must still be Deny.
    /// </summary>
    [Fact]
    public async Task Compose_ConfigAllow_CannotOverrideCSharpDeny()
    {
        // Config registry always returns allow (exit 0, no JSON).
        var allowOutput = new HookHandlerOutput(HookExitCodes.Ok, null, null, null);
        var registry = new HookRegistry(BuildOptions("*"), new CannedFactory(allowOutput), null);
        AgentHooks configHooks = registry.ToAgentHooks();

        // Compose: config hooks first, then the compiled C# Dangerous hook (deny wins regardless of order).
        AgentHooks composed = configHooks.Compose(BlockListHooks.Dangerous);

        // "ExecutePowershell" + "rm -rf /" is a pattern BlockListHooks.Dangerous blocks.
        PreToolUseHookContext ctx = MakePreToolUseCtx("ExecutePowershell");
        PreToolUseDecision result = await composed.OnPreToolUse!(ctx, CancellationToken.None);

        Assert.IsType<PreToolUseDecision.Deny>(result);
    }

    // ── Test 2 ───────────────────────────────────────────────────────────────

    /// <summary>
    /// A config hook that Denies wins even when composed with a permissive C# hook that Allows.
    /// </summary>
    [Fact]
    public async Task Compose_ConfigDeny_WinsAgainstPermissiveCSharpHook()
    {
        // Config registry always returns blocking deny (exit 2).
        var denyOutput = new HookHandlerOutput(HookExitCodes.BlockingDeny, null, "denied by config", null);
        var registry = new HookRegistry(BuildOptions("*"), new CannedFactory(denyOutput), null);
        AgentHooks configHooks = registry.ToAgentHooks();

        // Permissive C# hook: always allows.
        AgentHooks permissiveCSharpHooks = new()
        {
            OnPreToolUse = (_, _) => Task.FromResult(PreToolUseDecision.Allowed),
        };

        AgentHooks composed = configHooks.Compose(permissiveCSharpHooks);

        // "Bash" matches the "*" matcher in config.
        PreToolUseHookContext ctx = MakePreToolUseCtx("Bash");
        PreToolUseDecision result = await composed.OnPreToolUse!(ctx, CancellationToken.None);

        Assert.IsType<PreToolUseDecision.Deny>(result);
    }
}
