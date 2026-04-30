namespace Agency.GraphRAG.Code.DependencyInjection;

/// <summary>
/// Supported backing stores for the code index.
/// </summary>
public enum CodeIndexStore
{
    /// <summary>SQLite-backed graph storage.</summary>
    Sqlite,

    /// <summary>PostgreSQL-backed graph storage.</summary>
    Postgres,
}

/// <summary>
/// Configures GraphRAG code-index services.
/// </summary>
public sealed class CodeIndexOptions
{
    /// <summary>Gets or sets the backing store.</summary>
    public CodeIndexStore Store { get; set; } = CodeIndexStore.Sqlite;

    /// <summary>Gets or sets the connection string used by the selected store.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Gets or sets the SQLite database file path when <see cref="Store"/> is SQLite.</summary>
    public string? SqlitePath { get; set; }

    /// <summary>Gets or sets the working directory used to derive default SQLite paths.</summary>
    public string WorkingDirectory { get; set; } = Directory.GetCurrentDirectory();

    /// <summary>Gets or sets the default SQLite database file name.</summary>
    public string DefaultSqliteFileName { get; set; } = "graphrag-code.db";
}
