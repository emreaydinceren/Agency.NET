using Agency.Harness.Tools;

namespace Agency.Harness.Console.Test;

/// <summary>
/// Unit tests for <see cref="McpConfigResolver"/>, which expands <c>${RepoRoot}</c>/<c>${Configuration}</c>
/// placeholders in MCP server configuration so committed paths stay portable.
/// </summary>
public sealed class McpConfigResolverTests
{
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

    [Theory]
    [InlineData("/work/Agency/src/Harness/Agency.Harness.Console/bin/Release/net10.0/", "Release")]
    [InlineData("/work/Agency/src/Harness/Agency.Harness.Console/bin/Debug/net10.0", "Debug")]
    [InlineData("/no/output/segment/here", "Debug")]
    public void ResolveConfiguration_ReadsConfigFromBinPath(string baseDirectory, string expected)
    {
        Assert.Equal(expected, McpConfigResolver.ResolveConfiguration(baseDirectory));
    }

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
