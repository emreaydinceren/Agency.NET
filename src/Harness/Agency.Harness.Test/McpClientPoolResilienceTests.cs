namespace Agency.Harness.Test;

/// <summary>
/// Unit tests verifying that <see cref="McpClientPool"/> tolerates per-server connection failures
/// instead of throwing out of <see cref="McpClientPool.CreateAsync"/>.
/// </summary>
public sealed class McpClientPoolResilienceTests
{
    /// <summary>
    /// A server whose command cannot be spawned must not fail <see cref="McpClientPool.CreateAsync"/>;
    /// the failure must be recorded on <see cref="McpClientPool.FailedServers"/> instead.
    /// </summary>
    [Fact]
    public async Task CreateAsync_WhenServerCommandDoesNotExist_DoesNotThrowAndRecordsFailure()
    {
        McpClientOptions options = new()
        {
            Servers =
            [
                new McpServerConfig
                {
                    Name = "broken",
                    Transport = McpTransportKind.Stdio,
                    Command = "agency-nonexistent-command-xyz"
                }
            ]
        };

        McpClientPool pool = await McpClientPool.CreateAsync(options);

        Assert.Empty(pool.Tools);
        Assert.Contains("broken", pool.FailedServers.Keys);

        await pool.DisposeAsync();
    }
}
