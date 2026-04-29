using System.Text.Json;
using ModelContextProtocol.Client;

namespace Agency.Agentic.Tools;

/// <summary>
/// Manages the lifetime of one <see cref="McpClient"/> per configured MCP server and exposes all
/// discovered tools as <see cref="ITool"/> instances for use with <see cref="ToolRegistry"/>.
/// </summary>
public sealed class McpClientPool : IAsyncDisposable
{
    private readonly IReadOnlyList<McpClient> _clients;

    /// <summary>Gets all tools discovered from every connected MCP server.</summary>
    public IReadOnlyList<ITool> Tools { get; }

    private McpClientPool(List<McpClient> clients, List<ITool> tools)
    {
        this._clients = clients;
        this.Tools = tools;
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
        List<McpClient> clients = [];
        List<ITool> tools = [];

        foreach (McpServerConfig server in options.Servers)
        {
            IClientTransport transport = CreateTransport(server);
            McpClient client = await McpClient.CreateAsync(transport, cancellationToken: ct);
            clients.Add(client);

            IList<McpClientTool> serverTools = await client.ListToolsAsync(cancellationToken: ct);
            foreach (McpClientTool tool in serverTools)
            {
                tools.Add(new McpProxyTool(tool));
            }
        }

        return new McpClientPool(clients, tools);
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