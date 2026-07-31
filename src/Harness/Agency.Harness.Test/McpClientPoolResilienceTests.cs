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

    /// <summary>
    /// A server with <see cref="McpServerConfig.Enabled"/> set to <see langword="false"/> must not be
    /// connected at all - even a bogus <see cref="McpServerConfig.Command"/> that would otherwise fail
    /// must never be attempted, so the server lands in <see cref="McpClientPool.DisabledServers"/> and
    /// never in <see cref="McpClientPool.FailedServers"/>.
    /// </summary>
    [Fact]
    public async Task CreateAsync_WhenServerIsDisabled_DoesNotConnectAndRecordsDisabled()
    {
        McpClientOptions options = new()
        {
            Servers =
            [
                new McpServerConfig
                {
                    Name = "disabled",
                    Transport = McpTransportKind.Stdio,
                    Command = "agency-nonexistent-command-xyz",
                    Enabled = false
                }
            ]
        };

        McpClientPool pool = await McpClientPool.CreateAsync(options);

        Assert.Empty(pool.Tools);
        Assert.Contains("disabled", pool.DisabledServers);
        Assert.DoesNotContain("disabled", pool.FailedServers.Keys);

        await pool.DisposeAsync();
    }

    /// <summary>
    /// A server whose <see cref="McpServerConfig.Enabled"/> is left unset defaults to <see langword="true"/>,
    /// so it is still connected/attempted as before - proven here by the bogus command still failing into
    /// <see cref="McpClientPool.FailedServers"/> rather than being skipped as disabled.
    /// </summary>
    [Fact]
    public async Task CreateAsync_WhenEnabledIsDefault_StillAttemptsConnection()
    {
        McpClientOptions options = new()
        {
            Servers =
            [
                new McpServerConfig
                {
                    Name = "default-enabled",
                    Transport = McpTransportKind.Stdio,
                    Command = "agency-nonexistent-command-xyz"
                }
            ]
        };

        McpClientPool pool = await McpClientPool.CreateAsync(options);

        Assert.Empty(pool.Tools);
        Assert.Contains("default-enabled", pool.FailedServers.Keys);
        Assert.DoesNotContain("default-enabled", pool.DisabledServers);

        await pool.DisposeAsync();
    }

    /// <summary>
    /// When multiple servers are disabled, <see cref="McpClientPool.DisabledServers"/> preserves their
    /// configured order.
    /// </summary>
    [Fact]
    public async Task CreateAsync_WhenMultipleServersAreDisabled_PreservesConfiguredOrder()
    {
        McpClientOptions options = new()
        {
            Servers =
            [
                new McpServerConfig { Name = "first", Transport = McpTransportKind.Stdio, Command = "agency-nonexistent-command-xyz", Enabled = false },
                new McpServerConfig { Name = "second", Transport = McpTransportKind.Stdio, Command = "agency-nonexistent-command-xyz", Enabled = false },
                new McpServerConfig { Name = "third", Transport = McpTransportKind.Stdio, Command = "agency-nonexistent-command-xyz", Enabled = false }
            ]
        };

        McpClientPool pool = await McpClientPool.CreateAsync(options);

        Assert.Equal(["first", "second", "third"], pool.DisabledServers);

        await pool.DisposeAsync();
    }
}
