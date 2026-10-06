using System.Text.RegularExpressions;

namespace Agency.Indexer;

/// <summary>
/// Hybrid ranking: reorders a pool of vector-search candidates by fusing the vector rank with a BM25 keyword rank over the
/// same pool (reciprocal rank fusion), so a chunk that contains the query's exact words rises. It cannot add candidates the
/// vector search did not return, so the pool should be much larger than the number of hits wanted. A chunk that contains an
/// identifier-like query token (<c>snake_case</c>, <c>Dotted.Name</c>, <c>CamelCase</c>, <c>ADR-0006</c>, anything with a digit)
/// is marked <see cref="SearchResultHit.ExactMatch"/> so a score threshold does not drop it.
/// </summary>
internal static partial class HybridRanker
{
    private const double K1 = 1.2;
    private const double B = 0.75;
    private const int RrfConstant = 60;

    [GeneratedRegex(@"[\p{L}\p{N}_]+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Words();

    /// <summary>Returns <paramref name="pool"/> reordered by the fused rank.</summary>
    /// <param name="query">The search text.</param>
    /// <param name="pool">Vector-search candidates, best first.</param>
    public static IReadOnlyList<SearchResultHit> Rank(string query, IReadOnlyList<SearchResultHit> pool)
    {
        if (pool.Count == 0)
        {
            return pool;
        }

        string[] queryTerms = Tokens(query).Distinct(StringComparer.Ordinal).ToArray();
        string[] identifiers = IdentifierTokens(query);
        List<string[]> docs = pool.Select(h => Tokens(h.Text).ToArray()).ToList();
        double averageLength = Math.Max(1, docs.Average(d => d.Length));
        double[] bm25 = docs.Select(d => Bm25(queryTerms, d, docs, averageLength)).ToArray();

        // Keyword rank: only chunks that matched a term are ranked; the others get no keyword vote.
        int[] byKeyword = Enumerable.Range(0, pool.Count).Where(i => bm25[i] > 0).OrderByDescending(i => bm25[i]).ToArray();
        double[] fused = new double[pool.Count];
        for (int i = 0; i < pool.Count; i++)
        {
            fused[i] = 1.0 / (RrfConstant + i + 1);
        }

        for (int rank = 0; rank < byKeyword.Length; rank++)
        {
            fused[byKeyword[rank]] += 1.0 / (RrfConstant + rank + 1);
        }

        return Enumerable.Range(0, pool.Count)
            .OrderByDescending(i => fused[i])
            .ThenBy(i => i)
            .Select(i => pool[i] with { ExactMatch = identifiers.Any(id => pool[i].Text.Contains(id, StringComparison.OrdinalIgnoreCase)) })
            .ToList();
    }

    /// <summary>The whitespace-separated tokens of <paramref name="query"/> that look like identifiers rather than prose.</summary>
    internal static string[] IdentifierTokens(string query) =>
        query.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim('"', '\'', '`', '(', ')', '[', ']', ',', ';', '?', '!', '.', ':'))
            .Where(t => t.Length >= 3 && IsIdentifierLike(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static bool IsIdentifierLike(string token)
    {
        if (token.Any(char.IsDigit) || token.Contains('_', StringComparison.Ordinal) || token.Contains('.', StringComparison.Ordinal) || token.Contains(':', StringComparison.Ordinal))
        {
            return true;
        }

        // camelCase / PascalCase (a lower-case letter followed later by an upper-case one) and ALL-CAPS acronyms.
        bool lowerSeen = false;
        foreach (char c in token)
        {
            if (char.IsLower(c))
            {
                lowerSeen = true;
            }
            else if (char.IsUpper(c) && lowerSeen)
            {
                return true;
            }
        }

        return token.Length >= 3 && token.All(c => char.IsUpper(c) || char.IsDigit(c));
    }

    private static IEnumerable<string> Tokens(string text) =>
        Words().Matches(text).Select(m => m.Value.ToLowerInvariant());

    private static double Bm25(string[] queryTerms, string[] doc, List<string[]> docs, double averageLength)
    {
        double score = 0;
        foreach (string term in queryTerms)
        {
            int frequency = doc.Count(t => string.Equals(t, term, StringComparison.Ordinal));
            if (frequency == 0)
            {
                continue;
            }

            int containing = docs.Count(d => d.Contains(term, StringComparer.Ordinal));
            double idf = Math.Log(1 + ((docs.Count - containing + 0.5) / (containing + 0.5)));
            score += idf * (frequency * (K1 + 1)) / (frequency + (K1 * (1 - B + (B * doc.Length / averageLength))));
        }

        return score;
    }
}
