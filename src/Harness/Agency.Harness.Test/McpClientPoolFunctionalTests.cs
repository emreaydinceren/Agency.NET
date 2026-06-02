using System.Runtime.InteropServices;
using System.Text.Json;
using Agency.Harness.Tools;
using Agency.Llm.Common.Tools;

namespace Agency.Harness.Test;

/// <summary>
/// Functional tests for <see cref="McpClientPool"/> using the Agency.Mcp.Memory stdio server.
/// </summary>
[Trait("Category", "Functional")]
public sealed class McpClientPoolFunctionalTests : IClassFixture<McpClientPoolFunctionalTests.McpPoolFixture>
{
    private readonly McpPoolFixture _fixture;

    /// <summary>Initializes a new instance with the shared pool fixture.</summary>
    public McpClientPoolFunctionalTests(McpPoolFixture fixture)
    {
        this._fixture = fixture;
    }

    /// <summary>
    /// Tools discovered from the Memory MCP server must include memorize, recall, and forget.
    /// </summary>
    [Fact]
    public async Task ToolsDiscovered_ReturnsMemoryTools()
    {
        IReadOnlyList<ITool> tools = this._fixture.Pool.Tools;

        Assert.True(tools.Count >= 3);
        Assert.Contains(tools, t => t.Definition.Name == "memorize");
        Assert.Contains(tools, t => t.Definition.Name == "recall");
        Assert.Contains(tools, t => t.Definition.Name == "forget");

        await Task.CompletedTask;
    }

    /// <summary>
    /// Invoking the memorize tool via the pool proxy must return a success result.
    /// </summary>
    [Fact]
    public async Task InvokeMemorize_ReturnsSuccessResult()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        ITool memorizeTool = this._fixture.Pool.Tools.First(t => t.Definition.Name == "memorize");

        JsonElement input = JsonSerializer.SerializeToElement(new
        {
            record = new
            {
                Scope = new { UserId = "pool-test-u1", SessionId = "pool-test-s1" },
                Domain = "geography",
                Key = "capital-of-france",
                Value = "Paris"
            }
        });

        ToolResult result = await memorizeTool.InvokeAsync(input, ct);

        Assert.False(result.IsError);
        Assert.Contains("Memorized", result.Content);
    }

    /// <summary>
    /// Starts the Agency.Mcp.Memory server as a subprocess using an ephemeral SQLite database.
    /// </summary>
    public sealed class McpPoolFixture : IAsyncLifetime
    {
        private string _dbPath = null!;

        /// <summary>Gets the initialized pool with all discovered tools.</summary>
        public McpClientPool Pool { get; private set; } = null!;

        /// <inheritdoc/>
        public async ValueTask InitializeAsync()
        {
            this._dbPath = Path.Combine(
                Path.GetTempPath(),
                $"mcp_pool_{Guid.NewGuid():N}.db");

            McpClientOptions options = new()
            {
                Servers =
                [
                    new McpServerConfig
                    {
                        Name = "memory",
                        Transport = McpTransportKind.Stdio,
                        Command = GetServerExecutablePath(),
                        EnvironmentVariables = new Dictionary<string, string?>
                        {
                            ["Memory__Provider"] = "sqlite",
                            ["Memory__ConnectionString"] = $"Data Source={this._dbPath}"
                        }
                    }
                ]
            };

            this.Pool = await McpClientPool.CreateAsync(options);
        }

        /// <inheritdoc/>
        public async ValueTask DisposeAsync()
        {
            await this.Pool.DisposeAsync();

            if (File.Exists(this._dbPath))
            {
                File.Delete(this._dbPath);
            }
        }

        private static string GetServerExecutablePath()
        {
            // Test bin dir:   .../Harness/Agency.Harness.Test/bin/{config}/net10.0/
            // Server exe:     .../Mcp/Agency.Mcp.Memory/bin/{config}/net10.0/Agency.Mcp.Memory(.exe)
            string baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string[] segments = baseDir.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            int binIdx = Array.LastIndexOf(segments, "bin");
            string config = binIdx >= 0 && binIdx + 1 < segments.Length ? segments[binIdx + 1] : "Debug";

            string srcDir = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", ".."));
            string exeName = "Agency.Mcp.Memory" + (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : string.Empty);
            return Path.Combine(srcDir, "Mcp", "Agency.Mcp.Memory", "bin", config, "net10.0", exeName);
        }
    }
}