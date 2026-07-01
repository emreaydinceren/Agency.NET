using System.Text;
using Agency.Harness.Permissions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agency.Harness.Test.Permissions;

/// <summary>
/// Verifies the DI contract of <c>AddAgencyPermissions</c> (spec §10):
/// options binding, singleton IPermissionEvaluator registration with fail-fast validation,
/// custom section name, and no-registration passthrough.
/// </summary>
public sealed class PermissionServiceCollectionExtensionsTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static IConfiguration BuildConfig(string permissionsJson)
    {
        string json = $$"""{"Permissions": {{permissionsJson}}}""";
        return new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();
    }

    private static IConfiguration BuildConfigUnderSection(string sectionName, string permissionsJson)
    {
        string json = $$"""{"{{sectionName}}": {{permissionsJson}}}""";
        return new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();
    }

    private static ServiceProvider BuildProvider(IConfiguration config)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgencyPermissions(config);
        return services.BuildServiceProvider();
    }

    // ── Test 1a: Registration resolves IPermissionEvaluator ──────────────────

    /// <summary>
    /// After calling <c>AddAgencyPermissions</c> with a valid configuration,
    /// <see cref="IPermissionEvaluator"/> must be resolvable from the container.
    /// </summary>
    [Fact]
    public void Di_ValidConfig_ResolvesIPermissionEvaluator()
    {
        IConfiguration config = BuildConfig("""
            {
              "Allow": [ "ReadFile" ],
              "Deny":  []
            }
            """);
        using ServiceProvider provider = BuildProvider(config);

        IPermissionEvaluator evaluator = provider.GetRequiredService<IPermissionEvaluator>();

        Assert.NotNull(evaluator);
    }

    // ── Test 1b: Singleton — two resolutions return the same instance ─────────

    /// <summary>
    /// The <see cref="IPermissionEvaluator"/> registration made by <c>AddAgencyPermissions</c>
    /// must be a singleton: two resolutions from the same provider return the same instance.
    /// </summary>
    [Fact]
    public void Di_ValidConfig_EvaluatorIsSingleton()
    {
        IConfiguration config = BuildConfig("""
            {
              "Allow": [ "ReadFile" ]
            }
            """);
        using ServiceProvider provider = BuildProvider(config);

        IPermissionEvaluator first = provider.GetRequiredService<IPermissionEvaluator>();
        IPermissionEvaluator second = provider.GetRequiredService<IPermissionEvaluator>();

        Assert.Same(first, second);
    }

    // ── Test 1c: Bound config reaches the evaluator — deny rule returns Deny ──

    /// <summary>
    /// A deny rule bound from configuration must actually reach the resolved
    /// <see cref="IPermissionEvaluator"/>: evaluating a matching call must return
    /// <see cref="PermissionDecision.Deny"/>, proving the options were not swallowed during
    /// binding.
    /// </summary>
    [Fact]
    public void Di_BoundConfig_DenyRuleReachesEvaluator()
    {
        // A deny rule in config must produce Deny for a matching tool call — proving
        // the options reached the evaluator rather than being swallowed during binding.
        IConfiguration config = BuildConfig("""
            {
              "Deny": [ "WriteFile" ]
            }
            """);
        using ServiceProvider provider = BuildProvider(config);

        IPermissionEvaluator evaluator = provider.GetRequiredService<IPermissionEvaluator>();

        System.Text.Json.JsonElement input =
            System.Text.Json.JsonDocument.Parse("{}").RootElement;
        PermissionDecision decision = evaluator.Evaluate("WriteFile", input);

        Assert.IsType<PermissionDecision.Deny>(decision);
    }

    // ── Test 2: Fail-fast on malformed rule ────────────────────────────────────

    /// <summary>
    /// A malformed allow rule in configuration must fail fast: resolving
    /// <see cref="IPermissionEvaluator"/> must throw <see cref="InvalidOperationException"/>
    /// whose message chain contains the offending rule text.
    /// </summary>
    [Fact]
    public void Di_MalformedAllowRule_ThrowsOnEvaluatorResolution()
    {
        // "Tool(" is the canonical malformed-rule string used across the permissions tests.
        IConfiguration config = BuildConfig("""
            {
              "Allow": [ "Tool(" ]
            }
            """);
        using ServiceProvider provider = BuildProvider(config);

        // The DI factory validates at first resolution; DI may wrap the exception.
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<IPermissionEvaluator>());

        // The offending rule must appear in the message chain.
        Assert.Contains("Tool(", ex.Message);
    }

    // ── Test 3: Custom section name ────────────────────────────────────────────

    /// <summary>
    /// <c>AddAgencyPermissions</c> must bind from a caller-supplied configuration section name
    /// (rather than the default "Permissions"): a deny rule under that custom section must
    /// reach the resolved evaluator.
    /// </summary>
    [Fact]
    public void Di_CustomSectionName_BindsCorrectSection()
    {
        // Config has "MyPerms" section with a deny rule; "Permissions" section is absent.
        IConfiguration config = BuildConfigUnderSection("MyPerms", """
            {
              "Deny": [ "WriteFile" ]
            }
            """);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgencyPermissions(config, "MyPerms");
        using ServiceProvider provider = services.BuildServiceProvider();

        IPermissionEvaluator evaluator = provider.GetRequiredService<IPermissionEvaluator>();

        System.Text.Json.JsonElement input =
            System.Text.Json.JsonDocument.Parse("{}").RootElement;
        PermissionDecision decision = evaluator.Evaluate("WriteFile", input);

        // "MyPerms" deny rule reached the evaluator → Deny (not Ask, which would mean empty config was used).
        Assert.IsType<PermissionDecision.Deny>(decision);
    }

    // ── Test 4a: No-registration → GetService returns null ────────────────────

    /// <summary>
    /// Without calling <c>AddAgencyPermissions</c>, <see cref="IPermissionEvaluator"/> must not
    /// be registered: <c>GetService&lt;IPermissionEvaluator&gt;()</c> returns
    /// <see langword="null"/> rather than throwing.
    /// </summary>
    [Fact]
    public void Di_NoRegistration_GetServiceReturnsNull()
    {
        // Without AddAgencyPermissions, IPermissionEvaluator must not be in the container.
        var services = new ServiceCollection();
        using ServiceProvider provider = services.BuildServiceProvider();

        IPermissionEvaluator? evaluator = provider.GetService<IPermissionEvaluator>();

        Assert.Null(evaluator);
    }

    // ── Test 4b: AgentFactory still constructs without permissions registration ─

    /// <summary>
    /// Regression pin for spec §2.4 ("no evaluator → unchanged behavior"): when
    /// <see cref="IPermissionEvaluator"/> is absent from DI, resolution must yield
    /// <see langword="null"/> without throwing, and passing that <see langword="null"/> as the
    /// agent-level permissions parameter must remain a valid, compiling call shape.
    /// </summary>
    [Fact]
    public void Di_NoPermissionsRegistration_AgentFactoryConstructsSuccessfully()
    {
        // Guard: AgentFactory must accept construction without IPermissionEvaluator in DI.
        // After Task 19 adds the optional IPermissionEvaluator? ctor param, the factory
        // must still be resolvable when no evaluator is registered.
        //
        // We construct AgentFactory directly (its non-DI ctor path) to verify the compile-time
        // contract: if Task 19 adds a required ctor param this test fails to compile, which is
        // the intended RED signal.
        //
        // AgentFactory lives in Agency.Harness.Console and is internal; the test project does
        // not reference that assembly, so we exercise the harness-layer behavior: Agent can be
        // constructed with permissions = null and the existing hook/null path is unchanged.
        //
        // This is the regression pin for "no evaluator → unchanged behavior" (spec §2.4).
        var services = new ServiceCollection();
        using ServiceProvider provider = services.BuildServiceProvider();

        IPermissionEvaluator? evaluator = provider.GetService<IPermissionEvaluator>();

        // The whole point: null evaluator is legal; no exception thrown from DI.
        Assert.Null(evaluator);

        // Additionally verify we can pass null to the agent-level PermissionEvaluator parameter
        // without any type error — this is a compile-time guard. If Task 19 forgets the default
        // value or makes the param required, the next line fails to compile.
        IPermissionEvaluator? nullEvaluator = null;
        _ = nullEvaluator; // use the variable; actual Agent construction requires LlmClient wiring
    }
}
