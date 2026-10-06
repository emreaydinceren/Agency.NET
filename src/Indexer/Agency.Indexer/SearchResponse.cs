using System.Text.Json.Serialization;

namespace Agency.Indexer;

/// <summary>How a search's hits are filtered and shaped before they reach the calling agent's context.</summary>
/// <param name="MinScore">Drop hits scoring below this, or <see langword="null"/> to keep every score. Scores are model-dependent, so there is no default.</param>
/// <param name="Within">Keep only hits within this distance of the top score, or <see langword="null"/> to disable.</param>
/// <param name="NoText">Leave the chunk text out of every hit.</param>
/// <param name="SnippetChars">Cut each hit's text to at most this many characters, or <see langword="null"/> for the whole chunk.</param>
internal sealed record SearchOptions(double? MinScore, double? Within, bool NoText, int? SnippetChars);

/// <summary>A hit as printed: <see cref="Text"/> is omitted from the JSON when it is <see langword="null"/> (<c>--no-text</c>).</summary>
/// <param name="Path">The full path of the source file.</param>
/// <param name="Chunk">The chunk index within the file.</param>
/// <param name="Score">Cosine similarity in [0, 1]; higher is closer.</param>
/// <param name="Text">The chunk text, possibly cut to a snippet.</param>
internal sealed record SearchHitView(string Path, long? Chunk, double Score, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Text);

/// <summary>
/// The printed result of <c>search</c>. When filtering removed hits, <see cref="Filtered"/> and <see cref="BestScore"/> say
/// how many and how good the best of them was, so an agent can tell "nothing relevant" from "broken" and fall back to grep.
/// </summary>
/// <param name="Status">Always <c>ok</c>; an empty <paramref name="Hits"/> is a result, not an error.</param>
/// <param name="Index">The searched index.</param>
/// <param name="Hits">The hits that survived filtering, best first.</param>
/// <param name="Filtered">How many of the retrieved hits were dropped; absent when none were.</param>
/// <param name="BestScore">The top score before filtering; present only together with <paramref name="Filtered"/>.</param>
internal sealed record SearchResponse(
    string Status,
    string Index,
    IReadOnlyList<SearchHitView> Hits,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Filtered,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? BestScore)
{
    /// <summary>Applies <paramref name="options"/> to <paramref name="hits"/> (best first, as the index returns them).</summary>
    public static SearchResponse From(string index, IReadOnlyList<SearchResultHit> hits, SearchOptions options)
    {
        double best = hits.Count > 0 ? hits.Max(h => h.Score) : 0;
        List<SearchResultHit> kept = hits
            .Where(h => (options.MinScore is not { } min || h.Score >= min) && (options.Within is not { } within || h.Score >= best - within))
            .ToList();
        int dropped = hits.Count - kept.Count;

        return new SearchResponse(
            "ok",
            index,
            kept.Select(h => new SearchHitView(h.Path, h.Chunk, h.Score, Shape(h.Text, options))).ToList(),
            dropped > 0 ? dropped : null,
            dropped > 0 ? best : null);
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
