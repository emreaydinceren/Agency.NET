namespace Agency.VectorStore.Common;

/// <summary>
/// Represents a key-value entry with associated tags and a distance metric.
/// </summary>
/// <typeparam name="TValue">The type of the tag values associated with the entry.</typeparam>
/// <param name="Key">The key that uniquely identifies the entry.</param>
/// <param name="Value">The value associated with the specified key.</param>
/// <param name="Metadata">Key value pairs.</param>
/// <param name="Distance">
/// A numeric value representing the distance metric for the entry. The interpretation of this value depends on the
/// context in which the entry is used.
/// </param>
public record class SearchHit<TValue>(string Key, TValue Value, Dictionary<string, object>? Metadata, double Distance, DateTimeOffset UpdatedOn)
{
    // Converts Cosine distance (0 to 2) into a 0-100 score.
    public double SimilarityPercentage => Math.Max(0, (1.0 - Distance) * 100);
}


/// <summary>
/// Represents a query with optional key, value, metadata filter, result limit, and an option to include metadata in the
/// results.
/// </summary>
/// <param name="Key">The key to filter the query results. Can be null to match any key.</param>
/// <param name="Value">The value to filter the query results. Can be null to match any value.</param>
/// <param name="metadataFilter">An optional dictionary specifying metadata key-value pairs to filter the results. If null, no metadata filtering is
/// applied.</param>
/// <param name="Limit">The maximum number of results to return. If null, no limit is applied. Must be greater than zero if specified.</param>
/// <param name="IncludeMetadataInResults">A value indicating whether to include metadata in the query results. If <see langword="true"/>, metadata is
/// included; otherwise, it is excluded.</param>
public record class Query(string? Key, string? Value, IDictionary<string, object>? metadataFilter = null, int? Limit = 10, bool? IncludeMetadataInResults = false);

/// <summary>
/// Defines the contract for a key-value store that supports asynchronous upsert and search operations.
/// </summary>
/// <remarks>Implementations of this interface provide mechanisms to store and retrieve values associated with
/// string keys, along with optional metadata. The interface is generic to support storing values of various types. All
/// operations are asynchronous, enabling non-blocking usage in scalable applications.</remarks>
public interface IKVStore
{
    /// <summary>
    /// Inserts a new value or updates the existing value associated with the specified key asynchronously.
    /// </summary>
    /// <typeparam name="TValue">The type of the value to store or update.</typeparam>
    /// <param name="key">The key that identifies the value to insert or update. Cannot be null.</param>
    /// <param name="value">The value to insert or update for the specified key.</param>
    /// <param name="metadata">An optional collection of metadata to associate with the value. May be null if no metadata is required.</param>
    Task UpsertAsync<TValue>(string key, TValue value, IDictionary<string, object>? metadata = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes an asynchronous search operation using the specified query and returns the matching results.
    /// </summary>
    /// <typeparam name="TValue">The type of the values contained in each search hit result.</typeparam>
    /// <param name="query">The query criteria used to filter and retrieve search results. Cannot be null.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains a read-only list of search hits
    /// matching the query. The list is empty if no results are found.</returns>
    Task<IReadOnlyList<SearchHit<TValue>>> SearchAsync<TValue>(Query query, CancellationToken cancellationToken = default);
}
