using System.Collections.Concurrent;
using Agency.Memory.Common.Records;
using Agency.Memory.Common.Storage;

namespace Agency.Memory.Distiller.Test.Stubs;

/// <summary>Simple in-memory implementation of <see cref="IMemoryStore"/> for testing.</summary>
internal sealed class InMemoryMemoryStore : IMemoryStore
{
    private readonly ConcurrentDictionary<string, Common.Records.Record> _byId = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastWritten = new();

    /// <summary>Gets all records in the store.</summary>
    internal IReadOnlyDictionary<string, Common.Records.Record> AllRecords => this._byId;

    /// <inheritdoc/>
    public Task<Common.Records.Record> UpsertAsync(Common.Records.Record record, CancellationToken ct = default)
    {
        string upsertKey = $"{record.UserId}|{record.SessionId}|{record.Domain}|{record.Key}";

        // Check for existing by upsert key.
        Common.Records.Record? existing = this._byId.Values.FirstOrDefault(r =>
            $"{r.UserId}|{r.SessionId}|{r.Domain}|{r.Key}" == upsertKey);

        Common.Records.Record stored;
        if (existing is not null)
        {
            stored = record with { Id = existing.Id, CreatedAt = existing.CreatedAt };
            this._byId[existing.Id] = stored;
        }
        else
        {
            stored = record;
            this._byId[stored.Id] = stored;
        }

        this._lastWritten[record.UserId] = DateTimeOffset.UtcNow;
        return Task.FromResult(stored);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var hits = this._byId.Values
            .Where(r => r.UserId == query.UserId)
            .Take(query.TopK)
            .Select(r => new SearchHit(r, 0.9))
            .ToList();
        return Task.FromResult<IReadOnlyList<SearchHit>>(hits);
    }

    /// <inheritdoc/>
    public Task<Common.Records.Record?> GetByKeyAsync(string userId, string? sessionId, string domain, string key, CancellationToken ct = default)
    {
        string upsertKey = $"{userId}|{sessionId}|{domain}|{key}";
        Common.Records.Record? found = this._byId.Values.FirstOrDefault(r =>
            $"{r.UserId}|{r.SessionId}|{r.Domain}|{r.Key}" == upsertKey);
        return Task.FromResult(found);
    }

    /// <inheritdoc/>
    public Task<bool> ForgetAsync(string userId, string domain, string key, CancellationToken ct = default)
    {
        Common.Records.Record? existing = this._byId.Values.FirstOrDefault(r =>
            r.UserId == userId && r.Domain == domain && r.Key == key);

        if (existing is null)
        {
            return Task.FromResult(false);
        }

        this._byId.TryRemove(existing.Id, out _);
        this._lastWritten[userId] = DateTimeOffset.UtcNow;
        return Task.FromResult(true);
    }

    /// <inheritdoc/>
    public Task<int> ForgetMeAsync(string userId, CancellationToken ct = default)
    {
        string[] toRemove = this._byId.Values
            .Where(r => r.UserId == userId)
            .Select(r => r.Id)
            .ToArray();

        foreach (string id in toRemove)
        {
            this._byId.TryRemove(id, out _);
        }

        if (toRemove.Length > 0)
        {
            this._lastWritten[userId] = DateTimeOffset.UtcNow;
        }

        return Task.FromResult(toRemove.Length);
    }

    /// <inheritdoc/>
    public Task<DateTimeOffset?> LastWrittenAtAsync(string userId, CancellationToken ct = default) =>
        Task.FromResult(this._lastWritten.TryGetValue(userId, out DateTimeOffset ts) ? (DateTimeOffset?)ts : null);

    /// <inheritdoc/>
    public Task<IReadOnlyList<Common.Records.Record>> GetAllForUserAsync(string userId, CancellationToken ct = default)
    {
        IReadOnlyList<Common.Records.Record> result = this._byId.Values
            .Where(r => r.UserId == userId)
            .ToList();
        return Task.FromResult(result);
    }

    /// <inheritdoc/>
    public Task<int> DeleteWhereTtlExceededAsync(ContentType ct_, TimeSpan ttl, DateTimeOffset now, CancellationToken ct = default) =>
        Task.FromResult(0);

    /// <inheritdoc/>
    public Task<int> DeleteWhereLowImportanceStaleAsync(double importanceThreshold, TimeSpan staleAge, DateTimeOffset now, CancellationToken ct = default) =>
        Task.FromResult(0);

    /// <inheritdoc/>
    public Task<Common.Records.Record> MergeAsync(
        IReadOnlyList<string> idsToDelete,
        Common.Records.Record newRecord,
        CancellationToken ct = default)
    {
        foreach (string id in idsToDelete)
        {
            this._byId.TryRemove(id, out _);
        }

        this._byId[newRecord.Id] = newRecord;
        this._lastWritten[newRecord.UserId] = DateTimeOffset.UtcNow;
        return Task.FromResult(newRecord);
    }

    /// <inheritdoc/>
    public Task<Common.Records.Record?> UpdateRecordAsync(
        string recordId,
        string userId,
        string? newValue,
        double? newImportance,
        CancellationToken ct = default)
    {
        if (!this._byId.TryGetValue(recordId, out Common.Records.Record? existing))
        {
            return Task.FromResult<Common.Records.Record?>(null);
        }

        var updated = existing with
        {
            Value = newValue ?? existing.Value,
            Importance = newImportance ?? existing.Importance,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        this._byId[recordId] = updated;
        this._lastWritten[userId] = DateTimeOffset.UtcNow;
        return Task.FromResult<Common.Records.Record?>(updated);
    }

    /// <inheritdoc/>
    public Task<bool> DeleteByIdAsync(string recordId, string userId, CancellationToken ct = default)
    {
        bool removed = this._byId.TryRemove(recordId, out _);
        if (removed)
        {
            this._lastWritten[userId] = DateTimeOffset.UtcNow;
        }

        return Task.FromResult(removed);
    }
}
