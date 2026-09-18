using Agency.Harness;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Agency.Acp.Test;

/// <summary>
/// Configuration-bootstrap guarantees for <see cref="Program.BuildHost"/> (spec §6.5): a vanilla
/// <c>agency-acp</c> — no <c>Agent__*</c> environment overrides at all — must still resolve a
/// non-empty <see cref="AgentOptions.DefaultModel"/> (O-5), environment variables must still win
/// over the shipped file (Huddle's existing per-profile override path), and the hardcoded
/// <c>Skills:DisableShellExecution</c> lock must remain undefeatable by either the file or the
/// environment (spec §12 E-13).
/// </summary>
public sealed class ConfigurationBootstrapTests
{
    // Every Agent:* key that Program.BuildHost binds AgentOptions from, expressed as the
    // environment-variable form (double-underscore section separator) so a test can clear the
    // exact set a real deployment might have set, regardless of what this process happened to
    // inherit from its own launch environment.
    private static readonly string[] AgentEnvironmentVariableNames =
    [
        "Agent__DefaultClientName",
        "Agent__DefaultModel",
        "Agent__TurnTimeoutSeconds",
        "Agent__LLmClients__0__Name",
        "Agent__LLmClients__0__ClientType",
        "Agent__LLmClients__0__BaseUrl",
        "Agent__LLmClients__0__ApiKey",
    ];

    /// <summary>
    /// Task 21 (spec §6.5, O-5): with no <c>Agent__*</c> environment variables set, the process
    /// composition root must still resolve a usable <see cref="AgentOptions"/> — a non-empty
    /// <see cref="AgentOptions.DefaultClientName"/>, a non-empty <see cref="AgentOptions.DefaultModel"/>,
    /// and at least one configured LLM client. Before the shipped <c>appsettings.json</c> exists,
    /// nothing populates these and this test is RED.
    /// </summary>
    [Fact]
    public void BuildHost_NoEnvironmentConfigured_ResolvesNonEmptyDefaults()
    {
        Dictionary<string, string?> saved = ClearAgentEnvironmentVariables();
        try
        {
            using IHost host = Program.BuildHost();

            AgentOptions options = host.Services.GetRequiredService<IOptions<AgentOptions>>().Value;

            Assert.False(string.IsNullOrWhiteSpace(options.DefaultClientName));
            Assert.False(string.IsNullOrWhiteSpace(options.DefaultModel));
            Assert.NotEmpty(options.LLmClients);
        }
        finally
        {
            RestoreEnvironmentVariables(saved);
        }
    }

    /// <summary>
    /// Task 23 (spec §6.5, Implementation notes — layering): Huddle supplies
    /// <c>Agent__DefaultModel</c> via per-profile <c>EnvironmentOverrides</c>. <c>Host.CreateApplicationBuilder</c>
    /// adds environment variables after <c>appsettings.json</c>, so the environment value must win
    /// over the shipped file's placeholder. Inverting that precedence would silently break Huddle's
    /// existing integration.
    /// </summary>
    [Fact]
    public void BuildHost_EnvironmentDefaultModelSet_OverridesShippedFileValue()
    {
        const string distinctiveModel = "env-override-qwen3-distinctive-9f2c";
        string? saved = Environment.GetEnvironmentVariable("Agent__DefaultModel");
        Environment.SetEnvironmentVariable("Agent__DefaultModel", distinctiveModel);
        try
        {
            using IHost host = Program.BuildHost();

            AgentOptions options = host.Services.GetRequiredService<IOptions<AgentOptions>>().Value;

            Assert.Equal(distinctiveModel, options.DefaultModel);
        }
        finally
        {
            Environment.SetEnvironmentVariable("Agent__DefaultModel", saved);
        }
    }

