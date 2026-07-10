using Agency.Harness.Tools;

namespace Agency.Harness.Console.Test;

/// <summary>
/// Unit tests for <see cref="McpConfigResolver"/>, which expands <c>${RepoRoot}</c>/<c>${Configuration}</c>
/// placeholders in MCP server configuration so committed paths stay portable.
/// </summary>
public sealed class McpConfigResolverTests
{
    /// <summary>
    /// <c>${RepoRoot}</c> and <c>${Configuration}</c> placeholders are substituted in both
    /// server arguments and environment variable values.
    /// </summary>
    [Fact]
    public void Expand_SubstitutesTokensInCommandArgumentsAndEnvironment()
    {
        var options = new McpClientOptions
        {
            Servers =
            [
                new McpServerConfig
                {
                    Name = "memory",
                    Command = "dotnet",
                    Arguments = ["${RepoRoot}/src/Mcp/Agency.Mcp.Memory/bin/${Configuration}/net10.0/Agency.Mcp.Memory.dll"],
                    EnvironmentVariables = new Dictionary<string, string?>
                    {
                        ["Memory__Home"] = "${RepoRoot}/data"
                    }
                }
            ]
        };

        McpConfigResolver.Expand(options, repoRoot: "/work/Agency", configuration: "Release");

        McpServerConfig server = options.Servers[0];
        Assert.Equal("dotnet", server.Command);
        Assert.Equal(
            "/work/Agency/src/Mcp/Agency.Mcp.Memory/bin/Release/net10.0/Agency.Mcp.Memory.dll",
            server.Arguments![0]);
        Assert.Equal("/work/Agency/data", server.EnvironmentVariables!["Memory__Home"]);
    }

    /// <summary>
    /// <c>${GitHubToken}</c> is substituted with the supplied token when one is configured.
    /// </summary>
    [Fact]
    public void Expand_SubstitutesGitHubTokenWhenConfigured()
    {
        var options = new McpClientOptions
        {
            Servers =
            [
                new McpServerConfig
                {
                    Name = "github",
                    Command = "docker",
                    EnvironmentVariables = new Dictionary<string, string?>
                    {
                        ["GITHUB_PERSONAL_ACCESS_TOKEN"] = "${GitHubToken}"
                    }
                }
            ]
        };

        McpConfigResolver.Expand(options, repoRoot: "/work/Agency", configuration: "Release", gitHubToken: "ghp_test123");

        Assert.Equal("ghp_test123", options.Servers[0].EnvironmentVariables!["GITHUB_PERSONAL_ACCESS_TOKEN"]);
    }

    /// <summary>
    /// When no GitHub token is configured, the environment variable entry is removed rather than
    /// set to an empty string, so an ambient OS environment variable of the same name (e.g. set by
    /// <c>RunConsole.ps1</c>) still reaches the subprocess via inherited environment.
    /// </summary>
    [Fact]
    public void Expand_RemovesGitHubTokenEnvironmentVariableWhenNotConfigured()
    {
        var options = new McpClientOptions
        {
            Servers =
            [
                new McpServerConfig
                {
                    Name = "github",
                    Command = "docker",
                    EnvironmentVariables = new Dictionary<string, string?>
                    {
                        ["GITHUB_PERSONAL_ACCESS_TOKEN"] = "${GitHubToken}"
                    }
                }
            ]
        };

        McpConfigResolver.Expand(options, repoRoot: "/work/Agency", configuration: "Release", gitHubToken: null);

        Assert.False(options.Servers[0].EnvironmentVariables!.ContainsKey("GITHUB_PERSONAL_ACCESS_TOKEN"));
    }

    /// <summary>
    /// Command and argument values with no placeholder tokens pass through unmodified.
    /// </summary>
    [Fact]
    public void Expand_LeavesValuesWithoutTokensUnchanged()
    {
        var options = new McpClientOptions
        {
            Servers = [new McpServerConfig { Name = "notion", Command = "npx", Arguments = ["-y", "@notionhq/notion-mcp-server"] }]
        };

        McpConfigResolver.Expand(options, repoRoot: "/work/Agency", configuration: "Debug");

        Assert.Equal("npx", options.Servers[0].Command);
        Assert.Equal(["-y", "@notionhq/notion-mcp-server"], options.Servers[0].Arguments!);
    }

    /// <summary>
    /// The build configuration is read from the <c>bin/&lt;Configuration&gt;/</c> segment of the
    /// running assembly's base directory, defaulting to <c>Debug</c> when no such segment exists.
    /// </summary>
    /// <param name="baseDirectory">The candidate base directory to resolve from.</param>
    /// <param name="expected">The expected resolved configuration name.</param>
    [Theory]
    [InlineData("/work/Agency/src/Harness/Agency.Harness.Console/bin/Release/net10.0/", "Release")]
    [InlineData("/work/Agency/src/Harness/Agency.Harness.Console/bin/Debug/net10.0", "Debug")]
    [InlineData("/no/output/segment/here", "Debug")]
    public void ResolveConfiguration_ReadsConfigFromBinPath(string baseDirectory, string expected)
    {
        Assert.Equal(expected, McpConfigResolver.ResolveConfiguration(baseDirectory));
    }

    /// <summary>
    /// Starting from a deeply nested directory, the repo root is found by walking up until a
    /// <c>.git</c> directory is encountered.
    /// </summary>
    [Fact]
    public void FindRepoRoot_WalksUpToDirectoryContainingDotGit()
    {
        string root = Path.Combine(Path.GetTempPath(), $"mcp_repo_{Guid.NewGuid():N}");
        string nested = Path.Combine(root, "src", "Harness", "bin", "Debug");
        Directory.CreateDirectory(nested);
        Directory.CreateDirectory(Path.Combine(root, ".git"));

        try
        {
            Assert.Equal(root, McpConfigResolver.FindRepoRoot(nested));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
