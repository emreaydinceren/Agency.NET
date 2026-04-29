namespace Agency.Agentic.Tools;

/// <summary>
/// Specifies the transport mechanism for communicating with an MCP server.
/// </summary>
public enum McpTransportKind
{
    /// <summary>
    /// Stdio-based transport: spawns a subprocess and communicates via stdin/stdout.
    /// </summary>
    Stdio,

    /// <summary>
    /// HTTP-based transport: communicates with a remote MCP server via HTTP.
    /// </summary>
    Http
}

/// <summary>
/// Configuration for a single MCP server connection.
/// </summary>
public sealed class McpServerConfig
{
    /// <summary>
    /// Gets or sets the display name for the server.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the transport kind used to communicate with the server.
    /// </summary>
    public McpTransportKind Transport { get; set; } = McpTransportKind.Stdio;

    /// <summary>
    /// Gets or sets the executable path for Stdio transport. Required when <see cref="Transport"/> is <see cref="McpTransportKind.Stdio"/>.
    /// </summary>
    public string? Command { get; set; }

    /// <summary>
    /// Gets or sets the command-line arguments for the Stdio subprocess.
    /// </summary>
    public string[]? Arguments { get; set; }

    /// <summary>
    /// Gets or sets environment variables to pass to the Stdio subprocess.
    /// </summary>
    public Dictionary<string, string?>? EnvironmentVariables { get; set; }

    /// <summary>
    /// Gets or sets the endpoint URL for HTTP transport. Required when <see cref="Transport"/> is <see cref="McpTransportKind.Http"/>.
    /// </summary>
    public string? Url { get; set; }
}

/// <summary>
/// Configuration for the MCP client, containing settings for all MCP servers to connect to.
/// </summary>
public sealed class McpClientOptions
{
    /// <summary>
    /// Gets or sets the ordered list of MCP server configurations to connect to.
    /// </summary>
    public McpServerConfig[] Servers { get; set; } = [];
}