using ModelContextProtocol.Client;
using Agency.Harness.Instructions;

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

    /// <summary>
    /// Gets the names of servers that were not connected because they are disabled (<see cref="McpServerConfig.Enabled"/>
    /// is <see langword="false"/>), in configured order. A disabled server appears in neither <see cref="ToolNamesByServer"/>
    /// nor <see cref="FailedServers"/>, so this list is what lets the UI surface it for re-enabling.
    /// </summary>
    public IReadOnlyList<string> DisabledServers { get; }

    /// <summary>Gets instruction file resources discovered from every connected MCP server.</summary>
    public IReadOnlyList<InstructionSource> InstructionSources { get; }

    /// <summary>
    /// Gets whether <see cref="DisposeAsync"/> has already run. Lets a caller that owns this pool
    /// alongside other disposable resources (e.g. a per-session object graph) verify its disposal
    /// actually happened, rather than merely that disposing it did not throw.
    /// </summary>
    public bool IsDisposed { get; private set; }

    private McpClientPool(
        List<McpClient> clients,
        List<ITool> tools,
        Dictionary<string, IReadOnlyList<string>> toolNamesByServer,
        Dictionary<string, string> failedServers,
        List<string> disabledServers,
        List<InstructionSource> instructionSources)
    {
        this._clients = clients;
        this.Tools = tools;
        this.ToolNamesByServer = toolNamesByServer;
        this.FailedServers = failedServers;
        this.DisabledServers = disabledServers;
        this.InstructionSources = instructionSources;
    }

    /// <summary>
    /// Creates a new <see cref="McpClientPool"/> by connecting to each server in <paramref name="options"/>
    /// and listing its available tools and instruction resources.
    /// </summary>
    /// <param name="options">The MCP server configurations to connect to.</param>
    /// <param name="instructionFilenames">Ordered list of filenames to search for when probing MCP resources for instructions. Defaults to ["AGENTS.md"].</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An initialized pool whose <see cref="Tools"/> and <see cref="InstructionSources"/> are ready for use.</returns>
    public static async Task<McpClientPool> CreateAsync(
        McpClientOptions options,
        IReadOnlyList<string>? instructionFilenames = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        instructionFilenames ??= new[] { "AGENTS.md" };

        List<McpClient> clients = [];
        List<ITool> tools = [];
        var toolNamesByServer = new Dictionary<string, IReadOnlyList<string>>();
        var failedServers = new Dictionary<string, string>();
        List<string> disabledServers = [];
        List<InstructionSource> instructionSources = [];

        foreach (McpServerConfig server in options.Servers)
        {
            if (!server.Enabled)
            {
                disabledServers.Add(server.Name);
                continue;
            }

            IClientTransport? transport = null;
            McpResponseLoggingHandler? diagnostics = null;
            try
            {
                (transport, diagnostics) = CreateTransport(server);
                McpClient client = await McpClient.CreateAsync(transport, cancellationToken: ct);

                IList<McpClientTool> serverTools = await client.ListToolsAsync(cancellationToken: ct);
                var names = new List<string>(serverTools.Count);
                foreach (McpClientTool tool in serverTools)
                {
                    tools.Add(new McpProxyTool(tool));
                    names.Add(tool.Name);
                }

                await ProbeForInstructionResources(client, server.Name, instructionFilenames, instructionSources, ct);

                clients.Add(client);
                toolNamesByServer[server.Name] = names;
            }
            catch (Exception ex)
            {
                if (diagnostics is not null && transport is IAsyncDisposable disposableTransport)
                {
                    await disposableTransport.DisposeAsync();
                }

                string message = ex.Message;
                if (diagnostics?.FirstStatus is { } firstStatus)
                {
                    message = $"{message} (first attempt: {(int)firstStatus} {firstStatus})";
                }

                failedServers[server.Name] = message;
                continue;
            }
        }

        return new McpClientPool(clients, tools, toolNamesByServer, failedServers, disabledServers, instructionSources);
    }

    /// <summary>
    /// Probes an MCP server for instruction resources matching the configured filenames.
    /// </summary>
    private static async Task ProbeForInstructionResources(
        McpClient client,
        string serverName,
        IReadOnlyList<string> instructionFilenames,
        List<InstructionSource> results,
        CancellationToken ct)
    {
        try
        {
            var resources = await client.ListResourcesAsync(cancellationToken: ct);
            foreach (var resource in resources)
            {
                bool isMatch = false;
                foreach (var filename in instructionFilenames)
                {
                    if (resource.Name.Equals(filename, StringComparison.OrdinalIgnoreCase))
                    {
                        isMatch = true;
                        break;
                    }
                }

                if (!isMatch)
                {
                    continue;
                }

                try
                {
                    var readResult = await client.ReadResourceAsync(resource.Uri, cancellationToken: ct);
                    if (readResult.Contents is { Count: > 0 })
                    {
                        foreach (var content in readResult.Contents)
                        {
                            var aiContent = ModelContextProtocol.AIContentExtensions.ToAIContent(content);
                            if (aiContent is TextContent textContent)
                            {
                                results.Add(new InstructionSource(
                                    resource.Uri.ToString(),
                                    textContent.Text,
                                    InstructionSourceKind.Mcp,
                                    serverName));
                                break;
                            }
                        }
                    }
                }
                catch (Exception)
                {
                    // If reading this resource fails, skip it and continue to the next resource.
                }

            }
        }
        catch (Exception)
        {
            // Some MCP servers may not support the Resources capability; treat as no instruction resources.
        }
    }

    /// <summary>
    /// Builds the Http transport options for a server. Extracted as a pure function because
    /// <see cref="HttpClientTransport"/> exposes only its <c>Name</c> - the options it was
    /// constructed with cannot be read back, so this is the only seam a unit test can assert on.
    /// </summary>
    internal static HttpClientTransportOptions BuildHttpTransportOptions(McpServerConfig server) =>
        new()
        {
            Name = server.Name,
            Endpoint = new Uri(server.Url ?? throw new InvalidOperationException(
                $"Url is required for Http transport (server '{server.Name}').")),
            AdditionalHeaders = server.Headers
        };

    /// <summary>
    /// Builds the Stdio transport options for a server. Pure, for the same reason as
    /// <see cref="BuildHttpTransportOptions"/>.
    /// </summary>
    internal static StdioClientTransportOptions BuildStdioTransportOptions(McpServerConfig server) =>
        new()
        {
            Name = server.Name,
            Command = server.Command ?? throw new InvalidOperationException(
                $"Command is required for Stdio transport (server '{server.Name}')."),
            Arguments = server.Arguments,
            EnvironmentVariables = server.EnvironmentVariables
        };

    /// <summary>
    /// Creates the transport for a server. For Http, an <see cref="HttpClient"/> carrying a
    /// <see cref="McpResponseLoggingHandler"/> is installed so the first attempt's response status
    /// (e.g. a <c>401</c> the SDK's AutoDetect fallback would otherwise hide behind a later <c>404</c>)
    /// can be recovered by the caller after a connection failure.
    /// </summary>
    private static (IClientTransport Transport, McpResponseLoggingHandler? Diagnostics) CreateTransport(
        McpServerConfig server) =>
        server.Transport switch
        {
            McpTransportKind.Stdio => (new StdioClientTransport(BuildStdioTransportOptions(server)), null),
            McpTransportKind.Http => CreateHttpTransport(server),
            _ => throw new NotSupportedException(
                $"Transport kind '{server.Transport}' is not supported.")
        };

    private static (IClientTransport Transport, McpResponseLoggingHandler Diagnostics) CreateHttpTransport(
        McpServerConfig server)
    {
        var diagnostics = new McpResponseLoggingHandler { InnerHandler = new HttpClientHandler() };
        var httpClient = new HttpClient(diagnostics);
        var transport = new HttpClientTransport(BuildHttpTransportOptions(server), httpClient, loggerFactory: null, ownsHttpClient: true);
        return (transport, diagnostics);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (this.IsDisposed)
        {
            return;
        }

        this.IsDisposed = true;
        foreach (McpClient client in this._clients)
        {
            await client.DisposeAsync();
        }
    }
}