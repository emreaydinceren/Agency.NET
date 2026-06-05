using Microsoft.Data.Sqlite;

namespace Agency.Memory.Sql.Sqlite.Test;

/// <summary>
/// Unit tests for <see cref="MemorySchemaInitializer"/> against an in-memory SQLite database.
/// </summary>
public sealed class SchemaInitializerTests : IAsyncLifetime
{
    private string _connectionString = default!;
    private SqliteConnection _keepAlive = default!;
    private MemorySchemaInitializer _initializer = default!;

    /// <summary>Initialises a fresh in-memory DB before each test.</summary>
    public async ValueTask InitializeAsync()
    {
        this._connectionString = TestHelpers.BuildConnectionString();
        this._keepAlive = TestHelpers.OpenKeepAlive(this._connectionString);
        this._initializer = new MemorySchemaInitializer(this._connectionString);
        await Task.CompletedTask;
    }

    /// <summary>Closes the keep-alive connection, destroying the in-memory DB.</summary>
    public async ValueTask DisposeAsync()
    {
        await this._keepAlive.CloseAsync();
        this._keepAlive.Dispose();
    }

    // ── tests ─────────────────────────────────────────────────────────────────

    /// <summary>Running InitializeAsync twice on the same DB does not throw.</summary>
    [Fact]
    public async Task Init_IsIdempotent_RunTwiceDoesNotFail()
    {
        var ct = TestContext.Current.CancellationToken;
        await this._initializer.InitializeAsync(2, ct);
        var ex = await Record.ExceptionAsync(() => this._initializer.InitializeAsync(2, ct));
        Assert.Null(ex);
    }

    /// <summary>
    /// Passing a different embedding dimension when the schema was created with another dimension
    /// throws <see cref="InvalidOperationException"/>.
    /// </summary>
    [Fact]
    public async Task Init_RejectsEmbeddingDimMismatch()
    {
        var ct = TestContext.Current.CancellationToken;
        await this._initializer.InitializeAsync(2, ct);

        var initializer2 = new MemorySchemaInitializer(this._connectionString);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => initializer2.InitializeAsync(3, ct));
    }

    /// <summary>The records table exists after initialization.</summary>
    [Fact]
    public async Task Init_CreatesRecordsTable()
    {
        var ct = TestContext.Current.CancellationToken;
        await this._initializer.InitializeAsync(2, ct);

        bool exists = await TableExistsAsync("records", ct);
        Assert.True(exists);
    }

    /// <summary>The watermarks table exists after initialization.</summary>
    [Fact]
    public async Task Init_CreatesWatermarksTable()
    {
        var ct = TestContext.Current.CancellationToken;
        await this._initializer.InitializeAsync(2, ct);

        bool exists = await TableExistsAsync("watermarks", ct);
        Assert.True(exists);
    }

    /// <summary>The dead_letter table exists after initialization.</summary>
    [Fact]
    public async Task Init_CreatesDeadLetterTable()
    {
        var ct = TestContext.Current.CancellationToken;
        await this._initializer.InitializeAsync(2, ct);

        bool exists = await TableExistsAsync("dead_letter", ct);
        Assert.True(exists);
    }

    /// <summary>The user_state table exists after initialization.</summary>
    [Fact]
    public async Task Init_CreatesUserStateTable()
    {
        var ct = TestContext.Current.CancellationToken;
        await this._initializer.InitializeAsync(2, ct);

        bool exists = await TableExistsAsync("user_state", ct);
        Assert.True(exists);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private async Task<bool> TableExistsAsync(string tableName, CancellationToken ct)
    {
        const string sql = "SELECT name FROM sqlite_master WHERE type='table' AND name=@name;";
        await using var conn = new SqliteConnection(this._connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@name", tableName);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is not null;
    }
}