namespace Agency.Mcp.Memory;

/// <summary>
/// Configuration options for the memory MCP server.
/// </summary>
public sealed class MemoryOptions
{
    /// <summary>
    /// Gets or sets the provider to use for memory operations. Supported values are <c>sqlite</c> and <c>postgres</c>.
    /// </summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the connection string to use for memory operations. For SQLite use <c>Data Source=path_to_db</c>;
    /// for PostgreSQL use <c>Host=myserver;Database=mydb;Username=myuser;Password=mypassword</c>.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;
}
