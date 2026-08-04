using System.Text.Json;

namespace Agency.Harness.Console.Test;

/// <summary>
/// Unit tests for <see cref="McpServerConfiguration"/>, which persists the enabled/disabled state
/// toggled via <c>/mcp-toggle</c> to <c>Mcp:Servers[].Enabled</c> on disk without ever round-tripping
/// the full <c>McpClientOptions</c> object — doing so would write the machine-specific
/// <c>${RepoRoot}</c> path and the live <c>${GitHubToken}</c> secret that
/// <c>McpConfigResolver.Expand</c> substitutes in place at startup into the committed file.
/// </summary>
public sealed class McpServerConfigurationTests
{
    // Mirrors the real appsettings.json Mcp section shape: one server with a ${RepoRoot} token in
    // Arguments, one with a ${GitHubToken} token in EnvironmentVariables, plus the inert "_examples"
    // array that must never be matched against.
    private const string FixtureJson = """
        {
          "Mcp": {
            "Servers": [
              {
                "Name": "memory",
                "Transport": "Stdio",
                "Command": "dotnet",
                "Arguments": [ "${RepoRoot}/src/Mcp/Agency.Mcp.Memory/bin/${Configuration}/net10.0/Agency.Mcp.Memory.dll" ],
                "EnvironmentVariables": {
                  "Memory__Provider": "sqlite",
                  "Memory__ConnectionString": "Data Source=agency-mcp-memory.db"
                }
              },
              {
                "Name": "github",
                "Transport": "Stdio",
                "Command": "docker",
                "Arguments": [ "run", "-i", "--rm", "-e", "GITHUB_PERSONAL_ACCESS_TOKEN", "ghcr.io/github/github-mcp-server" ],
                "EnvironmentVariables": {
                  "GITHUB_PERSONAL_ACCESS_TOKEN": "${GitHubToken}"
                }
              }
            ],
            "_examples": [
              {
                "Name": "remote-example",
                "Transport": "Http",
                "Url": "http://localhost:5000/mcp"
              }
            ]
          }
        }
        """;

    private static string WriteFixture(string content = FixtureJson)
    {
        string dir = Path.Combine(Path.GetTempPath(), "agency-mcpserverconfig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "appsettings.json");
        File.WriteAllText(path, content);
        return path;
    }

    private static JsonElement FindServer(string path, string name)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("Mcp").GetProperty("Servers").EnumerateArray()
            .First(server => server.GetProperty("Name").GetString() == name)
            .Clone();
    }

    /// <summary>
    /// Setting <c>Enabled</c> to <see langword="false"/> on an existing server adds the key, and the
    /// value round-trips when the file is re-parsed.
    /// </summary>
    [Fact]
    public void Persist_SetsEnabledFalse_OnExistingServer_AddsKeyAndRoundTrips()
    {
        string path = WriteFixture();
        try
        {
            McpServerConfiguration.Persist(path, "memory", enabled: false);

            JsonElement memory = FindServer(path, "memory");
            Assert.False(memory.GetProperty("Enabled").GetBoolean());
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    /// <summary>
    /// Toggling a server back to <see langword="true"/> updates the existing <c>Enabled</c> key rather
    /// than duplicating it — the file contains exactly one <c>Enabled</c> property after both toggles.
    /// </summary>
    [Fact]
    public void Persist_TogglingBackToTrue_UpdatesExistingKeyNotDuplicate()
    {
        string path = WriteFixture();
        try
        {
            McpServerConfiguration.Persist(path, "memory", enabled: false);
            McpServerConfiguration.Persist(path, "memory", enabled: true);

            JsonElement memory = FindServer(path, "memory");
            Assert.True(memory.GetProperty("Enabled").GetBoolean());

            string written = File.ReadAllText(path);
            int occurrences = written.Split("\"Enabled\"").Length - 1;
            Assert.Equal(1, occurrences);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    /// <summary>
    /// Persisting a change to one server leaves every property of the other server byte-for-byte
    /// unchanged — in particular, the <c>${RepoRoot}</c>/<c>${GitHubToken}</c> tokens in
    /// <c>Arguments</c>/<c>EnvironmentVariables</c> must still read as unexpanded placeholders. This
    /// is the regression guard for the secret-leak/portability hazard this helper exists to avoid.
    /// </summary>
    [Fact]
    public void Persist_LeavesOtherServerCompletelyUntouched()
    {
        string path = WriteFixture();
        try
        {
            McpServerConfiguration.Persist(path, "memory", enabled: false);

            string[] expectedArguments =
                ["run", "-i", "--rm", "-e", "GITHUB_PERSONAL_ACCESS_TOKEN", "ghcr.io/github/github-mcp-server"];

            JsonElement github = FindServer(path, "github");
            Assert.Equal("docker", github.GetProperty("Command").GetString());
            Assert.Equal(
                expectedArguments,
                github.GetProperty("Arguments").EnumerateArray().Select(a => a.GetString()));
            Assert.Equal(
                "${GitHubToken}",
                github.GetProperty("EnvironmentVariables").GetProperty("GITHUB_PERSONAL_ACCESS_TOKEN").GetString());
            Assert.False(github.TryGetProperty("Enabled", out _));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    /// <summary>
    /// An unknown server name leaves the file content unchanged and does not throw.
    /// </summary>
    [Fact]
    public void Persist_UnknownServerName_LeavesFileUnchanged()
    {
        string path = WriteFixture();
        try
        {
            string before = File.ReadAllText(path);

            McpServerConfiguration.Persist(path, "does-not-exist", enabled: true);

            Assert.Equal(before, File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    /// <summary>
    /// A file whose <c>Mcp</c> section is absent entirely does not throw.
    /// </summary>
    [Fact]
    public void Persist_MissingMcpSection_DoesNotThrow()
    {
        string path = WriteFixture("""{"Agent":{"DefaultModel":"x"}}""");
        try
        {
            string before = File.ReadAllText(path);

            McpServerConfiguration.Persist(path, "memory", enabled: false);

            Assert.Equal(before, File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    /// <summary>
    /// A missing appsettings.json file does not throw.
    /// </summary>
    [Fact]
    public void Persist_MissingFile_DoesNotThrow()
    {
        McpServerConfiguration.Persist(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"), "memory", enabled: false);
    }

    /// <summary>
    /// Server name matching is case-insensitive: persisting <c>"GitHub"</c> updates the entry stored
    /// as <c>"github"</c>.
    /// </summary>
    [Fact]
    public void Persist_MatchesServerNameCaseInsensitively()
    {
        string path = WriteFixture();
        try
        {
            McpServerConfiguration.Persist(path, "GitHub", enabled: false);

            JsonElement github = FindServer(path, "github");
            Assert.False(github.GetProperty("Enabled").GetBoolean());
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }
}
