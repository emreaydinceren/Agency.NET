using Microsoft.Data.Sqlite;

namespace Agency.Memory.Sql.Sqlite.Test;

/// <summary>
/// Unit tests for <see cref="SqliteWatermarkRepository"/> against an in-memory SQLite database.
/// </summary>
public sealed class WatermarkRepositoryTests : IAsyncLifetime
{
    private string _connectionString = default!;
    private SqliteConnection _keepAlive = default!;
    private SqliteWatermarkRepository _repo = default!;

    /// <summary>Initialises a fresh in-memory DB and schema before each test.</summary>
    public async ValueTask InitializeAsync()
    {
        this._connectionString = TestHelpers.BuildConnectionString();
        this._keepAlive = TestHelpers.OpenKeepAlive(this._connectionString);
        await new MemorySchemaInitializer(this._connectionString).InitializeAsync(2, TestContext.Current.CancellationToken);
        this._repo = new SqliteWatermarkRepository(this._connectionString);
    }

    /// <summary>Closes the keep-alive connection, destroying the in-memory DB.</summary>
    public async ValueTask DisposeAsync()
    {
        await this._keepAlive.CloseAsync();
        this._keepAlive.Dispose();
    }

    // ── tests ─────────────────────────────────────────────────────────────────

    /// <summary>Getting a watermark for an unknown session returns 0.</summary>
    [Fact]
    public async Task Get_UnknownSession_ReturnsZero()
    {
        var ct = TestContext.Current.CancellationToken;
        int result = await this._repo.GetAsync("u-unknown", "s-unknown", ct);
        Assert.Equal(0, result);
    }

    /// <summary>Advancing with a lower value after a higher value does not regress the watermark.</summary>
    [Fact]
    public async Task Advance_MonotonicallyIncreases_OldValueIgnored()
    {
        var ct = TestContext.Current.CancellationToken;
        string uid = UniqueUser();
        const string sid = "s1";

        await this._repo.AdvanceAsync(uid, sid, 5, ct);
        int after = await this._repo.AdvanceAsync(uid, sid, 3, ct);

        Assert.Equal(5, after);
        Assert.Equal(5, await this._repo.GetAsync(uid, sid, ct));
    }

    /// <summary>A fresh repository instance hydrates the watermark from the database on cache miss.</summary>
    [Fact]
    public async Task Advance_RestartHydrationFromDb()
    {
        var ct = TestContext.Current.CancellationToken;
        string uid = UniqueUser();
        const string sid = "s2";

        await this._repo.AdvanceAsync(uid, sid, 10, ct);

        // New instance — no in-memory cache
        var repo2 = new SqliteWatermarkRepository(this._connectionString);
        int value = await repo2.GetAsync(uid, sid, ct);

        Assert.Equal(10, value);
    }

    /// <summary>Deleting a watermark removes the row; a new repo instance sees 0 on cache miss.</summary>
    [Fact]
    public async Task Delete_OnSessionDispose_RemovesRow()
    {
        var ct = TestContext.Current.CancellationToken;
        string uid = UniqueUser();
        const string sid = "s3";

        await this._repo.AdvanceAsync(uid, sid, 7, ct);
        await this._repo.DeleteAsync(uid, sid, ct);

        // A new repo (no cache) sees 0
        var repo2 = new SqliteWatermarkRepository(this._connectionString);
        int value = await repo2.GetAsync(uid, sid, ct);
        Assert.Equal(0, value);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static string UniqueUser() => $"u-{Guid.NewGuid():N}";
}