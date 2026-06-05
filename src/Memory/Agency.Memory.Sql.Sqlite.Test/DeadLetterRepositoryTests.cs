using Microsoft.Data.Sqlite;

namespace Agency.Memory.Sql.Sqlite.Test;

/// <summary>
/// Unit tests for <see cref="SqliteDeadLetterRepository"/> against an in-memory SQLite database.
/// </summary>
public sealed class DeadLetterRepositoryTests : IAsyncLifetime
{
    private string _connectionString = default!;
    private SqliteConnection _keepAlive = default!;
    private SqliteDeadLetterRepository _repo = default!;

    /// <summary>Initialises a fresh in-memory DB and schema before each test.</summary>
    public async ValueTask InitializeAsync()
    {
        this._connectionString = TestHelpers.BuildConnectionString();
        this._keepAlive = TestHelpers.OpenKeepAlive(this._connectionString);
        await new MemorySchemaInitializer(this._connectionString).InitializeAsync(2, TestContext.Current.CancellationToken);
        this._repo = new SqliteDeadLetterRepository(this._connectionString);
    }

    /// <summary>Closes the keep-alive connection, destroying the in-memory DB.</summary>
    public async ValueTask DisposeAsync()
    {
        await this._keepAlive.CloseAsync();
        this._keepAlive.Dispose();
    }

    // ── tests ─────────────────────────────────────────────────────────────────

    /// <summary>Writing a dead-letter entry persists the payload as JSON and the error text.</summary>
    [Fact]
    public async Task Write_PersistsJobPayloadAsJson_AndErrorText()
    {
        var ct = TestContext.Current.CancellationToken;
        string uid = UniqueUser();
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

    /// <summary>ListSince returns only rows created after the specified cutoff.</summary>
    [Fact]
    public async Task ListSince_ReturnsRowsCreatedAfterCutoff()
    {
        var ct = TestContext.Current.CancellationToken;
        string uid = UniqueUser();
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
}