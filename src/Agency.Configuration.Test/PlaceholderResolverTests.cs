using Agency.Llm.Common;
using Microsoft.Extensions.Configuration;

namespace Agency.Configuration.Test;

/// <summary>
/// Integration tests for <see cref="AgencyConfigurationBuilderExtensions.AddPlaceholderResolver"/>
/// covering: ConfigurationManager regression guard, strongly-typed array binding,
/// environment-variable precedence, and resolver-last override semantics.
/// </summary>
public sealed class PlaceholderResolverTests
{
    // -------------------------------------------------------------------------
    // Test 12 — ConfigurationManager regression guard
    // -------------------------------------------------------------------------

    /// <summary>
    /// Verifies that <c>AddPlaceholderResolver</c> works correctly when called on a
    /// real <see cref="ConfigurationManager"/> (which implements both
    /// <c>IConfigurationBuilder</c> and <c>IConfigurationRoot</c>, and whose
    /// <c>Build()</c> returns <c>this</c>).  The snapshot strategy must not
    /// self-destruct in this scenario.
    /// </summary>
    [Fact]
    public void AddPlaceholderResolver_OverConfigurationManager_ExpandsValues()
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Agent:BaseUrl"] = "http://host:1234",
            ["Agent:LLmClients:0:BaseUrl"] = "${Agent:BaseUrl}/v1"
        });

        configuration.AddPlaceholderResolver();

        // Token-bearing value is expanded.
        Assert.Equal("http://host:1234/v1", configuration["Agent:LLmClients:0:BaseUrl"]);

        // The base key itself is still readable (no empty/self-referential config).
        Assert.Equal("http://host:1234", configuration["Agent:BaseUrl"]);
    }

    // -------------------------------------------------------------------------
    // Test 13 — Headline binding: array elements via Get<T>
    // -------------------------------------------------------------------------

    /// <summary>
    /// Verifies the headline scenario: a two-element <c>Agent:LLmClients</c> array
    /// whose <c>BaseUrl</c> properties are placeholders are correctly expanded and
    /// bound via <c>GetSection(...).Get&lt;LlmClientOptions[]&gt;()</c>.
    /// </summary>
    [Fact]
    public void AddPlaceholderResolver_BindsExpandedArray_ToLlmClientOptions()
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Agent:BaseUrl"] = "http://host:1234",
            ["Agent:LLmClients:0:Name"] = "Client0",
            ["Agent:LLmClients:0:BaseUrl"] = "${Agent:BaseUrl}/v1",
            ["Agent:LLmClients:1:Name"] = "Client1",
            ["Agent:LLmClients:1:BaseUrl"] = "${Agent:BaseUrl}"
        });

        configuration.AddPlaceholderResolver();

        LlmClientOptions[]? clients =
            configuration.GetSection("Agent:LLmClients").Get<LlmClientOptions[]>();

        Assert.NotNull(clients);
        Assert.Equal(2, clients.Length);
        Assert.Equal("http://host:1234/v1", clients[0].BaseUrl);
        Assert.Equal("http://host:1234", clients[1].BaseUrl);
    }

    // -------------------------------------------------------------------------
    // Test 14 — Environment-variable precedence
    // -------------------------------------------------------------------------

    /// <summary>
    /// Verifies that when an environment variable overrides a key before
    /// <c>AddPlaceholderResolver</c> is called, placeholder tokens referencing
    /// that key resolve to the environment-variable value (env wins in the snapshot).
    /// </summary>
    [Fact]
    public void AddPlaceholderResolver_ResolvesTokensToEnvValue_WhenEnvOverridesBaseUrl()
    {
        Environment.SetEnvironmentVariable("Agent__BaseUrl", "http://env-host:9999");
        try
        {
            var configuration = new ConfigurationManager();
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Agent:BaseUrl"] = "http://host:1234",
                ["Agent:LLmClients:0:BaseUrl"] = "${Agent:BaseUrl}/v1"
            });
            // AddEnvironmentVariables after in-memory so env var has higher precedence.
            configuration.AddEnvironmentVariables();

            configuration.AddPlaceholderResolver();

            Assert.Equal("http://env-host:9999/v1", configuration["Agent:LLmClients:0:BaseUrl"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("Agent__BaseUrl", null);
        }
    }

    // -------------------------------------------------------------------------
    // Test 15 — Resolver-last override: expanded value shadows raw token
    // -------------------------------------------------------------------------

    /// <summary>
    /// Verifies that after <c>AddPlaceholderResolver</c> is called, reading a
    /// token-bearing configuration key returns the fully expanded string, not the
    /// raw <c>${...}</c> literal.  This confirms the resolver source is appended
    /// last and therefore takes precedence.
    /// </summary>
    [Fact]
    public void AddPlaceholderResolver_ReturnsExpanded_NotRawToken()
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Agent:BaseUrl"] = "http://host:1234",
            ["Agent:Api:Endpoint"] = "${Agent:BaseUrl}/api"
        });

        configuration.AddPlaceholderResolver();

        string? result = configuration["Agent:Api:Endpoint"];

        Assert.Equal("http://host:1234/api", result);
        Assert.DoesNotContain("${", result ?? string.Empty, StringComparison.Ordinal);
    }
}
