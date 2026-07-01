using System.Text;
using System.Text.Json;
using Agency.Harness.Contexts;
using Agency.Harness.Hooks;
using Agency.Harness.Hooks.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agency.Harness.Test.Hooks.Configuration;

/// <summary>
/// End-to-end test that walks the full path:
/// JSON config → <c>HookRegistry</c> build → <c>ToAgentHooks()</c> → <c>OnPreToolUse</c>
/// delegate → real <c>pwsh</c> process runs → deny JSON output → <c>AggregateDecision</c>
/// returns <see cref="PreToolUseDecision.Deny"/> with reason <c>blocked-by-test</c>.
/// </summary>
[Trait("Category", "Functional")]
[Trait("Category", "Process")]
public sealed class ConfiguredHooksE2ETests
{
    /// <summary>
    /// A JSON-configured <c>Command</c> handler that runs a real <c>pwsh</c> guard script and prints
    /// deny JSON produces a <see cref="PreToolUseDecision.Deny"/> decision with the script's reason, end to end.
    /// </summary>
    [Fact]
    public async Task E2E_PreToolUse_DenyFromCommandHandler_BlocksTool()
    {
        // 1. Write a temp guard script that emits deny JSON and exits 0.
        var scriptPath = Path.Combine(Path.GetTempPath(), $"guard-{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(scriptPath,
            """Write-Output '{"hookSpecificOutput":{"permissionDecision":"deny","permissionDecisionReason":"blocked-by-test"}}' """,
            cancellationToken: TestContext.Current.CancellationToken);

        try
        {
            // 2. Build IConfiguration with a PreToolUse hook targeting the script.
            var json = $$"""
            {
              "Hooks": {
                "PreToolUse": [
                  {
                    "matcher": "Bash|ExecutePowershell",
                    "hooks": [
                      { "type": "Command", "command": "pwsh",
                        "args": ["-NoProfile", "-File", "{{scriptPath.Replace("\\", "\\\\")}}"],
                        "timeout": 30 }
                    ]
                  }
                ]
              }
            }
            """;
            var config = new ConfigurationBuilder()
                .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
                .Build();

            // 3. Build DI container and resolve HookRegistry.
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddOptions<AgentOptions>();
            services.AddAgencyConfiguredHooks(config);
            var provider = services.BuildServiceProvider();
            var registry = provider.GetRequiredService<HookRegistry>();

            // 4. Get the AgentHooks and invoke OnPreToolUse directly.
            var agentHooks = registry.ToAgentHooks();
            Assert.NotNull(agentHooks.OnPreToolUse);

            // 5. Build a PreToolUseHookContext for the "ExecutePowershell" tool.
            Context agentContext = new() { Query = new QueryContext { Prompt = "test" } };
            JsonElement input = JsonDocument.Parse("{}").RootElement.Clone();
            var ctx = new PreToolUseHookContext("ExecutePowershell", input, agentContext);

            var decision = await agentHooks.OnPreToolUse!(ctx, TestContext.Current.CancellationToken);

            // 6. Assert deny with the expected reason.
            var deny = Assert.IsType<PreToolUseDecision.Deny>(decision);
            Assert.Contains("blocked-by-test", deny.Reason);
        }
        finally
        {
            if (File.Exists(scriptPath))
            {
                File.Delete(scriptPath);
            }
        }
    }
}
