using Agency.Memory.Common.Storage;
using Microsoft.Data.Sqlite;

namespace Agency.Memory.Sql.Sqlite;

/// <summary>
/// Provisions the SQLite schema required by the Agency memory system.
/// Creates the <c>records</c>, <c>watermarks</c>, <c>dead_letter</c>, <c>user_state</c>,
/// and <c>schema_meta</c> tables if they do not already exist.
/// </summary>
/// <remarks>
/// This class is idempotent: running it multiple times is safe as long as the embedding
/// dimension does not change. Passing a dimension that differs from an already-provisioned
/// <c>records</c> table throws <see cref="InvalidOperationException"/> as a fail-fast guard
/// (per Spec §12.3 "Embedding dimension change").
/// </remarks>
public sealed class MemorySchemaInitializer : IMemorySchemaInitializer
{
    private readonly string _connectionString;

    /// <summary>
    /// Creates a new <see cref="MemorySchemaInitializer"/> using the provided connection string.
    /// </summary>
    /// <param name="connectionString">The SQLite connection string used to open connections.</param>
    public MemorySchemaInitializer(string connectionString)
    {
        this._connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
    }

    /// <summary>
    /// Provisions all required tables and indexes.
    /// Safe to call on every application start (all DDL uses <c>IF NOT EXISTS</c>).
    /// </summary>
    /// <param name="embeddingDim">The embedding dimension; must match any pre-existing schema.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the schema is ready.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the schema was previously initialised with a different embedding dimension.
    /// </exception>
    public async Task InitializeAsync(int embeddingDim, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(this._connectionString);
        await conn.OpenAsync(ct);

        await CreateSchemaMetaTableAsync(conn, ct);
        await CheckDimensionMismatchAsync(conn, embeddingDim, ct);
        await CreateRecordsTableAsync(conn, ct);
        await CreateWatermarksTableAsync(conn, ct);
        await CreateDeadLetterTableAsync(conn, ct);
        await CreateUserStateTableAsync(conn, ct);
        await CreateIndexesAsync(conn, ct);
        await PersistDimensionAsync(conn, embeddingDim, ct);
    }

    private static async Task CreateSchemaMetaTableAsync(SqliteConnection conn, CancellationToken ct)
    {
        const string sql = @"
            CREATE TABLE IF NOT EXISTS schema_meta (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );";

        await using var cmd = new SqliteCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task CheckDimensionMismatchAsync(SqliteConnection conn, int dim, CancellationToken ct)
    {
        const string sql = "SELECT value FROM schema_meta WHERE key = 'embedding_dim';";

        await using var cmd = new SqliteCommand(sql, conn);
        var result = await cmd.ExecuteScalarAsync(ct);

        if (result is null or DBNull)
        {
            return; // first init — no conflict
        }

        if (int.TryParse(result.ToString(), out int stored) && stored != dim)
        {
            throw new InvalidOperationException(
                $"The 'records.embedding' column was created with dimension {stored} " +
                $"but the current IEmbeddingGenerator produces dimension {dim}. " +
                "A schema rebuild is required to change the embedding dimension (see Spec §12.3).");
        }
    }

    private static async Task PersistDimensionAsync(SqliteConnection conn, int dim, CancellationToken ct)
    {
        const string sql = @"
            INSERT INTO schema_meta (key, value) VALUES ('embedding_dim', @dim)
            ON CONFLICT (key) DO NOTHING;";

        await using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@dim", dim.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task CreateRecordsTableAsync(SqliteConnection conn, CancellationToken ct)
    {
        const string tableSql = @"
            CREATE TABLE IF NOT EXISTS records (
                id               TEXT PRIMARY KEY,
                user_id          TEXT NOT NULL,
                session_id       TEXT NULL,
                content_type     INTEGER NOT NULL,
                domain           TEXT NOT NULL,
                key              TEXT NOT NULL,
                title            TEXT NOT NULL,
                value            TEXT NOT NULL,
                tags             TEXT NOT NULL DEFAULT '[]',
                importance       REAL NOT NULL CHECK (importance >= 0 AND importance <= 1),
                embedding        TEXT NOT NULL,
                created_at       TEXT NOT NULL,
                updated_at       TEXT NOT NULL,
                last_accessed_at TEXT NULL
            );";

        await using var cmd = new SqliteCommand(tableSql, conn);
        await cmd.ExecuteNonQueryAsync(ct);

        // Functional unique index so NULL session_id is treated as '' (one global scope per user/domain/key)
        const string idxSql = @"
            CREATE UNIQUE INDEX IF NOT EXISTS records_upsert_key
            ON records (user_id, COALESCE(session_id, ''), domain, key);";

        await using var idxCmd = new SqliteCommand(idxSql, conn);
        await idxCmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task CreateWatermarksTableAsync(SqliteConnection conn, CancellationToken ct)
    {
        const string sql = @"
            CREATE TABLE IF NOT EXISTS watermarks (
                user_id                 TEXT NOT NULL,
                session_id              TEXT NOT NULL,
                last_distilled_turn_idx INTEGER NOT NULL DEFAULT 0,
                last_updated_at         TEXT NOT NULL,
                PRIMARY KEY (user_id, session_id)
            );";

        await using var cmd = new SqliteCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task CreateDeadLetterTableAsync(SqliteConnection conn, CancellationToken ct)
    {
        const string tableSql = @"
            CREATE TABLE IF NOT EXISTS dead_letter (
                id          TEXT PRIMARY KEY,
                user_id     TEXT NOT NULL,
                session_id  TEXT NULL,
                job_kind    TEXT NOT NULL,
                job_payload TEXT NOT NULL,
                error       TEXT NOT NULL,
                stack       TEXT NULL,
                created_at  TEXT NOT NULL
            );";

        await using var cmd = new SqliteCommand(tableSql, conn);
        await cmd.ExecuteNonQueryAsync(ct);

        const string idxSql = @"
            CREATE INDEX IF NOT EXISTS dead_letter_created_at_idx
            ON dead_letter(created_at);";

        await using var idxCmd = new SqliteCommand(idxSql, conn);
        await idxCmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task CreateUserStateTableAsync(SqliteConnection conn, CancellationToken ct)
    {
        const string sql = @"
            CREATE TABLE IF NOT EXISTS user_state (
                user_id         TEXT PRIMARY KEY,
                last_written_at TEXT NOT NULL
            );";

        await using var cmd = new SqliteCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task CreateIndexesAsync(SqliteConnection conn, CancellationToken ct)
    {
        string[] indexes =
        [
            @"CREATE INDEX IF NOT EXISTS records_user_content_type_idx
              ON records (user_id, content_type);",

            @"CREATE INDEX IF NOT EXISTS records_user_domain_idx
              ON records (user_id, domain);",

            @"CREATE INDEX IF NOT EXISTS records_updated_at_idx
              ON records (updated_at);",

            @"CREATE INDEX IF NOT EXISTS records_last_accessed_at_idx
              ON records (last_accessed_at)
              WHERE last_accessed_at IS NOT NULL;",
        ];

        foreach (var sql in indexes)
        {
            // CA2100: `sql` is a compile-time literal from the fixed `indexes` array above,
            // never user input.
#pragma warning disable CA2100
            await using var cmd = new SqliteCommand(sql, conn);
#pragma warning restore CA2100
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}