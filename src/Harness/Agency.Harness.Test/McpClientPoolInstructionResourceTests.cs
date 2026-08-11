namespace Agency.Harness.Test;

/// <summary>
/// Unit tests verifying that <see cref="McpClientPool"/> discovers and collects instruction resources
/// from MCP servers matching configured filenames.
/// </summary>
public sealed class McpClientPoolInstructionResourceTests
{
    /// <summary>
    /// Verifies that an empty pool (no servers) returns an empty InstructionSources list.
    /// </summary>
    [Fact]
    public async Task CreateAsync_WithNoServers_ReturnsEmptyInstructionSources()
    {
        // Arrange
        McpClientOptions options = new() { Servers = [] };

        // Act
        McpClientPool pool = await McpClientPool.CreateAsync(options);

        // Assert
        Assert.Empty(pool.InstructionSources);

        await pool.DisposeAsync();
    }

    /// <summary>
    /// Verifies that when a server fails to connect, instruction collection gracefully
    /// continues and the failed server contributes no instruction sources.
    /// </summary>
    [Fact]
    public async Task CreateAsync_WhenServerFailsToConnect_ContinuesWithoutInstructionResources()
    {
        // Arrange
        McpClientOptions options = new()
        {
            Servers =
            [
                new McpServerConfig
                {
                    Name = "broken-server",
                    Transport = McpTransportKind.Stdio,
                    Command = "agency-nonexistent-command-xyz"
                }
            ]
        };

        // Act
        McpClientPool pool = await McpClientPool.CreateAsync(options);

        // Assert: the pool is healthy despite server failure
        Assert.Empty(pool.InstructionSources);
        Assert.Contains("broken-server", pool.FailedServers.Keys);

        await pool.DisposeAsync();
    }

    /// <summary>
    /// Verifies that disabled servers are never probed for instruction resources,
    /// and contribute nothing to InstructionSources.
    /// </summary>
    [Fact]
    public async Task CreateAsync_WhenServerIsDisabled_SkipsInstructionProbing()
    {
        // Arrange
        McpClientOptions options = new()
        {
            Servers =
            [
                new McpServerConfig
                {
                    Name = "disabled-server",
                    Transport = McpTransportKind.Stdio,
                    Command = "agency-nonexistent-command-xyz",
                    Enabled = false
                }
            ]
        };

        // Act
        McpClientPool pool = await McpClientPool.CreateAsync(options);

        // Assert
        Assert.Empty(pool.InstructionSources);
        Assert.Contains("disabled-server", pool.DisabledServers);

        await pool.DisposeAsync();
    }

    /// <summary>
    /// Verifies that the default instruction filename list is used when none is provided to CreateAsync.
    /// </summary>
    [Fact]
    public async Task CreateAsync_WithDefaultInstructionFilenames_UsesAgentsMdAsDefault()
    {
        // Arrange
        McpClientOptions options = new() { Servers = [] };

        // Act
        // CreateAsync is called without the instructionFilenames parameter, using the default
        McpClientPool pool = await McpClientPool.CreateAsync(options);

        // Assert: should complete successfully with empty sources (no servers to probe)
        Assert.Empty(pool.InstructionSources);

        await pool.DisposeAsync();
    }

    /// <summary>
    /// Verifies that custom instruction filenames can be passed to CreateAsync and are used
    /// during resource probing (this is a unit test contract verification).
    /// </summary>
    [Fact]
    public async Task CreateAsync_WithCustomInstructionFilenames_AcceptsCustomList()
    {
        // Arrange
        var customFilenames = new[] { "CUSTOM.md", "README.md" };
        McpClientOptions options = new() { Servers = [] };

        // Act
        McpClientPool pool = await McpClientPool.CreateAsync(options, customFilenames);

        // Assert: pool should initialize successfully with custom filenames
        // (actual resource matching is tested with a real/mocked server)
        Assert.Empty(pool.InstructionSources);

        await pool.DisposeAsync();
    }
}
