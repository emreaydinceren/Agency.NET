namespace Agency.Harness.Test;

/// <summary>
/// Unit tests pinning <see cref="McpServerConfig.Headers"/> through to the MCP transport options.
/// A server that authenticates the transport itself - a bearer token minted per session - is
/// unreachable without this, so the passthrough is asserted rather than assumed.
/// </summary>
public sealed class McpServerConfigHeadersTests
{
    /// <summary>
    /// Headers configured on an Http server must reach <c>AdditionalHeaders</c> verbatim.
    /// </summary>
    [Fact]
    public void BuildHttpTransportOptions_WhenHeadersConfigured_ForwardsThemVerbatim()
    {
        McpServerConfig server = new()
        {
            Name = "team",
            Transport = McpTransportKind.Http,
            Url = "http://127.0.0.1:18777/mcp",
            Headers = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Authorization"] = "Bearer abc123"
            }
        };

        var options = McpClientPool.BuildHttpTransportOptions(server);

        Assert.NotNull(options.AdditionalHeaders);
        Assert.Equal("Bearer abc123", options.AdditionalHeaders!["Authorization"]);
        Assert.Equal("team", options.Name);
        Assert.Equal(new Uri("http://127.0.0.1:18777/mcp"), options.Endpoint);
    }

    /// <summary>
    /// Absence must stay absence. An unconfigured <see cref="McpServerConfig.Headers"/> must not
    /// become an empty dictionary - "no headers configured" and "an empty header set" are
    /// different states, and only the first should reach the transport as <see langword="null"/>.
    /// </summary>
    [Fact]
    public void BuildHttpTransportOptions_WhenHeadersAbsent_LeavesAdditionalHeadersNull()
    {
        McpServerConfig server = new()
        {
            Name = "team",
            Transport = McpTransportKind.Http,
            Url = "http://127.0.0.1:18777/mcp"
        };

        var options = McpClientPool.BuildHttpTransportOptions(server);

        Assert.Null(options.AdditionalHeaders);
    }

    /// <summary>
    /// A missing <see cref="McpServerConfig.Url"/> is a configuration fault and must fail loudly
    /// rather than produce a transport pointed nowhere.
    /// </summary>
    [Fact]
    public void BuildHttpTransportOptions_WhenUrlMissing_Throws()
    {
        McpServerConfig server = new() { Name = "team", Transport = McpTransportKind.Http };

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => McpClientPool.BuildHttpTransportOptions(server));

        Assert.Contains("team", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Stdio authenticates through <see cref="McpServerConfig.EnvironmentVariables"/>, never
    /// through HTTP headers. Configuring both must not leak the headers into the subprocess options.
    /// </summary>
    [Fact]
    public void BuildStdioTransportOptions_IgnoresHeaders()
    {
        McpServerConfig server = new()
        {
            Name = "github",
            Transport = McpTransportKind.Stdio,
            Command = "docker",
            Arguments = ["run", "-i"],
            EnvironmentVariables = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["TOKEN"] = "secret"
            },
            Headers = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Authorization"] = "Bearer should-not-appear"
            }
        };

        var options = McpClientPool.BuildStdioTransportOptions(server);

        Assert.Equal("docker", options.Command);
        Assert.Equal("github", options.Name);
        Assert.NotNull(options.EnvironmentVariables);
        Assert.Equal("secret", options.EnvironmentVariables!["TOKEN"]);
    }
}
