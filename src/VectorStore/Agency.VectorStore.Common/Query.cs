namespace Agency.VectorStore.Common;

/// <summary>
/// Represents a query with optional key, value, metadata filter, result limit, and an option to include metadata in the
/// results.
/// </summary>
/// <param name="UserId">The user whose entries to search. Cannot be null.</param>
/// <param name="SessionId">
/// The session to restrict results to. If null, results across all sessions of the user are returned.
/// </param>
/// <param name="Key">The key to filter the query results. Can be null to match any key.</param>
/// <param name="Value">The value to filter the query results. Can be null to match any value.</param>
/// <param name="MetadataFilter">
/// An optional dictionary specifying metadata key-value pairs to filter the results. If null, no metadata filtering is
/// applied.
/// </param>
/// <param name="Limit">
/// The maximum number of results to return. If null, no limit is applied. Must be greater than zero if specified.
/// </param>
/// <param name="IncludeMetadataInResults">
/// A value indicating whether to include metadata in the query results. If <see langword="true"/>, metadata is
/// included; otherwise, it is excluded.
/// </param>
public record class Query(string UserId, string? SessionId, string? Key, string? Value, IDictionary<string, object>? MetadataFilter = null, int? Limit = 10, bool? IncludeMetadataInResults = false);
