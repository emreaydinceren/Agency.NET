using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Agency.Mcp.Memory.Test;

/// <summary>
/// Functional tests that exercise <see cref="MemoryTool"/> through a real MCP client connection,
/// simulating how an AI agent would discover and invoke memory tools over the MCP protocol.
/// A single <see cref="McpClientFixture"/> is shared across all tests so the server process starts once per class.
/// </summary>
[Trait("Category", "Functional")]
public sealed class MemoryMcpClientFunctionalTests : IClassFixture<MemoryMcpClientFunctionalTests.McpClientFixture>
{
    private readonly McpClientFixture _fixture;

    /// <summary>
    /// Initializes a new instance of <see cref="MemoryMcpClientFunctionalTests"/> with the shared fixture.
    /// </summary>
    public MemoryMcpClientFunctionalTests(McpClientFixture fixture)
    {
        this._fixture = fixture;
    }

    // ── Tool discovery ────────────────────────────────────────────────────────

    /// <summary>
    /// An AI agent connecting to the server should discover the three memory tools via the MCP protocol.
    /// </summary>
    [Fact]
    public async Task ListTools_ReturnsMemorizeRecallAndForget()
    {
        IList<McpClientTool> tools = await this._fixture.Client.ListToolsAsync(
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(tools, t => t.Name == "memorize");
        Assert.Contains(tools, t => t.Name == "recall");
        Assert.Contains(tools, t => t.Name == "forget");
    }

    // ── Round-trip ────────────────────────────────────────────────────────────

    /// <summary>
    /// An AI agent stores a fact via memorize and retrieves it via recall — exercising the full MCP round-trip.
    /// </summary>
    [Fact]
    public async Task Memorize_Then_Recall_ReturnsStoredFact()
    {
        var scope = new { UserId = "mcp-u1", SessionId = "mcp-s1" };
        CancellationToken ct = TestContext.Current.CancellationToken;

        CallToolResult memorizeResult = await this._fixture.Client.CallToolAsync(
            "memorize",
            new Dictionary<string, object?>
            {
                ["record"] = new
                {
                    Scope = scope,
                    Domain = "geography",
                    Key = "capital-of-france",
                    Value = "Paris"
                }
            },
            cancellationToken: ct);

        Assert.True(memorizeResult.IsError is not true, GetText(memorizeResult));

        CallToolResult recallResult = await this._fixture.Client.CallToolAsync(
            "recall",
            new Dictionary<string, object?>
            {
                ["scope"] = scope,
                ["domain"] = "geography",
                ["key"] = "capital-of-france",
                ["tags"] = null
            },
            cancellationToken: ct);

        Assert.True(recallResult.IsError is not true, GetText(recallResult));
        string json = GetText(recallResult);
        using var doc = JsonDocument.Parse(json);
        Assert.Contains(
            doc.RootElement.EnumerateArray(),
            e => e.GetProperty("Value").GetString()!.Contains("Paris"));
    }

    // ── Forget ────────────────────────────────────────────────────────────────

    /// <summary>
    /// An AI agent that stores and then forgets a fact should receive an empty result on subsequent recall.
    /// </summary>
    [Fact]
    public async Task Memorize_Forget_Recall_ReturnsNoHits()
    {
        var scope = new { UserId = "mcp-u2", SessionId = "mcp-s2" };
        CancellationToken ct = TestContext.Current.CancellationToken;

        await this._fixture.Client.CallToolAsync(
            "memorize",
            new Dictionary<string, object?>
            {
                ["record"] = new { Scope = scope, Domain = "ephemeral", Key = "temp", Value = "to-be-forgotten" }
            },
            cancellationToken: ct);

        CallToolResult forgetResult = await this._fixture.Client.CallToolAsync(
            "forget",
            new Dictionary<string, object?>
            {
                ["scope"] = scope,
                ["domain"] = "ephemeral",
                ["key"] = "temp"
            },
            cancellationToken: ct);

        Assert.True(forgetResult.IsError is not true, GetText(forgetResult));

        CallToolResult recallResult = await this._fixture.Client.CallToolAsync(
            "recall",
            new Dictionary<string, object?>
            {
                ["scope"] = scope,
                ["domain"] = "ephemeral",
                ["key"] = "temp",
                ["tags"] = null
            },
            cancellationToken: ct);

        string json = GetText(recallResult);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(0, doc.RootElement.GetArrayLength());
    }

    // ── Tags ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Recalling with a tag filter should return only entries that carry that tag, just as an AI agent would use scoped memory.
    /// </summary>
    [Fact]
    public async Task Recall_WithTagFilter_ReturnsOnlyTaggedEntries()
    {
        var scope = new { UserId = "mcp-u3", SessionId = "mcp-s3" };
        string suffix = Guid.NewGuid().ToString("N")[..6];
        CancellationToken ct = TestContext.Current.CancellationToken;

        await this._fixture.Client.CallToolAsync(
            "memorize",
            new Dictionary<string, object?>
            {
                ["record"] = new
                {
                    Scope = scope,
                    Domain = "notes",
                    Key = $"important-{suffix}",
                    Value = "critical information",
                    Tags = new[] { "important", "work" }
                }
            },
            cancellationToken: ct);

        await this._fixture.Client.CallToolAsync(
            "memorize",
            new Dictionary<string, object?>
            {
                ["record"] = new
                {
                    Scope = scope,
                    Domain = "notes",
                    Key = $"personal-{suffix}",
                    Value = "personal note",
                    Tags = new[] { "personal" }
                }
            },
            cancellationToken: ct);

        CallToolResult result = await this._fixture.Client.CallToolAsync(
            "recall",
            new Dictionary<string, object?>
            {
                ["scope"] = scope,
                ["domain"] = null,
                ["key"] = null,
                ["tags"] = new[] { "important" }
            },
            cancellationToken: ct);

        string json = GetText(result);
        Assert.True(result.IsError is not true, $"Recall failed: {json}");
        using var doc = JsonDocument.Parse(json);
        var hits = doc.RootElement.EnumerateArray().ToList();

        Assert.Contains(hits, e => e.GetProperty("Key").GetString()!.Contains($"important-{suffix}"));
        Assert.DoesNotContain(hits, e => e.GetProperty("Key").GetString()!.Contains($"personal-{suffix}"));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string GetText(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    // ── Fixture ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts the <c>Agency.Mcp.Memory</c> server as a subprocess with an ephemeral SQLite database,
    /// and establishes an MCP client session before any tests run.
    /// </summary>
    public sealed class McpClientFixture : IAsyncLifetime
    {
        private string _dbPath = null!;

        /// <summary>Gets the MCP client connected to the running server.</summary>
        public McpClient Client { get; private set; } = null!;

        /// <inheritdoc/>
        public async ValueTask InitializeAsync()
        {
            this._dbPath = Path.Combine(
                Path.GetTempPath(),
                $"mcp_memory_{Guid.NewGuid():N}.db");

            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Command = GetServerExecutablePath(),
                EnvironmentVariables = new Dictionary<string, string?>
                {
                    ["Memory__Provider"] = "sqlite",
                    ["Memory__ConnectionString"] = $"Data Source={this._dbPath}"
                }
            });

            this.Client = await McpClient.CreateAsync(transport);
        }

        /// <inheritdoc/>
        public async ValueTask DisposeAsync()
        {
            await this.Client.DisposeAsync();

            if (File.Exists(this._dbPath))
            {
                File.Delete(this._dbPath);
            }
        }

        private static string GetServerExecutablePath()
        {
            // Test bin dir:   .../Mcp/Agency.Mcp.Memory.Test/bin/{config}/net10.0/
            // Server exe:     .../Mcp/Agency.Mcp.Memory/bin/{config}/net10.0/Agency.Mcp.Memory(.exe)
            string baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string[] segments = baseDir.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            int binIdx = Array.LastIndexOf(segments, "bin");
            string config = binIdx >= 0 && binIdx + 1 < segments.Length ? segments[binIdx + 1] : "Debug";

            string mcpDir = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".."));
            string exeName = "Agency.Mcp.Memory" + (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : string.Empty);
            return Path.Combine(mcpDir, "Agency.Mcp.Memory", "bin", config, "net10.0", exeName);
        }
    }
}
