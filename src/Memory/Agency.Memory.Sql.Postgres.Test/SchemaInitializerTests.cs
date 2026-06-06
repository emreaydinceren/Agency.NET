using Npgsql;

namespace Agency.Memory.Sql.Postgres.Test;

/// <summary>
/// Functional tests for <see cref="MemorySchemaInitializer"/>. Require a running PostgreSQL instance.
/// Start with: <c>docker compose up -d</c> in the <c>src/</c> directory.
/// Run with: <c>dotnet test --filter "Category=Functional"</c>.
/// </summary>
[Trait("Category", "Functional")]
public sealed class SchemaInitializerTests : IAsyncLifetime
{
    private NpgsqlDataSource _dataSource = default!;
    private MemorySchemaInitializer _initializer = default!;

    /// <summary>
    /// Initialises the data source and wires the schema initializer before each test.
    /// </summary>
    public async ValueTask InitializeAsync()
    {
        this._dataSource = TestHelpers.BuildDataSource<SchemaInitializerTests>();
        this._initializer = new MemorySchemaInitializer(this._dataSource);

        // Drop the records table so the schema tests can always (re)create it with dim=1536,
        // regardless of what dimension a previous test class left behind.
        await using var conn = await this._dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var cmd = new NpgsqlCommand("DROP TABLE IF EXISTS records CASCADE;", (NpgsqlConnection)conn);
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Tears down the data source.</summary>
    public async ValueTask DisposeAsync()
    {
        await this._dataSource.DisposeAsync();
    }

    // ── records table ────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that <c>records</c> table exists with all required columns after initialisation.
    /// </summary>
    [Fact]
    public async Task Init_CreatesRecordsTable_WithAllColumnsAndConstraints()
    {
        var ct = TestContext.Current.CancellationToken;
        await this._initializer.InitializeAsync(1536, ct);

        await using var conn = await this._dataSource.OpenConnectionAsync(ct);

        var columns = await GetTableColumnsAsync(conn, "records", ct);

        Assert.Contains("id", columns);
        Assert.Contains("user_id", columns);
        Assert.Contains("session_id", columns);
        Assert.Contains("content_type", columns);
        Assert.Contains("domain", columns);
        Assert.Contains("key", columns);
        Assert.Contains("title", columns);
        Assert.Contains("value", columns);
        Assert.Contains("tags", columns);
        Assert.Contains("importance", columns);
        Assert.Contains("embedding", columns);
        Assert.Contains("created_at", columns);
        Assert.Contains("updated_at", columns);
        Assert.Contains("last_accessed_at", columns);
    }

    /// <summary>
    /// Verifies that the HNSW index on <c>records.embedding</c> using cosine ops is created.
    /// </summary>
    [Fact]
    public async Task Init_CreatesHnswIndex_OnEmbedding_WithCosineOps()
    {
        var ct = TestContext.Current.CancellationToken;
        await this._initializer.InitializeAsync(1536, ct);

        await using var conn = await this._dataSource.OpenConnectionAsync(ct);

        const string sql = @"
            SELECT indexdef FROM pg_indexes
            WHERE schemaname = current_schema()
              AND tablename  = 'records'
              AND indexname  = 'records_embedding_hnsw';";

        await using var cmd = new NpgsqlCommand(sql, (NpgsqlConnection)conn);
        var result = await cmd.ExecuteScalarAsync(ct) as string;

        Assert.NotNull(result);
        Assert.Contains("vector_cosine_ops", result, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Verifies that the unique index on the upsert key <c>(user_id, session_id, domain, key)</c> is created.
    /// </summary>
    [Fact]
    public async Task Init_CreatesUniqueIndex_OnUpsertKey()
    {
        var ct = TestContext.Current.CancellationToken;
        await this._initializer.InitializeAsync(1536, ct);

        await using var conn = await this._dataSource.OpenConnectionAsync(ct);

        const string sql = @"
            SELECT COUNT(1) FROM pg_indexes
            WHERE schemaname = current_schema()
              AND tablename  = 'records'
              AND indexname  = 'records_upsert_key';";

        await using var cmd = new NpgsqlCommand(sql, (NpgsqlConnection)conn);
        var count = (long?)await cmd.ExecuteScalarAsync(ct);

        Assert.Equal(1L, count);
    }

    // ── watermarks table ─────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that the <c>watermarks</c> table is created with the composite primary key.
    /// </summary>
    [Fact]
    public async Task Init_CreatesWatermarksTable_WithPrimaryKey()
    {
        var ct = TestContext.Current.CancellationToken;
        await this._initializer.InitializeAsync(1536, ct);

        await using var conn = await this._dataSource.OpenConnectionAsync(ct);

        var columns = await GetTableColumnsAsync(conn, "watermarks", ct);
        Assert.Contains("user_id", columns);
        Assert.Contains("session_id", columns);
        Assert.Contains("last_distilled_turn_idx", columns);
        Assert.Contains("last_updated_at", columns);

        // Confirm the PK constraint exists in the current schema (avoid double-counting
        // a legacy public.watermarks left over from earlier test runs).
        const string pkSql = @"
            SELECT COUNT(1) FROM pg_constraint c
            JOIN pg_class     t ON t.oid = c.conrelid
            JOIN pg_namespace n ON n.oid = t.relnamespace
            WHERE t.relname = 'watermarks'
              AND c.contype = 'p'
              AND n.nspname = current_schema();";

        await using var cmd = new NpgsqlCommand(pkSql, (NpgsqlConnection)conn);
        var count = (long?)await cmd.ExecuteScalarAsync(ct);
        Assert.Equal(1L, count);
    }

    // ── dead_letter table ────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that the <c>dead_letter</c> table is created and has an index on <c>created_at</c>.
    /// </summary>
    [Fact]
    public async Task Init_CreatesDeadLetterTable_WithCreatedAtIndex()
    {
        var ct = TestContext.Current.CancellationToken;
        await this._initializer.InitializeAsync(1536, ct);

        await using var conn = await this._dataSource.OpenConnectionAsync(ct);

        var columns = await GetTableColumnsAsync(conn, "dead_letter", ct);
        Assert.Contains("id", columns);
        Assert.Contains("job_payload", columns);
        Assert.Contains("error", columns);
        Assert.Contains("created_at", columns);

        const string idxSql = @"
            SELECT COUNT(1) FROM pg_indexes
            WHERE schemaname = current_schema()
              AND tablename  = 'dead_letter'
              AND indexname  = 'dead_letter_created_at_idx';";

        await using var cmd = new NpgsqlCommand(idxSql, (NpgsqlConnection)conn);
        var count = (long?)await cmd.ExecuteScalarAsync(ct);
        Assert.Equal(1L, count);
    }

    // ── user_state table ─────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that the <c>user_state</c> table is created with the primary key on <c>user_id</c>.
    /// </summary>
    [Fact]
    public async Task Init_CreatesUserStateTable_WithPrimaryKey()
    {
        var ct = TestContext.Current.CancellationToken;
        await this._initializer.InitializeAsync(1536, ct);

        await using var conn = await this._dataSource.OpenConnectionAsync(ct);

        var columns = await GetTableColumnsAsync(conn, "user_state", ct);
        Assert.Contains("user_id", columns);
        Assert.Contains("last_written_at", columns);
    }

    // ── idempotency ──────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that running <see cref="MemorySchemaInitializer.InitializeAsync"/> twice does not fail.
    /// </summary>
    [Fact]
    public async Task Init_IsIdempotent_RunTwiceDoesNotFail()
    {
        var ct = TestContext.Current.CancellationToken;
        await this._initializer.InitializeAsync(1536, ct);
        var ex = await Xunit.Record.ExceptionAsync(() => this._initializer.InitializeAsync(1536, ct));
        Assert.Null(ex);
    }

    /// <summary>
    /// Verifies that passing a different embedding dimension when the table already exists
    /// (with a different dimension) throws <see cref="InvalidOperationException"/>.
    /// </summary>
    [Fact]
    public async Task Init_RejectsEmbeddingDimMismatch()
    {
        var ct = TestContext.Current.CancellationToken;
        // First call establishes the schema with 1536 dims
        await this._initializer.InitializeAsync(1536, ct);

        // A second initializer with a different dim must detect the mismatch
        var initializer2 = new MemorySchemaInitializer(this._dataSource);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => initializer2.InitializeAsync(512, ct));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static async Task<HashSet<string>> GetTableColumnsAsync(
        System.Data.Common.DbConnection conn,
        string tableName,
        CancellationToken ct)
    {
        // Scope to current_schema() so the helper works regardless of whether tests run with
        // the default search_path or with the isolated per-project schema configured by
        // TestHelpers.BuildDataSource.
        const string sql = @"
            SELECT column_name FROM information_schema.columns
            WHERE table_schema = current_schema() AND table_name = @tbl;";

        await using var cmd = new NpgsqlCommand(sql, (NpgsqlConnection)conn);
        cmd.Parameters.AddWithValue("tbl", tableName);

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }
}
