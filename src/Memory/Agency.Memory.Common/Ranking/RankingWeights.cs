namespace Agency.Memory.Common.Ranking;

/// <summary>
/// Linear combination weights for the composite ranking formula defined in Spec §8.3.
/// </summary>
/// <param name="Similarity">Weight applied to cosine similarity (wₛ).</param>
/// <param name="Recency">Weight applied to exponential recency decay (wᵣ).</param>
/// <param name="Importance">Weight applied to the stored importance score (wᵢ).</param>
/// <param name="SessionMatch">Additive bonus when the record belongs to the current session (wₘ).</param>
public sealed record RankingWeights(
    double Similarity,
    double Recency,
    double Importance,
    double SessionMatch)
{
    /// <summary>
    /// Gets the default weights from Spec §8.3: wₛ=0.5, wᵣ=0.3, wᵢ=0.2, wₘ=0.1.
    /// </summary>
    public static RankingWeights Default { get; } = new(0.5, 0.3, 0.2, 0.1);
}
