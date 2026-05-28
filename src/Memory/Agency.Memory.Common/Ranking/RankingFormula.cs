using Agency.Memory.Common.Records;

namespace Agency.Memory.Common.Ranking;

/// <summary>
/// Computes the composite ranking score for a memory record against a query, per Spec §8.3.
/// </summary>
/// <remarks>
/// The formula is:
/// <code>
/// score = wₛ · clip(similarity, 0, 1)
///       + wᵣ · exp(-ageDays / halfLifeDays)
///       + wᵢ · record.Importance
///       + wₘ · sessionMatch
/// </code>
/// Weights are intentionally not normalised to sum to 1 — the session-match term is
/// an additive bonus on top of the core score (as noted in Spec §8.3).
/// </remarks>
internal static class RankingFormula
{
    /// <summary>The default recency half-life in days (7 days per Spec §8.3).</summary>
    internal const double DefaultHalfLifeDays = 7.0;

    /// <summary>
    /// Computes the composite ranking score.
    /// </summary>
    /// <param name="similarity">Raw cosine similarity in [-1, 1]. Clamped to [0, 1] before use.</param>
    /// <param name="record">The candidate record to score.</param>
    /// <param name="currentSessionId">The session ID of the current agent turn, for session-match bonus.</param>
    /// <param name="now">The current UTC time, used to compute record age.</param>
    /// <param name="weights">The linear combination weights.</param>
    /// <param name="halfLifeDays">The recency half-life in days.</param>
    /// <returns>The composite score (ordinal, not a probability).</returns>
    internal static double Score(
        double similarity,
        Record record,
        string? currentSessionId,
        DateTimeOffset now,
        RankingWeights weights,
        double halfLifeDays)
    {
        double clippedSimilarity = Math.Max(0.0, Math.Min(1.0, similarity));
        double ageDays = (now - record.UpdatedAt).TotalDays;
        double recency = Math.Exp(-ageDays / halfLifeDays);
        double sessionMatch = record.SessionId is not null && record.SessionId == currentSessionId ? 1.0 : 0.0;

        return (weights.Similarity * clippedSimilarity)
             + (weights.Recency * recency)
             + (weights.Importance * record.Importance)
             + (weights.SessionMatch * sessionMatch);
    }
}