    /// <summary>
    /// Task 24 (spec §12 E-13; base spec §6.5 lock #4): shipping a config file must not create a
    /// route to defeating a v1 safety lock. Even when the environment itself supplies
    /// <c>Skills__DisableShellExecution=false</c> — beating the shipped file on its own — the
    /// hardcoded in-memory source <see cref="Program.BuildHost"/> adds must still win. Read the
    /// same way <c>V1GuaranteeTests.SkillShellExecutionDisabled</c> asserts lock #4: via
    /// <c>GetValue&lt;bool&gt;("Skills:DisableShellExecution")</c> on the resolved
    /// <see cref="IConfiguration"/>, not through <see cref="AgentOptions"/> (the lock is a
    /// <c>Skills:*</c> key, not an <c>Agent:*</c> one).
    /// </summary>
    [Fact]
    public void BuildHost_EnvironmentDisablesShellExecutionLockFalse_HardcodedSourceStillWinsTrue()
    {
        string? saved = Environment.GetEnvironmentVariable("Skills__DisableShellExecution");
        Environment.SetEnvironmentVariable("Skills__DisableShellExecution", "false");
        try
        {
            using IHost host = Program.BuildHost();

            IConfiguration config = host.Services.GetRequiredService<IConfiguration>();

            Assert.True(config.GetValue<bool>("Skills:DisableShellExecution"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("Skills__DisableShellExecution", saved);
        }
    }

    /// <summary>
    /// Regression (reported by Agency.Huddle against drop 0.1.196-ga1fc165f21): a vanilla
    /// <c>agency-acp</c> must not merely <em>bind</em> the shipped configuration — it must be able
    /// to build an <see cref="Agent"/> from it, which is what <c>session/new</c> actually does.
    /// </summary>
    /// <remarks>
    /// The original shipped <c>appsettings.json</c> omitted <c>Agent:LLmClients:0:ApiKey</c>.
    /// <see cref="AgentOptions"/> bound fine and every existing bootstrap assertion passed, but
    /// <c>OpenAIClient.BuildOpenAIClient</c> calls <c>new ApiKeyCredential(opts.ApiKey)</c>, which
    /// rejects an empty string — so every <c>session/new</c> failed with an opaque
    /// <c>ArgumentException: Value cannot be an empty string. (Parameter 'key')</c>. Asserting that
    /// options bind is not the same as asserting the process works; this test closes that gap by
    /// exercising the same factory call the dispatcher makes.
    /// </remarks>
    [Fact]
    public void BuildHost_NoEnvironmentConfigured_CanBuildAnAgentFromShippedConfiguration()
    {
        Dictionary<string, string?> saved = ClearAgentEnvironmentVariables();
        try
        {
            using IHost host = Program.BuildHost();
            using IServiceScope scope = host.Services.CreateScope();
            IAgentFactory factory = scope.ServiceProvider.GetRequiredService<IAgentFactory>();
            AgentOptions options = scope.ServiceProvider.GetRequiredService<IOptions<AgentOptions>>().Value;

            // Exactly what SessionFactory.CreateAsync step 6 does.
            Agent agent = factory.CreateAgent(null, options.DefaultModel);

            Assert.NotNull(agent);
        }
        finally
        {
            RestoreEnvironmentVariables(saved);
        }
    }

    /// <summary>
    /// Companion to the regression above (spec §12 E-12): if an operator removes
    /// <c>Agent:LLmClients:0:ApiKey</c>, the failure must <em>name the key</em> rather than surfacing
    /// the SDK's opaque <c>ArgumentException: Value cannot be an empty string. (Parameter 'key')</c>.
    /// Shipping a working default is not enough on its own — the next person to edit the file must
    /// be told what they broke.
    /// </summary>
    [Fact]
    public void CreateAgent_ApiKeyMissing_ThrowsNamingTheConfigurationKey()
    {
        Dictionary<string, string?> saved = ClearAgentEnvironmentVariables();
        try
        {
            using IHost host = Program.BuildHost();
            using IServiceScope scope = host.Services.CreateScope();
            AgentOptions options = scope.ServiceProvider.GetRequiredService<IOptions<AgentOptions>>().Value;

            // Simulate an operator deleting the ApiKey line from the shipped appsettings.json.
            options.LLmClients[0].ApiKey = string.Empty;

            IAgentFactory factory = scope.ServiceProvider.GetRequiredService<IAgentFactory>();
            Exception ex = Assert.ThrowsAny<Exception>(() => factory.CreateAgent(null, options.DefaultModel));

            string message = ex.ToString();
            Assert.Contains("ApiKey", message, StringComparison.Ordinal);
            Assert.Contains("Agent__LLmClients__<n>__ApiKey", message, StringComparison.Ordinal);
            Assert.DoesNotContain("Parameter 'key'", message, StringComparison.Ordinal);
        }
        finally
        {
            RestoreEnvironmentVariables(saved);
        }
    }

    private static Dictionary<string, string?> ClearAgentEnvironmentVariables()
    {
        var saved = new Dictionary<string, string?>();
        foreach (string name in AgentEnvironmentVariableNames)
        {
            saved[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }

        return saved;
    }

    private static void RestoreEnvironmentVariables(Dictionary<string, string?> saved)
    {
        foreach ((string name, string? value) in saved)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }
}
