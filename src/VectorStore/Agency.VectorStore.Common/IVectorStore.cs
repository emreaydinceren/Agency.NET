namespace Agency.VectorStore.Common;

/// <summary>
/// Defines the contract for a key-value store that supports asynchronous upsert and search operations.
/// </summary>
/// <remarks>
/// Implementations of this interface provide mechanisms to store and retrieve values associated with string keys, along
/// with optional metadata. The interface is generic to support storing values of various types. All operations are
/// asynchronous, enabling non-blocking usage in scalable applications.
/// </remarks>
public interface IVectorStore
{
    /// <summary>
    /// Inserts a new value or updates the existing value associated with the specified key asynchronously.
    /// </summary>
    /// <typeparam name="TValue">The type of the value to store or update.</typeparam>
    /// <param name="userId">The user this entry belongs to. Cannot be null.</param>
    /// <param name="sessionId">The session this entry belongs to, or <see langword="null"/> for user-global entries (stored as <c>"*"</c>).</param>
    /// <param name="key">The key that identifies the value to insert or update. Cannot be null.</param>
    /// <param name="value">The value to insert or update for the specified key.</param>
    /// <param name="metadata">
    /// An optional collection of metadata to associate with the value. May be null if no metadata is required.
    /// </param>
    /// <param name="projectId">The project scope for this entry, or <see langword="null"/> for the global project (stored as <c>"*"</c>).</param>
    /// <param name="cancellationToken">A token to observe for cancellation requests.</param>
    Task UpsertAsync<TValue>(
        string userId,
        string? sessionId,
        string key,
        TValue value,
        IDictionary<string, object>? metadata = null,
        string? projectId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes an asynchronous search operation using the specified query and returns the matching results.
    /// </summary>
    /// <typeparam name="TValue">The type of the values contained in each search hit result.</typeparam>
    /// <param name="query">The query criteria used to filter and retrieve search results. Cannot be null.</param>
    /// <param name="cancellationToken">A token to observe for cancellation requests.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains a read-only list of search hits
    /// matching the query. The list is empty if no results are found.
    /// </returns>
    Task<IReadOnlyList<SearchHit<TValue>>> SearchAsync<TValue>(Query query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the entry with the given key asynchronously.
    /// </summary>
    /// <param name="userId">The user this entry belongs to. Cannot be null.</param>
    /// <param name="sessionId">The session this entry belongs to, or <see langword="null"/> for user-global entries (stored as <c>"*"</c>).</param>
    /// <param name="key">The key that identifies the entry to delete. Cannot be null.</param>
    /// <param name="projectId">The project scope for this entry, or <see langword="null"/> for the global project (stored as <c>"*"</c>).</param>
    /// <param name="cancellationToken">A token to observe for cancellation requests.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result is <see langword="true"/> if an entry
    /// was removed, or <see langword="false"/> if no entry existed for that key.
    /// </returns>
    Task<bool> DeleteAsync(
        string userId,
        string? sessionId,
        string key,
        string? projectId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the distinct project ids that have entries for the given user, excluding the global project
    /// sentinel <c>"*"</c>.
    /// </summary>
    /// <param name="userId">The user whose projects to list. Cannot be null.</param>
    /// <param name="cancellationToken">A token to observe for cancellation requests.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains the distinct project ids
    /// for the user, in ascending order.
    /// </returns>
    Task<IReadOnlyList<string>> ListProjectsAsync(
        string userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the documents that carry a <c>source_file</c> metadata entry within the given scope.
    /// </summary>
    /// <param name="userId">The user whose documents to list. Cannot be null.</param>
    /// <param name="sessionId">
    /// The session to restrict results to, or <see langword="null"/> to use the global session scope.
    /// </param>
    /// <param name="projectIds">
    /// The projects to include in addition to the global scope, or <see langword="null"/> to restrict results
    /// to the global scope.
    /// </param>
    /// <param name="cancellationToken">A token to observe for cancellation requests.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains the matching documents,
    /// ordered by source file.
    /// </returns>
    Task<IReadOnlyList<DocumentInfo>> ListDocumentsAsync(
        string userId,
        string? sessionId,
        IReadOnlyList<string>? projectIds,
        CancellationToken cancellationToken = default);
}
