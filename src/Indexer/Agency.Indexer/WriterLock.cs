using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Agency.Indexer;

/// <summary>
/// Guarantees a single writer per index. Readers never take the lock. Both implementations are released by
/// the operating system or the database when the holding process dies, so a crash never leaves a stale lock.
/// </summary>
internal interface IWriterLock
{
    /// <summary>Tries to take the writer lock for <paramref name="index"/> without waiting.</summary>
    /// <returns>A handle that releases the lock when disposed, or <see langword="null"/> if another process holds it.</returns>
    Task<IAsyncDisposable?> TryAcquireAsync(string index, CancellationToken ct);
}

/// <summary>
/// SQLite writer lock: an exclusively opened <c>&lt;database&gt;.&lt;index&gt;.lock</c> file next to the database.
/// Not reliable on network file systems — neither is SQLite itself.
/// </summary>
internal sealed class FileWriterLock(string databasePath) : IWriterLock
{
    /// <inheritdoc/>
    public Task<IAsyncDisposable?> TryAcquireAsync(string index, CancellationToken ct)
    {
        string path = $"{databasePath}.{index}.lock";
        try
        {
            IAsyncDisposable handle = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return Task.FromResult<IAsyncDisposable?>(handle);
        }
        catch (IOException)
        {
            return Task.FromResult<IAsyncDisposable?>(null);
        }
    }
}

/// <summary>
/// PostgreSQL writer lock: a session-level advisory lock held on a dedicated, unpooled connection.
/// </summary>
internal sealed class PostgresWriterLock(string connectionString) : IWriterLock
{
    /// <inheritdoc/>
    public async Task<IAsyncDisposable?> TryAcquireAsync(string index, CancellationToken ct)
    {
        long key = LockKey(index);
        var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@k)", connection);
        command.Parameters.AddWithValue("k", key);
        if ((bool)(await command.ExecuteScalarAsync(ct))!)
        {
            return connection;
        }

        await connection.DisposeAsync();
        return null;
    }

    /// <summary>A stable 64-bit advisory-lock key derived from the index name.</summary>
    internal static long LockKey(string index) =>
        BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes($"agency-index:{index}")), 0);
}
