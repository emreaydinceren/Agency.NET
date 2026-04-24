using Agency.Common;

namespace Agency.KeyValueStore.Common;

/// <summary>
/// Extension methods for converting search results to Dataset format.
/// </summary>
public static class SearchHitExtensions
{
    /// <summary>
    /// Converts a collection of search hits to a Dataset with columns for Key, Value, Distance, SimilarityPercentage, and UpdatedOn.
    /// </summary>
    /// <typeparam name="TValue">The type of the value contained in each search hit.</typeparam>
    /// <param name="hits">The collection of search hits to convert.</param>
    /// <returns>A Dataset representation of the search hits.</returns>
    public static Dataset ToDataset<TValue>(this IReadOnlyList<SearchHit<TValue>> hits)
    {
        var columns = new IColumnMetadata[]
        {
            new ColumnMetadata("Key", 0),
            new ColumnMetadata("Value", 1),
            new ColumnMetadata("Distance", 2),
            new ColumnMetadata("SimilarityPercentage", 3),
            new ColumnMetadata("UpdatedOn", 4),
        };

        var rows = hits.Select((hit, _) => new object?[]
        {
            hit.Key,
            hit.Value,
            hit.Distance,
            hit.SimilarityPercentage,
            hit.UpdatedOn,
        }).ToList();

        return new Dataset(columns, rows);
    }

    private sealed record ColumnMetadata(string? ColumnName, int? ColumnOrdinal) : IColumnMetadata;
}