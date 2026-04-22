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
    /// <param name="key">The key that identifies the value to insert or update. Cannot be null.</param>
    /// <param name="value">The value to insert or update for the specified key.</param>
    /// <param name="metadata">
    /// An optional collection of metadata to associate with the value. May be null if no metadata is required.
    /// </param>
    Task UpsertAsync<TValue>(string key, TValue value, IDictionary<string, object>? metadata = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes an asynchronous search operation using the specified query and returns the matching results.
    /// </summary>
    /// <typeparam name="TValue">The type of the values contained in each search hit result.</typeparam>
    /// <param name="query">The query criteria used to filter and retrieve search results. Cannot be null.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains a read-only list of search hits
    /// matching the query. The list is empty if no results are found.
    /// </returns>
    Task<IReadOnlyList<SearchHit<TValue>>> SearchAsync<TValue>(Query query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the entry with the given key asynchronously.
    /// </summary>
    /// <param name="key">The key that identifies the entry to delete. Cannot be null.</param>
    /// <param name="cancellationToken">A token to observe for cancellation requests.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result is <see langword="true"/> if an entry
    /// was removed, or <see langword="false"/> if no entry existed for that key.
    /// </returns>
    Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default);
}
