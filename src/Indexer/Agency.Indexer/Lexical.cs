using System.Text.RegularExpressions;

namespace Agency.Indexer;

/// <summary>A passage found by keyword match, straight from the file on disk.</summary>
/// <param name="Path">The full path of the file.</param>
/// <param name="StartLine">The 1-based first line.</param>
/// <param name="EndLine">The 1-based last line.</param>
/// <param name="Text">The passage text.</param>
/// <param name="Heading">The heading path above it.</param>
/// <param name="Score">The BM25 score.</param>
internal sealed record LexicalHit(string Path, int StartLine, int EndLine, string Text, string? Heading, double Score);

/// <summary>
/// Keyword side of hybrid search: words of the query, words that occur in most files (a project name), and a BM25 ranking over
/// passage-sized windows of the indexed files, read from disk so an exact term is found however the embedding drifted.
/// </summary>
internal static partial class Lexical
{
    private const double K1 = 1.2;
    private const double B = 0.75;

    /// <summary>A word is common when it occurs in at least this share of the files.</summary>
    public const double CommonShare = 0.75;

    /// <summary>Fewer files than this have no common terms: the statistics mean nothing.</summary>
    public const int MinFilesForCommonTerms = 10;

    private const int MaxCommonTerms = 50;

    /// <summary>A query keeps at least this many words after common ones are dropped.</summary>
    private const int MinWordsAfterDrop = 3;

    private static readonly HashSet<string> FunctionWords = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "are", "was", "were", "has", "have", "had", "how", "what", "why", "when", "where", "which", "who", "does", "did",
        "can", "could", "should", "would", "will", "with", "without", "from", "into", "onto", "that", "this", "these", "those", "than", "then",
        "its", "their", "there", "about", "after", "before", "over", "under", "not", "but", "any", "all", "our", "your", "you", "she", "his", "her",
    };

    [GeneratedRegex(@"[\p{L}\p{N}_]+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Words();

    /// <summary>The lower-cased words of <paramref name="text"/>.</summary>
    public static IEnumerable<string> Tokens(string text) =>
        Words().Matches(text).Select(m => m.Value.ToLowerInvariant());

    /// <summary>The distinct content words of a query: three or more characters, no function words.</summary>
    public static string[] QueryTerms(string query) =>
        Tokens(query).Where(t => t.Length >= 3 && !FunctionWords.Contains(t)).Distinct(StringComparer.Ordinal).ToArray();

    /// <summary>The words found in at least <see cref="CommonShare"/> of the files (most common first), or none for a small corpus.</summary>
    /// <param name="files">The text of every indexed file.</param>
    public static IReadOnlyList<string> CommonTerms(IEnumerable<string> files)
    {
        var documentFrequency = new Dictionary<string, int>(StringComparer.Ordinal);
        int count = 0;
        foreach (string file in files)
        {
            count++;
            foreach (string term in Tokens(file).Where(t => t.Length >= 3 && !FunctionWords.Contains(t)).ToHashSet(StringComparer.Ordinal))
            {
                documentFrequency[term] = documentFrequency.GetValueOrDefault(term) + 1;
            }
        }

        return count < MinFilesForCommonTerms
            ? []
            : documentFrequency.Where(kv => kv.Value >= count * CommonShare).OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Take(MaxCommonTerms).Select(kv => kv.Key).ToList();
    }

    /// <summary>
    /// Removes the common words from <paramref name="query"/> so a project name in it does not pull every score up. The query is left
    /// alone when fewer than <see cref="MinWordsAfterDrop"/> words would remain.
    /// </summary>
    /// <returns>The query to embed and the words that were left out.</returns>
    public static (string Query, IReadOnlyList<string> Dropped) DropCommon(string query, IReadOnlyList<string>? common)
    {
        if (common is not { Count: > 0 })
        {
            return (query, []);
        }

        string[] words = query.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        bool IsCommon(string word) => Tokens(word).Any(t => common.Contains(t, StringComparer.Ordinal));
        string[] kept = words.Where(w => !IsCommon(w)).ToArray();
        string[] dropped = words.Where(IsCommon).ToArray();
        return dropped.Length == 0 || kept.Length < MinWordsAfterDrop
            ? (query, [])
            : (string.Join(' ', kept), dropped.Select(d => string.Concat(Tokens(d))).Distinct(StringComparer.Ordinal).ToList());
    }

    /// <summary>
    /// Ranks the passage-sized windows of <paramref name="files"/> against <paramref name="query"/> with BM25 and returns the best.
    /// </summary>
    /// <param name="files">Full paths of the indexed files; missing and HTML files are skipped.</param>
    /// <param name="query">The query as typed.</param>
    /// <param name="passage">The window shape.</param>
    /// <param name="take">The most hits to return.</param>
    /// <param name="keep">Whether a file's path is wanted (the <c>--path</c> filter).</param>
    public static IReadOnlyList<LexicalHit> Search(IEnumerable<string> files, string query, PassageOptions passage, int take, Func<string, bool> keep)
    {
        string[] terms = QueryTerms(query);
        if (terms.Length == 0)
        {
            return [];
        }

        var windows = new List<(string Path, Passage Passage, int[] Frequencies, int Length)>();
        var documentFrequency = new int[terms.Length];
        long totalLength = 0;
        int windowCount = 0;
        foreach (string path in files)
        {
            string extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
            if (extension is ".html" or ".htm" || !keep(path) || !File.Exists(path))
            {
                continue;
            }

            string content;
            try
            {
                content = File.ReadAllText(path);
            }
            catch (IOException)
            {
                continue;
            }

            foreach (Passage window in PassageSplitter.Split(content, extension is ".md" or ".markdown" or ".mdx", passage))
            {
                var frequencies = new int[terms.Length];
                int length = 0;
                foreach (string token in Tokens(window.Embedded))
                {
                    length++;
                    int at = Array.IndexOf(terms, token);
                    if (at >= 0)
                    {
                        frequencies[at]++;
                    }
                }

                totalLength += length;
                windowCount++;
                if (frequencies.Any(f => f > 0))
                {
                    for (int t = 0; t < terms.Length; t++)
                    {
                        if (frequencies[t] > 0)
                        {
                            documentFrequency[t]++;
                        }
                    }

                    windows.Add((path, window, frequencies, length));
                }
            }
        }

        if (windows.Count == 0)
        {
            return [];
        }

        double averageLength = Math.Max(1, (double)totalLength / Math.Max(1, windowCount));
        return windows
            .Select(w => (w.Path, w.Passage, Score: Bm25(w.Frequencies, w.Length, documentFrequency, windowCount, averageLength)))
            .Where(w => w.Score > 0)
            .OrderByDescending(w => w.Score)
            .Take(take)
            .Select(w => new LexicalHit(w.Path, w.Passage.StartLine, w.Passage.EndLine, w.Passage.Text, w.Passage.Heading, w.Score))
            .ToList();
    }

    private static double Bm25(int[] frequencies, int length, int[] documentFrequency, int documents, double averageLength)
    {
        double score = 0;
        for (int t = 0; t < frequencies.Length; t++)
        {
            if (frequencies[t] == 0)
            {
                continue;
            }

            double idf = Math.Log(1 + ((documents - documentFrequency[t] + 0.5) / (documentFrequency[t] + 0.5)));
            score += idf * (frequencies[t] * (K1 + 1)) / (frequencies[t] + (K1 * (1 - B + (B * length / averageLength))));
        }

        return score;
    }
}
