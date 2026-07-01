using System.Text;
using Agency.Harness.Hooks.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Agency.Harness.Test.Hooks.Configuration;

/// <summary>
/// Verifies the DI contract of <c>AddAgencyConfiguredHooks</c> (spec §6.6):
/// HooksOptions binding, HookRegistry registration, PostConfigure wiring onto
/// AgentOptions.ConfiguredHooks, empty-section behaviour, and unknown-event validation.
/// </summary>
public sealed class HookServiceCollectionExtensionsTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static IConfiguration BuildConfig(string hooksJson)
    {
        var json = $$"""{"Hooks": {{hooksJson}}}""";
        return new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();
    }

    private static ServiceProvider BuildProvider(IConfiguration config)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions<AgentOptions>();
        services.AddAgencyConfiguredHooks(config);
        return services.BuildServiceProvider();
    }

    // ── Test 1 ───────────────────────────────────────────────────────────────

    /// <summary><c>AddAgencyConfiguredHooks</c> binds a valid <c>Hooks</c> section and registers a resolvable <see cref="HookRegistry"/>.</summary>
    [Fact]
    public void Di_BindsHooksSection_BuildsRegistry()
    {
        const string hooksJson = """
            {
              "PreToolUse": [
                {
                  "matcher": "*",
                  "hooks": [{ "type": "Command", "command": "pwsh" }]
                }
              ]
            }
            """;

        IConfiguration config = BuildConfig(hooksJson);
        using ServiceProvider provider = BuildProvider(config);

        HookRegistry registry = provider.GetRequiredService<HookRegistry>();

        Assert.NotNull(registry);
    }

    // ── Test 2 ───────────────────────────────────────────────────────────────

    /// <summary><c>AddAgencyConfiguredHooks</c> wires the built registry's projection onto <c>AgentOptions.ConfiguredHooks</c> via a post-configure step.</summary>
    [Fact]
    public void Di_PostConfigure_SetsConfiguredHooks()
    {
        const string hooksJson = """
            {
              "PreToolUse": [
                {
                  "matcher": "*",
                  "hooks": [{ "type": "Command", "command": "pwsh" }]
                }
              ]
            }
            """;

        IConfiguration config = BuildConfig(hooksJson);
        using ServiceProvider provider = BuildProvider(config);

        IOptions<AgentOptions> options = provider.GetRequiredService<IOptions<AgentOptions>>();

        Assert.NotNull(options.Value.ConfiguredHooks);
        Assert.NotNull(options.Value.ConfiguredHooks.OnPreToolUse);
    }

    // ── Test 3 ───────────────────────────────────────────────────────────────

    /// <summary>With no <c>Hooks</c> section present, <c>AgentOptions.ConfiguredHooks</c> ends up empty (null hooks or no <c>OnPreToolUse</c>) rather than throwing.</summary>
    [Fact]
    public void Di_NoHooksSection_ConfiguredHooksEmptyNone()
    {
        IConfiguration config = BuildConfig("{}");
        using ServiceProvider provider = BuildProvider(config);

        IOptions<AgentOptions> options = provider.GetRequiredService<IOptions<AgentOptions>>();

        bool isEmpty =
            options.Value.ConfiguredHooks == null ||
            options.Value.ConfiguredHooks.OnPreToolUse == null;

        Assert.True(isEmpty);
    }

    // ── Test 4 ───────────────────────────────────────────────────────────────

    /// <summary>An unrecognized event name in the configured <c>Hooks</c> section causes resolving <see cref="HookRegistry"/> to throw with that name in the message.</summary>
    [Fact]
    public void Di_UnknownEventName_ThrowsOnBuild()
    {
        const string hooksJson = """{"UnknownEvent": []}""";

        IConfiguration config = BuildConfig(hooksJson);
        using ServiceProvider provider = BuildProvider(config);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<HookRegistry>());

        Assert.Contains("UnknownEvent", ex.Message);
    }
}
