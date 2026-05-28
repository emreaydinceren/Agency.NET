using Agency.Memory.Common.Records;

namespace Agency.Memory.Common.Storage;

/// <summary>
/// Abstraction for the vector-backed, user-partitioned memory store.
/// A single source of truth for all durable <see cref="Record"/> items.
/// </summary>
public interface IMemoryStore
{
    /// <summary>
    /// Inserts or updates a record using the upsert key <c>(UserId, SessionId, Domain, Key)</c>.
    /// If <see cref="Record.Embedding"/> is empty, an embedding is generated automatically.
    /// </summary>
    /// <param name="record">The record to persist.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The persisted record with assigned <see cref="Record.Id"/> and timestamps.</returns>
    Task<Record> UpsertAsync(Record record, CancellationToken ct = default);

    /// <summary>
    /// Searches for records similar to <paramref name="query"/> within the store.
    /// </summary>
    /// <param name="query">The search parameters including query embedding, filters, and topK.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matching records ordered by cosine similarity descending.</returns>
    Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken ct = default);

    /// <summary>
    /// Retrieves a record by its upsert key, or <see langword="null"/> if not found.
    /// </summary>
    /// <param name="userId">The owning user.</param>
    /// <param name="sessionId">The session, or <see langword="null"/> for global records.</param>
    /// <param name="domain">The domain.</param>
    /// <param name="key">The key within the domain.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matching record, or <see langword="null"/>.</returns>
    Task<Record?> GetByKeyAsync(string userId, string? sessionId, string domain, string key, CancellationToken ct = default);

    /// <summary>
    /// Hard-deletes the record identified by the given key tuple.
    /// </summary>
    /// <param name="userId">The owning user.</param>
    /// <param name="domain">The domain.</param>
    /// <param name="key">The key within the domain.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see langword="true"/> if a record was deleted; <see langword="false"/> if not found.</returns>
    Task<bool> ForgetAsync(string userId, string domain, string key, CancellationToken ct = default);

    /// <summary>
    /// Hard-deletes all records for the given user (GDPR-style Forget-Me).
    /// </summary>
    /// <param name="userId">The user whose data is removed.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of records deleted.</returns>
    Task<int> ForgetMeAsync(string userId, CancellationToken ct = default);

    /// <summary>
    /// Returns the most recent mutation timestamp for the given user,
    /// or <see langword="null"/> if the user has no records.
    /// Used by the retrieval gate (§8.1).
    /// </summary>
    /// <param name="userId">The user to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The most recent mutation timestamp, or <see langword="null"/>.</returns>
    Task<DateTimeOffset?> LastWrittenAtAsync(string userId, CancellationToken ct = default);

    /// <summary>
    /// Returns all records for the given user (used by the Consolidator).
    /// </summary>
    /// <param name="userId">The user whose records are returned.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>All records for the user, in no guaranteed order.</returns>
    Task<IReadOnlyList<Record>> GetAllForUserAsync(string userId, CancellationToken ct = default);

    /// <summary>
    /// Deletes records of the given content type that exceed the TTL
    /// and have not been accessed recently.
    /// </summary>
    /// <param name="contentType">The content type to sweep.</param>
    /// <param name="ttl">The age threshold.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of records deleted.</returns>
    Task<int> DeleteWhereTtlExceededAsync(ContentType contentType, TimeSpan ttl, CancellationToken ct = default);

    /// <summary>
    /// Deletes records whose importance is below <paramref name="importanceThreshold"/>
    /// and have not been accessed within <paramref name="staleAge"/>.
    /// </summary>
    /// <param name="importanceThreshold">Maximum importance for pruning eligibility.</param>
    /// <param name="staleAge">Minimum time since last access.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of records deleted.</returns>
    Task<int> DeleteWhereLowImportanceStaleAsync(double importanceThreshold, TimeSpan staleAge, CancellationToken ct = default);

    /// <summary>
    /// Atomically deletes the records identified by <paramref name="idsToDelete"/> and
    /// inserts <paramref name="newRecord"/> in a single transaction (Spec §6.3 / §8.4).
    /// Used by the Consolidator's <c>Memory_Merge</c> tool.
    /// </summary>
    /// <param name="idsToDelete">The ids of records to hard-delete.</param>
    /// <param name="newRecord">The replacement record to insert.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The inserted record with assigned timestamps.</returns>
    Task<Record> MergeAsync(IReadOnlyList<string> idsToDelete, Record newRecord, CancellationToken ct = default);

    /// <summary>
    /// Updates the <c>Value</c> and/or <c>Importance</c> of the record identified by
    /// <paramref name="recordId"/> for the given <paramref name="userId"/>.
    /// Only non-<see langword="null"/> parameters are applied.
    /// Refreshes <c>UpdatedAt</c> and bumps <c>LastWrittenAt</c>.
    /// </summary>
    /// <param name="recordId">The UUID string of the record to update.</param>
    /// <param name="userId">The owning user (ownership check).</param>
    /// <param name="newValue">The new markdown value, or <see langword="null"/> to leave unchanged.</param>
    /// <param name="newImportance">The new importance score, or <see langword="null"/> to leave unchanged.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The updated record, or <see langword="null"/> if not found.</returns>
    Task<Record?> UpdateRecordAsync(string recordId, string userId, string? newValue, double? newImportance, CancellationToken ct = default);

    /// <summary>
    /// Hard-deletes a single record by its surrogate id for the given user.
    /// Bumps <c>LastWrittenAt</c>.
    /// </summary>
    /// <param name="recordId">The UUID string of the record to delete.</param>
    /// <param name="userId">The owning user (ownership check).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see langword="true"/> if a record was deleted; <see langword="false"/> if not found.</returns>
    Task<bool> DeleteByIdAsync(string recordId, string userId, CancellationToken ct = default);
}
