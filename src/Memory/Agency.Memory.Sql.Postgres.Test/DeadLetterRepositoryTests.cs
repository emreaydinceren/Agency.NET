using Npgsql;

namespace Agency.Memory.Sql.Postgres.Test;

/// <summary>
/// Functional tests for <see cref="DeadLetterRepository"/>.
/// </summary>
[Trait("Category", "Functional")]
public sealed class DeadLetterRepositoryTests : IAsyncLifetime
{
    private NpgsqlDataSource _dataSource = default!;
    private DeadLetterRepository _repo = default!;

    /// <summary>Initialises the repository.</summary>
    public async ValueTask InitializeAsync()
    {
        this._dataSource = TestHelpers.BuildDataSource<DeadLetterRepositoryTests>();
        this._repo = new DeadLetterRepository(this._dataSource);

        await TestHelpers.ResetSchemaAsync(this._dataSource, 1536, TestContext.Current.CancellationToken);
    }

    /// <summary>Disposes the data source.</summary>
    public async ValueTask DisposeAsync() => await this._dataSource.DisposeAsync();

    // ── tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Writing a dead-letter entry persists the payload as JSONB and the error text.
    /// </summary>
    [Fact]
    public async Task Write_PersistsJobPayloadAsJsonb_AndErrorText()
    {
        var ct = TestContext.Current.CancellationToken;
        var uid = UniqueUser();
        var before = DateTimeOffset.UtcNow.AddMinutes(-1);
        var payload = new { SessionId = "s1", TurnIndex = 42 };
        var error = new InvalidOperationException("LLM returned 400");

        await this._repo.WriteAsync(uid, "s1", "distillation", payload, error, ct);

        var entries = (await this._repo.ListSinceAsync(before, ct))
            .Where(e => e.UserId == uid)
            .ToList();
        Assert.Single(entries);
        Assert.Equal("s1", entries[0].SessionId);
        Assert.Equal("distillation", entries[0].JobKind);
        Assert.Contains("400", entries[0].Error);
        Assert.Contains("42", entries[0].JobPayloadJson);
    }

    /// <summary>
    /// ListSince returns only rows created after the specified cutoff.
    /// </summary>
    /// <remarks>
    /// The cutoff is derived from the server-assigned <c>created_at</c> of the first row so that
    /// the comparison stays in Postgres's clock frame — mixing client-side <c>UtcNow</c> with
    /// server-side <c>now()</c> failed intermittently in CI under container clock skew.
    /// All assertions are scoped to a unique user id so unrelated dead-letter rows left by
    /// other tests do not pollute the result.
    /// </remarks>
    [Fact]
    public async Task ListSince_ReturnsRowsCreatedAfterCutoff()
    {
        var ct = TestContext.Current.CancellationToken;
        var uid = UniqueUser();
        var before = DateTimeOffset.UtcNow.AddMinutes(-1);

        await this._repo.WriteAsync(uid, null, "consolidation", new { }, new Exception("err1"), ct);

        var firstRow = (await this._repo.ListSinceAsync(before, ct))
            .Single(e => e.UserId == uid);
        var cutoff = firstRow.CreatedAt;

        await Task.Delay(50, ct);
        await this._repo.WriteAsync(uid, null, "consolidation", new { }, new Exception("err2"), ct);

        var sinceStart = (await this._repo.ListSinceAsync(before, ct))
            .Where(e => e.UserId == uid)
            .ToList();
        var sinceCutoff = (await this._repo.ListSinceAsync(cutoff, ct))
            .Where(e => e.UserId == uid)
            .ToList();

        Assert.Equal(2, sinceStart.Count);
        Assert.Single(sinceCutoff);
        Assert.Contains("err2", sinceCutoff[0].Error);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static string UniqueUser() => $"u-{Guid.NewGuid():N}";

    private async Task TruncateAsync(CancellationToken ct)
    {
        await using var conn = await this._dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "TRUNCATE TABLE records, watermarks, dead_letter, user_state;",
            (NpgsqlConnection)conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
