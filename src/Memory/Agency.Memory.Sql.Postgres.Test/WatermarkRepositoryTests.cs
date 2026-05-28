using Agency.Memory.Sql.Postgres;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Agency.Memory.Sql.Postgres.Test;

/// <summary>
/// Functional tests for <see cref="WatermarkRepository"/>.
/// </summary>
[Trait("Category", "Functional")]
public sealed class WatermarkRepositoryTests : IAsyncLifetime
{
    private NpgsqlDataSource _dataSource = default!;
    private WatermarkRepository _repo = default!;
    private readonly string _runId = Guid.NewGuid().ToString("N")[..8];

    /// <summary>Initialises the repository.</summary>
    public async ValueTask InitializeAsync()
    {
        this._dataSource = TestHelpers.BuildDataSource<WatermarkRepositoryTests>();
        this._repo = new WatermarkRepository(this._dataSource);

        await TestHelpers.ResetSchemaAsync(this._dataSource, 1536, TestContext.Current.CancellationToken);
    }

    /// <summary>Disposes the data source.</summary>
    public async ValueTask DisposeAsync() => await this._dataSource.DisposeAsync();

    // ── tests ─────────────────────────────────────────────────────────────────

    /// <summary>Getting a watermark for an unknown session returns 0.</summary>
    [Fact]
    public async Task Get_UnknownSession_ReturnsZero()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await this._repo.GetAsync($"u-{this._runId}", "s-unknown", ct);
        Assert.Equal(0, result);
    }

    /// <summary>Advancing with a lower value after a higher value has no effect.</summary>
    [Fact]
    public async Task Advance_MonotonicallyIncreases_OldValueIgnored()
    {
        var ct = TestContext.Current.CancellationToken;
        var uid = UniqueUser();
        const string sid = "s1";

        await this._repo.AdvanceAsync(uid, sid, 5, ct);
        int after = await this._repo.AdvanceAsync(uid, sid, 3, ct);

        Assert.Equal(5, after);
        Assert.Equal(5, await this._repo.GetAsync(uid, sid, ct));
    }

    /// <summary>A fresh repository instance hydrates the watermark from the database.</summary>
    [Fact]
    public async Task Advance_RestartHydrationFromDb()
    {
        var ct = TestContext.Current.CancellationToken;
        var uid = UniqueUser();
        const string sid = "s2";

        await this._repo.AdvanceAsync(uid, sid, 10, ct);

        // New instance — no in-memory cache
        var repo2 = new WatermarkRepository(this._dataSource);
        var value = await repo2.GetAsync(uid, sid, ct);

        Assert.Equal(10, value);
    }

    /// <summary>Deleting a watermark removes the row.</summary>
    [Fact]
    public async Task Delete_OnSessionDispose_RemovesRow()
    {
        var ct = TestContext.Current.CancellationToken;
        var uid = UniqueUser();
        const string sid = "s3";

        await this._repo.AdvanceAsync(uid, sid, 7, ct);
        await this._repo.DeleteAsync(uid, sid, ct);

        // A new repo (no cache) sees 0
        var repo2 = new WatermarkRepository(this._dataSource);
        var value = await repo2.GetAsync(uid, sid, ct);
        Assert.Equal(0, value);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private string UniqueUser() => $"u-{Guid.NewGuid():N}";

    private async Task TruncateAsync(CancellationToken ct)
    {
        await using var conn = await this._dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "TRUNCATE TABLE records, watermarks, dead_letter, user_state;",
            (NpgsqlConnection)conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
