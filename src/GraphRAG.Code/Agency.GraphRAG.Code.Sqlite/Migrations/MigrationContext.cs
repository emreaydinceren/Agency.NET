namespace Agency.GraphRAG.Code.Sqlite.Migrations;

/// <summary>
/// Supplies migration-time configuration values for the SQLite GraphRAG schema.
/// </summary>
public sealed record class MigrationContext
{
    /// <summary>
    /// Gets the default embedding width used by vector virtual tables.
    /// </summary>
    public const int DefaultEmbeddingDimensions = 1536;

    /// <summary>
    /// Gets the embedding width used when creating vector virtual tables.
    /// </summary>
    public int EmbeddingDimensions { get; init; } = DefaultEmbeddingDimensions;
}
