namespace Agency.KeyValueStore.Common;

/// <summary>
/// Represents a key-value entry with associated tags and a distance metric.
/// </summary>
/// <typeparam name="TValue">The type of the tag values associated with the entry.</typeparam>
/// <param name="SessionId">The session this entry belongs to, or <see langword="null"/> if it is user-global.</param>
/// <param name="Key">The key that uniquely identifies the entry.</param>
/// <param name="Value">The value associated with the specified key.</param>
/// <param name="Metadata">Key value pairs.</param>
public record class SearchHit<TValue>(string? SessionId, string Key, TValue Value, Dictionary<string, object>? Metadata, DateTimeOffset UpdatedOn) : 
    SearchHit(SessionId, Key, Metadata, UpdatedOn)
{
}

/// <summary>
/// Represents a key-value entry with associated tags and a distance metric.
/// </summary>
/// <param name="SessionId">The session this entry belongs to, or <see langword="null"/> if it is user-global.</param>
/// <param name="Key">The key that uniquely identifies the entry.</param>
/// <param name="Metadata">Key value pairs.</param>
public record class SearchHit(string? SessionId, string Key, Dictionary<string, object>? Metadata, DateTimeOffset UpdatedOn)
{
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