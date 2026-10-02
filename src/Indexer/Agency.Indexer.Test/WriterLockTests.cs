using Microsoft.Extensions.Configuration;

namespace Agency.Indexer.Test;

/// <summary>Tests for the <see cref="IWriterLock"/> implementations.</summary>
public sealed class WriterLockTests
{
    /// <summary>Verifies the file lock admits one holder per index and is reusable after release.</summary>
    [Fact]
    public async Task FileWriterLock_SecondAcquire_FailsUntilReleased()
    {
        using var dir = new TempDirectory();
        var writerLock = new FileWriterLock(Path.Combine(dir.Path, "index.db"));
        CancellationToken ct = TestContext.Current.CancellationToken;

        IAsyncDisposable? first = await writerLock.TryAcquireAsync("docs", ct);
        Assert.NotNull(first);
        Assert.Null(await writerLock.TryAcquireAsync("docs", ct));
        await using (IAsyncDisposable? other = await writerLock.TryAcquireAsync("other", ct))
        {
            Assert.NotNull(other);
        }

        await first.DisposeAsync();
        await using IAsyncDisposable? again = await writerLock.TryAcquireAsync("docs", ct);
        Assert.NotNull(again);
    }

    /// <summary>Verifies the Postgres advisory lock admits one holder per index and is reusable after release.</summary>
    [Trait("Category", "Functional")]
    [Fact]
    public async Task PostgresWriterLock_SecondAcquire_FailsUntilReleased()
    {
        string connectionString = new ConfigurationBuilder().AddEnvironmentVariables().Build().GetConnectionString("PostgreSql")
            ?? throw new InvalidOperationException("Set ConnectionStrings__PostgreSql to run this test.");
        var writerLock = new PostgresWriterLock(connectionString);
        CancellationToken ct = TestContext.Current.CancellationToken;
        string index = $"lock-test-{Guid.NewGuid():N}";

        IAsyncDisposable? first = await writerLock.TryAcquireAsync(index, ct);
        Assert.NotNull(first);
        Assert.Null(await writerLock.TryAcquireAsync(index, ct));

        await first.DisposeAsync();
        await using IAsyncDisposable? again = await writerLock.TryAcquireAsync(index, ct);
        Assert.NotNull(again);
    }
}
