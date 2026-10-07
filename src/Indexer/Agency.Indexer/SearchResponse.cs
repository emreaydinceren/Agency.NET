using System.Text.Json.Serialization;

namespace Agency.Indexer;

/// <summary>How a search's hits are filtered and shaped before they reach the calling agent's context.</summary>
/// <param name="MinScore">Drop hits scoring below this, or <see langword="null"/> to keep every score. Scores are model-dependent, so there is no default.</param>
/// <param name="Within">Keep only hits within this distance of the top score, or <see langword="null"/> to disable.</param>
/// <param name="NoText">Leave the chunk text out of every hit.</param>
/// <param name="SnippetChars">Cut each hit's text to at most this many characters, or <see langword="null"/> for the whole chunk.</param>
/// <param name="PerFile">Keep at most this many hits from any one file, or <see langword="null"/> for no cap.</param>
/// <param name="Top">Return at most this many hits (after filtering), or <see langword="null"/> for all that survive.</param>
/// <param name="ShowIndex">Name the index each hit came from (a search over several indexes).</param>
internal sealed record SearchOptions(
    double? MinScore,
    double? Within,
    bool NoText,
    int? SnippetChars,
    int? PerFile = null,
    int? Top = null,
    bool ShowIndex = false);

/// <summary>A hit as printed: <see cref="Text"/> is omitted from the JSON when it is <see langword="null"/> (<c>--no-text</c>), and so are the location fields an older index did not record.</summary>
/// <param name="Path">The full path of the source file.</param>
/// <param name="Chunk">The chunk index within the file.</param>
/// <param name="Score">Cosine similarity in [0, 1]; higher is closer.</param>
/// <param name="Text">The chunk text, possibly cut to a snippet.</param>
/// <param name="Heading">The Markdown heading path above the chunk.</param>
/// <param name="StartLine">The 1-based first line of the chunk in the file.</param>
/// <param name="EndLine">The 1-based last line of the chunk in the file.</param>
/// <param name="Index">The index the hit came from, when several were searched.</param>
internal sealed record SearchHitView(
    string Path,
    long? Chunk,
    double Score,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Text,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Heading = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? StartLine = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? EndLine = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Index = null);

/// <summary>
/// The printed result of <c>search</c>. When filtering removed hits, <see cref="Filtered"/> and <see cref="BestScore"/> say
/// how many and how good the best of them was, so an agent can tell "nothing relevant" from "broken" and fall back to grep.
/// </summary>
/// <param name="Status">Always <c>ok</c>; an empty <paramref name="Hits"/> is a result, not an error.</param>
/// <param name="Index">The searched index (several, comma-separated, for a multi-index search).</param>
/// <param name="Hits">The hits that survived filtering, best first.</param>
/// <param name="Filtered">How many of the retrieved hits were dropped; absent when none were.</param>
/// <param name="BestScore">The top score before filtering; present only together with <paramref name="Filtered"/>.</param>
/// <param name="MinScore">The score threshold that was applied, when there was one.</param>
/// <param name="TopGap">The top hit's lead over the second hit, when at least two hits are returned.</param>
/// <param name="Hint">Advice for the agent, present only when the top hit clearly leads (<see cref="DecisiveGap"/>).</param>
internal sealed record SearchResponse(
    string Status,
    string Index,
    IReadOnlyList<SearchHitView> Hits,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Filtered,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? BestScore,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? MinScore = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? TopGap = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Hint = null)
{
    /// <summary>
    /// How far the top score must lead the runner-up for <see cref="Hint"/> to tell the agent to stop searching.
    /// A heuristic, not tuned per embedding model.
    /// </summary>
    public const double DecisiveGap = 0.08;

    /// <summary>Applies <paramref name="options"/> to <paramref name="hits"/> (best first, as the index returns them).</summary>
    public static SearchResponse From(string index, IReadOnlyList<SearchResultHit> hits, SearchOptions options)
    {
        double best = hits.Count > 0 ? hits.Max(h => h.Score) : 0;

        // A chunk holding an identifier from the query (hybrid search) survives the thresholds: its score says little about it.
        List<SearchResultHit> kept = hits
            .Where(h => h.ExactMatch || ((options.MinScore is not { } min || h.Score >= min) && (options.Within is not { } within || h.Score >= best - within)))
            .ToList();
        int dropped = hits.Count - kept.Count;

        IEnumerable<SearchResultHit> shaped = kept;
        if (options.PerFile is { } perFile)
        {
            var perFileCount = new Dictionary<string, int>(StringComparer.Ordinal);
            var capped = new List<SearchResultHit>();
            foreach (SearchResultHit hit in kept)
            {
                int count = perFileCount.GetValueOrDefault(hit.Path) + 1;
                perFileCount[hit.Path] = count;
                if (count <= perFile)
                {
                    capped.Add(hit);
                }
            }

            shaped = capped;
        }

        if (options.Top is { } top)
        {
            shaped = shaped.Take(top);
        }

        List<SearchResultHit> returned = shaped.ToList();
        double? gap = returned.Count > 1 ? Math.Round(returned[0].Score - returned[1].Score, 4) : null;

        return new SearchResponse(
            "ok",
            index,
            returned.Select(h => new SearchHitView(h.Path, h.Chunk, h.Score, Shape(h.Text, options), h.Heading, h.StartLine, h.EndLine, options.ShowIndex ? h.Index : null)).ToList(),
            dropped > 0 ? dropped : null,
            dropped > 0 ? best : null,
            options.MinScore,
            gap,
            gap is >= DecisiveGap ? $"Top hit leads the next by {gap:0.00}: read it and stop searching." : null);
    }

    private static string? Shape(string text, SearchOptions options)
    {
        if (options.NoText)
        {
            return null;
        }

        if (options.SnippetChars is not { } limit || text.Length <= limit)
        {
            return text;
        }

        // Never cut between the two halves of a surrogate pair.
        int length = limit > 0 && char.IsHighSurrogate(text[limit - 1]) ? limit - 1 : limit;
        return text[..length];
    }
}
