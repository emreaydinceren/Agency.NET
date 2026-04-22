namespace Agency.KeyValueStore.Common;

/// <summary>
/// Represents a key-value entry with associated tags and a distance metric.
/// </summary>
/// <typeparam name="TValue">The type of the tag values associated with the entry.</typeparam>
/// <param name="Key">The key that uniquely identifies the entry.</param>
/// <param name="Value">The value associated with the specified key.</param>
/// <param name="Metadata">Key value pairs.</param>
/// <param name="Distance">
/// A numeric value representing the distance metric for the entry. For stores that do not compute similarity this is
/// always <c>0.0</c>, so <see cref="SimilarityPercentage"/> is always <c>100</c>.
/// </param>
public record class SearchHit<TValue>(string Key, TValue Value, Dictionary<string, object>? Metadata, double Distance, DateTimeOffset UpdatedOn)
{
    /// <summary>
    /// Converts Cosine distance (0 to 2) into a 0-100 score.
    /// </summary>
    public double SimilarityPercentage => Math.Max(0, (1.0 - Distance) * 100);

    private readonly TimeSpan _recency = DateTimeOffset.UtcNow - UpdatedOn;

    /// <summary>
    /// Gets the number of minutes that have elapsed since the last update.
    /// </summary>
    public double RecencyMinutes => _recency.TotalMinutes;

    /// <summary>
    /// Gets the recency value, in hours.
    /// </summary>
    public double RecencyHours => _recency.TotalHours;
}