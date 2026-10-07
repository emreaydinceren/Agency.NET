using System.Text.Json.Serialization;

namespace Agency.Indexer;

/// <summary>The JSON printed by <c>search</c>.</summary>
/// <param name="Status">Always <c>ok</c>.</param>
/// <param name="Index">The searched index.</param>
/// <param name="BestScore">The top hit's score, 0 when there are no hits.</param>
/// <param name="TopGap">The top hit's lead over the second, when there are at least two hits.</param>
/// <param name="Hint">Advice for the agent, when the result list warrants any.</param>
/// <param name="Hits">The hits, best first.</param>
internal sealed record SearchResponse(
    string Status,
    string Index,
    double BestScore,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? TopGap,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Hint,
    IReadOnlyList<SearchResultHit> Hits);

/// <summary>Summarizes how decisive a result list is, so an agent can stop searching early.</summary>
internal static class SearchGuidance
{
    /// <summary>
    /// How far the best score must lead the runner-up to call it decisive. A heuristic, not tuned per
    /// embedding model.
    /// </summary>
    public const double DecisiveGap = 0.08;

    /// <summary>Returns the best score and its lead over the second hit, or <see langword="null"/> without at least two hits.</summary>
    public static (double Best, double? Gap) Summarize(IReadOnlyList<SearchResultHit> hits) =>
        hits.Count == 0 ? (0, null) : (hits[0].Score, hits.Count > 1 ? Math.Round(hits[0].Score - hits[1].Score, 4) : null);

    /// <summary>Returns a one-line hint when the top hit is decisively ahead; otherwise <see langword="null"/>.</summary>
    public static string? Hint(double? gap) =>
        gap is >= DecisiveGap
            ? $"Top hit leads the next by {gap:0.00}: read it and stop searching."
            : null;
}
