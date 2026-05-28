using Agency.Memory.Common.Ranking;
using Agency.Memory.Common.Records;

namespace Agency.Memory.Common.Options;

/// <summary>
/// Configuration for the memory retrieval and hygiene subsystems.
/// </summary>
public sealed class MemoryOptions
{
    /// <summary>Gets or sets the name of the memory collection (default: "agency_memory").</summary>
    public string CollectionName { get; set; } = "agency_memory";

    /// <summary>Gets or sets the ranking weights for the composite retrieval formula.</summary>
    public RankingWeights Ranking { get; set; } = RankingWeights.Default;

    /// <summary>Gets or sets the recency half-life in days for the ranking formula (default: 7).</summary>
    public double RecencyHalfLifeDays { get; set; } = 7.0;

    /// <summary>Gets or sets the maximum number of records returned after ranking (default: 10).</summary>
    public int RetrievalTopK { get; set; } = 10;

    /// <summary>Gets or sets the over-fetch multiplier applied before re-ranking (default: 3).</summary>
    public int OverFetchFactor { get; set; } = 3;

    /// <summary>
    /// Gets or sets per-<see cref="ContentType"/> similarity thresholds used as hints by the Consolidator.
    /// Keys are <see cref="ContentType"/> values.
    /// </summary>
    public Dictionary<ContentType, double> ConsolidationSimilarityThreshold { get; set; } = [];

    /// <summary>
    /// Gets or sets per-<see cref="ContentType"/> TTL values for the hygiene sweeper.
    /// Records older than their TTL (and not recently accessed) are deleted.
    /// A missing entry means no TTL for that type.
    /// </summary>
    public Dictionary<ContentType, TimeSpan> Ttl { get; set; } = [];

    /// <summary>Gets or sets the importance threshold below which stale records are pruned (default: 0.2).</summary>
    public double ImportancePruneThreshold { get; set; } = 0.2;

    /// <summary>Gets or sets the age after which low-importance records are eligible for pruning (default: 30 days).</summary>
    public TimeSpan StalePruneAge { get; set; } = TimeSpan.FromDays(30);

    /// <summary>Gets or sets the hygiene sweep interval (default: 24 hours).</summary>
    public TimeSpan HygieneSchedule { get; set; } = TimeSpan.FromHours(24);
}
