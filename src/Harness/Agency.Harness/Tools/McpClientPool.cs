using ModelContextProtocol.Client;

namespace Agency.Harness.Tools;

/// <summary>
/// Manages the lifetime of one <see cref="McpClient"/> per configured MCP server and exposes all
/// discovered tools as <see cref="ITool"/> instances for use with <see cref="ToolRegistry"/>.
/// </summary>
public sealed class McpClientPool : IAsyncDisposable
{
    private readonly IReadOnlyList<McpClient> _clients;

    /// <summary>Gets all tools discovered from every connected MCP server.</summary>
    public IReadOnlyList<ITool> Tools { get; }

    /// <summary>
    /// Gets the tool names discovered from each MCP server, keyed by server name in configured order.
    /// Used to attribute a tool back to its originating server (e.g. for diagnostics/inspection).
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> ToolNamesByServer { get; }

    /// <summary>Gets the error message for each server that failed to connect, keyed by server name.</summary>
    public IReadOnlyDictionary<string, string> FailedServers { get; }

    private McpClientPool(
        List<McpClient> clients,
        List<ITool> tools,
        Dictionary<string, IReadOnlyList<string>> toolNamesByServer,
        Dictionary<string, string> failedServers)
    {
        this._clients = clients;
        this.Tools = tools;
        this.ToolNamesByServer = toolNamesByServer;
        this.FailedServers = failedServers;
    }

    /// <summary>
    /// Creates a new <see cref="McpClientPool"/> by connecting to each server in <paramref name="options"/>
    /// and listing its available tools.
    /// </summary>
    /// <param name="options">The MCP server configurations to connect to.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An initialized pool whose <see cref="Tools"/> are ready for registration.</returns>
    public static async Task<McpClientPool> CreateAsync(McpClientOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<McpClient> clients = [];
        List<ITool> tools = [];
        var toolNamesByServer = new Dictionary<string, IReadOnlyList<string>>();
        var failedServers = new Dictionary<string, string>();

        foreach (McpServerConfig server in options.Servers)
        {
            try
            {
                IClientTransport transport = CreateTransport(server);
                McpClient client = await McpClient.CreateAsync(transport, cancellationToken: ct);

                IList<McpClientTool> serverTools = await client.ListToolsAsync(cancellationToken: ct);
                var names = new List<string>(serverTools.Count);
                foreach (McpClientTool tool in serverTools)
                {
                    tools.Add(new McpProxyTool(tool));
                    names.Add(tool.Name);
                }

                clients.Add(client);
                toolNamesByServer[server.Name] = names;
            }
            catch (Exception ex)
            {
                failedServers[server.Name] = ex.Message;
                continue;
            }
        }

        return new McpClientPool(clients, tools, toolNamesByServer, failedServers);
    }

    private static IClientTransport CreateTransport(McpServerConfig server) =>
        server.Transport switch
        {
            McpTransportKind.Stdio => new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = server.Name,
                Command = server.Command ?? throw new InvalidOperationException(
                    $"Command is required for Stdio transport (server '{server.Name}')."),
                Arguments = server.Arguments,
                EnvironmentVariables = server.EnvironmentVariables
            }),
            McpTransportKind.Http => new HttpClientTransport(new HttpClientTransportOptions
            {
                Name = server.Name,
                Endpoint = new Uri(server.Url ?? throw new InvalidOperationException(
                    $"Url is required for Http transport (server '{server.Name}')."))
            }),
            _ => throw new NotSupportedException(
                $"Transport kind '{server.Transport}' is not supported.")
        };

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        foreach (McpClient client in this._clients)
        {
            await client.DisposeAsync();
        }
    }
}