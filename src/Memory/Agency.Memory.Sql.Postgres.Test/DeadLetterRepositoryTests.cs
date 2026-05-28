using Agency.Memory.Sql.Postgres;
using Microsoft.Extensions.Configuration;
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
        var config = new ConfigurationBuilder()
            .AddUserSecrets<DeadLetterRepositoryTests>()
            .AddEnvironmentVariables()
            .Build();

        var cs = config.GetConnectionString("PostgreSql")
            ?? throw new InvalidOperationException("Connection string 'PostgreSql' not found.");

        var builder = new NpgsqlDataSourceBuilder(cs);
        builder.UseVector();
        this._dataSource = builder.Build();
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
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var payload = new { SessionId = "s1", TurnIndex = 42 };
        var error = new InvalidOperationException("LLM returned 400");

        await this._repo.WriteAsync("u1", "s1", "distillation", payload, error, ct);

        var entries = await this._repo.ListSinceAsync(before, ct);
        Assert.Single(entries);
        Assert.Equal("u1", entries[0].UserId);
        Assert.Equal("s1", entries[0].SessionId);
        Assert.Equal("distillation", entries[0].JobKind);
        Assert.Contains("400", entries[0].Error);
        Assert.Contains("42", entries[0].JobPayloadJson);
    }

    /// <summary>
    /// ListSince returns only rows created after the specified cutoff.
    /// </summary>
    [Fact]
    public async Task ListSince_ReturnsRowsCreatedAfterCutoff()
    {
        var ct = TestContext.Current.CancellationToken;
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        await this._repo.WriteAsync("u1", null, "consolidation", new { }, new Exception("err1"), ct);
        await Task.Delay(50, ct);
        var cutoff = DateTimeOffset.UtcNow;
        await Task.Delay(50, ct);
        await this._repo.WriteAsync("u1", null, "consolidation", new { }, new Exception("err2"), ct);

        var sinceStart = await this._repo.ListSinceAsync(before, ct);
        var sinceCutoff = await this._repo.ListSinceAsync(cutoff, ct);

        Assert.Equal(2, sinceStart.Count);
        Assert.Single(sinceCutoff);
        Assert.Contains("err2", sinceCutoff[0].Error);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private async Task TruncateAsync(CancellationToken ct)
    {
        await using var conn = await this._dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "TRUNCATE TABLE records, watermarks, dead_letter, user_state;",
            (NpgsqlConnection)conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
