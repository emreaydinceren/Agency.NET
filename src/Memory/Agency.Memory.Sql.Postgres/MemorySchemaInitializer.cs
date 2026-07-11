using Agency.Memory.Common.Storage;
using Npgsql;

namespace Agency.Memory.Sql.Postgres;

/// <summary>
/// Provisions the PostgreSQL schema required by the Agency memory system.
/// Creates the <c>records</c>, <c>watermarks</c>, <c>dead_letter</c>, and <c>user_state</c> tables
/// and their associated indexes if they do not already exist.
/// </summary>
/// <remarks>
/// This class is idempotent: running it multiple times is safe as long as the embedding
/// dimension does not change. Passing a dimension that differs from an already-provisioned
/// <c>records</c> table throws <see cref="InvalidOperationException"/> as a fail-fast guard
/// (per Spec §12.3 "Embedding dimension change").
/// </remarks>
public sealed class MemorySchemaInitializer : IMemorySchemaInitializer
{
    private readonly NpgsqlDataSource _dataSource;

    /// <summary>
    /// Creates a new <see cref="MemorySchemaInitializer"/> using the provided data source.
    /// </summary>
    /// <param name="dataSource">The Npgsql data source used to open connections.</param>
    public MemorySchemaInitializer(NpgsqlDataSource dataSource)
    {
        this._dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    /// <summary>
    /// Provisions all required tables and indexes.
    /// Safe to call on every application start (all DDL uses <c>IF NOT EXISTS</c>).
    /// </summary>
    /// <param name="embeddingDim">The embedding dimension; must match any pre-existing column.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the schema is ready.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the <c>records.embedding</c> column already exists with a different dimension.
    /// </exception>
    public async Task InitializeAsync(int embeddingDim, CancellationToken ct = default)
    {
        await using var conn = await this._dataSource.OpenConnectionAsync(ct);

        await EnsureExtensionAsync(conn, ct);

        // On a fresh database, this data source's first-ever connection (opened just above) already
        // cached Npgsql's type map before the extension existed, so the 'vector' type is unresolvable
        // for any Pgvector.Vector parameter until the map is reloaded (see Npgsql + pgvector docs).
        await this._dataSource.ReloadTypesAsync(ct);

        await CheckDimensionMismatchAsync(conn, embeddingDim, ct);
        await CreateRecordsTableAsync(conn, embeddingDim, ct);
        await CreateWatermarksTableAsync(conn, ct);
        await CreateDeadLetterTableAsync(conn, ct);
        await CreateUserStateTableAsync(conn, ct);
        await CreateIndexesAsync(conn, ct);
    }

    private static async Task EnsureExtensionAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        // Pin the extension to the public schema so the vector type is reachable from any
        // search_path that includes public (the default, and what test infrastructure relies on
        // when each test project uses its own per-project schema).
        const string sql = "CREATE EXTENSION IF NOT EXISTS vector SCHEMA public;";
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task CheckDimensionMismatchAsync(NpgsqlConnection conn, int dim, CancellationToken ct)
    {
        // Check if the column already exists and parse its declared dimension from the type name.
        // We scope to current_schema() (rather than hard-coding 'public') so that callers using
        // a custom search_path — e.g. test projects that isolate their tables into a private
        // schema — still get a correct mismatch verdict.
        const string sql = @"
            SELECT udt_name
            FROM information_schema.columns
            WHERE table_schema = current_schema()
              AND table_name   = 'records'
              AND column_name  = 'embedding';";

        await using var cmd = new NpgsqlCommand(sql, conn);
        var result = await cmd.ExecuteScalarAsync(ct);

        if (result is null or DBNull)
        {
            return; // table or column doesn't exist yet — no conflict
        }

        // The pg type name for vector(N) is stored as 'vector' in udt_name.
        // To get N we query pg_attribute / pg_type. Scope to current_schema() so we don't
        // pick up a same-named table that lives in a different schema.
        const string dimSql = @"
            SELECT atttypmod
            FROM pg_attribute a
            JOIN pg_class     c ON c.oid = a.attrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relname  = 'records'
              AND a.attname  = 'embedding'
              AND a.attnum   > 0
              AND n.nspname  = current_schema();";

        await using var dimCmd = new NpgsqlCommand(dimSql, conn);
        var raw = await dimCmd.ExecuteScalarAsync(ct);

        if (raw is int atttypmod && atttypmod > 0 && atttypmod != dim)
        {
            throw new InvalidOperationException(
                $"The 'records.embedding' column was created with dimension {atttypmod} " +
                $"but the current IEmbeddingGenerator produces dimension {dim}. " +
                "A schema rebuild is required to change the embedding dimension (see Spec §12.3).");
        }
    }

    private static async Task CreateRecordsTableAsync(NpgsqlConnection conn, int dim, CancellationToken ct)
    {
        // The unique constraint on (user_id, session_id, domain, key) must treat NULL session_id as a
        // single "global" scope, not as infinitely many distinct NULLs (Postgres default NULL != NULL
        // in unique constraints). We therefore use a functional unique index with COALESCE rather than
        // a table-level CONSTRAINT.
        string sql = $@"
            CREATE TABLE IF NOT EXISTS records (
                id               UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                user_id          TEXT NOT NULL,
                session_id       TEXT NULL,
                content_type     SMALLINT NOT NULL,
                domain           TEXT NOT NULL,
                key              TEXT NOT NULL,
                title            TEXT NOT NULL,
                value            TEXT NOT NULL,
                tags             TEXT[] NOT NULL DEFAULT '{{}}',
                importance       DOUBLE PRECISION NOT NULL CHECK (importance >= 0 AND importance <= 1),
                embedding        vector({dim}) NOT NULL,
                created_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
                updated_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
                last_accessed_at TIMESTAMPTZ NULL
            );";

        // CA2100: `sql` is built from a compile-time literal template with only `{dim}`
        // (an internally-configured int, not user input) interpolated in.
#pragma warning disable CA2100
        await using var cmd = new NpgsqlCommand(sql, conn);
#pragma warning restore CA2100
        await cmd.ExecuteNonQueryAsync(ct);

        // If the old table-level constraint exists (from a prior schema version), drop it so that
        // the functional unique index (below) can take over. Using IF EXISTS for safety.
        const string dropOldConstraint = @"
            ALTER TABLE records DROP CONSTRAINT IF EXISTS records_upsert_key;";

        await using var dropCmd = new NpgsqlCommand(dropOldConstraint, conn);
        await dropCmd.ExecuteNonQueryAsync(ct);

        // Functional unique index so that NULL session_id is treated as '' (one global scope per user/domain/key)
        const string idxSql = @"
            CREATE UNIQUE INDEX IF NOT EXISTS records_upsert_key
            ON records (user_id, COALESCE(session_id, ''), domain, key);";

        await using var idxCmd = new NpgsqlCommand(idxSql, conn);
        await idxCmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task CreateWatermarksTableAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = @"
            CREATE TABLE IF NOT EXISTS watermarks (
                user_id                 TEXT NOT NULL,
                session_id              TEXT NOT NULL,
                last_distilled_turn_idx INTEGER NOT NULL DEFAULT 0,
                last_updated_at         TIMESTAMPTZ NOT NULL DEFAULT now(),
                PRIMARY KEY (user_id, session_id)
            );";

        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task CreateDeadLetterTableAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        const string tableSql = @"
            CREATE TABLE IF NOT EXISTS dead_letter (
                id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                user_id     TEXT NOT NULL,
                session_id  TEXT NULL,
                job_kind    TEXT NOT NULL,
                job_payload JSONB NOT NULL,
                error       TEXT NOT NULL,
                stack       TEXT NULL,
                created_at  TIMESTAMPTZ NOT NULL DEFAULT now()
            );";

        await using var cmd = new NpgsqlCommand(tableSql, conn);
        await cmd.ExecuteNonQueryAsync(ct);

        const string idxSql = @"
            CREATE INDEX IF NOT EXISTS dead_letter_created_at_idx
            ON dead_letter(created_at);";

        await using var idxCmd = new NpgsqlCommand(idxSql, conn);
        await idxCmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task CreateUserStateTableAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = @"
            CREATE TABLE IF NOT EXISTS user_state (
                user_id        TEXT PRIMARY KEY,
                last_written_at TIMESTAMPTZ NOT NULL
            );";

        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task CreateIndexesAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        string[] indexes =
        [
            @"CREATE INDEX IF NOT EXISTS records_embedding_hnsw
              ON records USING hnsw (embedding vector_cosine_ops)
              WITH (m = 16, ef_construction = 64);",

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
            await using var cmd = new NpgsqlCommand(sql, conn);
#pragma warning restore CA2100
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}
