using System.Text;
using System.Text.RegularExpressions;
using Agency.Embeddings.Common;

namespace Agency.Indexer;

/// <summary>What a grep-style search is asked to do.</summary>
/// <param name="TopFiles">The most files to print.</param>
/// <param name="PerFile">The most passages considered per file when picking its lines.</param>
/// <param name="LinesPerFile">The lines printed per file.</param>
/// <param name="MaxLineChars">A printed line is cut to this many characters.</param>
/// <param name="Highlight">Mark query words in the printed lines with <c>**</c>.</param>
/// <param name="MinScore">The configured minimum score, or <see langword="null"/> to use each index's noise ceiling.</param>
/// <param name="Within">Keep only hits within this distance of the best score.</param>
/// <param name="Hybrid">Fuse the vector ranking with a keyword ranking of the files.</param>
/// <param name="RawQuery">Embed the query as typed, without dropping words common to the corpus.</param>
/// <param name="PathGlob">Keep files whose path under the index root matches.</param>
internal sealed record LineSearchOptions(
    int TopFiles = 4,
    int PerFile = 2,
    int LinesPerFile = 2,
    int MaxLineChars = 200,
    bool Highlight = false,
    double? MinScore = null,
    double? Within = null,
    bool Hybrid = true,
    bool RawQuery = false,
    string? PathGlob = null);

/// <summary>A file block of the printed result, remembered so <c>read --hit N</c> can open it.</summary>
/// <param name="Index">The index it came from.</param>
/// <param name="Path">The full path of the file.</param>
/// <param name="StartLine">The first line of the best passage, when known.</param>
/// <param name="EndLine">The last line of the best passage, when known.</param>
internal sealed record PrintedHit(string Index, string Path, int? StartLine, int? EndLine);

/// <summary>The printed result of a grep-style search.</summary>
/// <param name="Text">What goes to stdout.</param>
/// <param name="TopScore">The best raw score printed, or <see langword="null"/> when nothing matched.</param>
/// <param name="Hits">One entry per printed file, in order.</param>
internal sealed record LineSearchResult(string Text, double? TopScore, IReadOnlyList<PrintedHit> Hits);

/// <summary>
/// The grep-style search: retrieves passages (vector, fused with a keyword ranking of the files), keeps the best files, and prints
/// the lines of each that are closest to the question as <c>path:line: text</c>, so the answer is usually in the output itself.
/// </summary>
internal static partial class LineSearch
{
    /// <summary>How far the top file must lead the runner-up, in noise standard deviations, to be called the winner. A heuristic.</summary>
    public const double DecisiveLeadSigma = 1.5;

    /// <summary>The raw lead used when the index has no noise statistics.</summary>
    public const double DecisiveRawLead = 0.03;

    private const int RrfConstant = 60;
    private const int LexicalTake = 20;
    private const int MaxLexicalOnly = 6;
    private const int MinLineChars = 20;
    private const int MaxLinesPerPassage = 40;
    private const int HeadingChars = 80;

    [GeneratedRegex(@"^[\s|:\-=_*`~]*$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex RuleLine();

    private sealed class Candidate(SearchResultHit hit, string index, NoiseStats? noise)
    {
        public SearchResultHit Hit { get; set; } = hit;

        public string Index { get; } = index;

        public NoiseStats? Noise { get; } = noise;

        public int? VectorRank { get; set; }

        public int? LexicalRank { get; set; }

        public double Fused { get; set; }
    }

    private sealed record Line(int? Number, string Text, int Order);

    /// <summary>Runs the search over <paramref name="indexes"/>.</summary>
    public static async Task<LineSearchResult> RunAsync(
        IndexService service,
        IEmbeddingGenerator embeddings,
        IReadOnlyList<string> indexes,
        string query,
        LineSearchOptions options,
        CancellationToken ct)
    {
        string[] identifiers = HybridRanker.IdentifierTokens(query);
        var candidates = new List<Candidate>();
        var roots = new Dictionary<string, string>(StringComparer.Ordinal);
        var floors = new List<double>();
        IReadOnlyList<string> dropped = [];
        string embeddedQuery = query;
        int retrieved = 0;
        double best = 0;

        foreach (string index in indexes)
        {
            IndexConfig config = await service.GetConfigAsync(index, ct);
            roots[index] = config.Root;
            (string indexQuery, IReadOnlyList<string> droppedHere) = options.RawQuery ? (query, []) : Lexical.DropCommon(query, config.CommonTerms);
            if (index == indexes[0])
            {
                embeddedQuery = indexQuery;
                dropped = droppedHere;
            }

            int pool = Math.Max(50, options.TopFiles * options.PerFile * 10);
            IReadOnlyList<SearchResultHit> vector = await service.SearchAsync(index, indexQuery, pool, ct, options.PathGlob, pool);
            bool passages = config.FormatVersion >= IndexFormat.Passages;
            List<Candidate> mine = vector
                .Select((h, i) => new Candidate(passages ? h with { Text = Passage.StripHeading(h.Text, h.Heading) } : h, index, config.Noise) { VectorRank = i })
                .ToList();

            if (options.Hybrid)
            {
                await FuseLexicalAsync(service, embeddings, index, config, query, indexQuery, options, mine, ct);
            }

            foreach (Candidate c in mine)
            {
                c.Fused = (c.VectorRank is { } v ? 1.0 / (RrfConstant + v + 1) : 0) + (c.LexicalRank is { } l ? 1.0 / (RrfConstant + l + 1) : 0);
                if (options.Hybrid && identifiers.Any(id => c.Hit.Text.Contains(id, StringComparison.OrdinalIgnoreCase)))
                {
                    // An identifier from the query is an exact hit: it counts as one more first-place vote.
                    c.Hit = c.Hit with { ExactMatch = true };
                    c.Fused += 1.0 / (RrfConstant + 1);
                }
            }

            retrieved += mine.Count;
            best = Math.Max(best, mine.Count > 0 ? mine.Max(c => c.Hit.Score) : 0);
            double? floor = options.MinScore ?? config.Noise?.Ceiling;
            if (floor is { } f)
            {
                floors.Add(f);
            }

            bool explicitMin = options.MinScore is not null;
            candidates.AddRange(mine.Where(c => c.Hit.ExactMatch || floor is not { } fl || (explicitMin ? c.Hit.Score >= fl : c.Hit.Score > fl)));
        }

        if (options.Within is { } within)
        {
            candidates = candidates.Where(c => c.Hit.ExactMatch || c.Hit.Score >= best - within).ToList();
        }

        double? shownFloor = floors.Count > 0 ? floors.Max() : null;
        var sb = new StringBuilder();
        if (candidates.Count == 0)
        {
            AppendDropped(sb, dropped);
            sb.Append("# no match above min score");
            sb.Append(shownFloor is { } sf ? $" ({sf:0.00})" : "");
            sb.Append(retrieved > 0 ? $"; best was {best:0.00}" : "");
            return new LineSearchResult(sb.ToString(), null, []);
        }

        List<Candidate> ordered = candidates.OrderByDescending(c => c.Fused).ThenByDescending(c => c.Hit.Score).ToList();
        var files = new List<List<Candidate>>();
        foreach (Candidate c in ordered)
        {
            List<Candidate>? file = files.FirstOrDefault(f => f[0].Index == c.Index && string.Equals(f[0].Hit.Path, c.Hit.Path, StringComparison.Ordinal));
            if (file is null)
            {
                if (files.Count < options.TopFiles)
                {
                    files.Add([c]);
                }
            }
            else if (file.Count < options.PerFile)
            {
                file.Add(c);
            }
        }

        IReadOnlyList<List<Line>> chosen = await ChooseLinesAsync(embeddings, embeddedQuery, files, query, options, ct);
        Regex? highlight = options.Highlight ? HighlightPattern(Lexical.QueryTerms(query)) : null;
        var printed = new List<PrintedHit>();
        AppendDropped(sb, dropped);
        for (int f = 0; f < files.Count; f++)
        {
            Candidate top = files[f][0];
            string rel = Path.GetRelativePath(roots[top.Index], top.Hit.Path).Replace(Path.DirectorySeparatorChar, '/');
            string prefix = indexes.Count > 1 ? $"{top.Index}:" : "";
            foreach (Line line in chosen[f])
            {
                string text = Fit(line.Text, options.MaxLineChars);
                sb.Append(prefix).Append(rel).Append(line.Number is { } n ? $":{n}: " : ": ").AppendLine(highlight is null ? text : highlight.Replace(text, "**$0**"));
            }

            double fileScore = files[f].Max(c => c.Hit.Score);
            string z = top.Noise is { } noise ? $", {noise.Normalize(fileScore):+0.0;-0.0} sd" : "";
            string heading = top.Hit.Heading is { Length: > 0 } h ? " " + Fit(h, HeadingChars) : "";
            sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"  [score {fileScore:0.00}{z}]{heading}");
            int? first = chosen[f].FirstOrDefault(l => l.Number is not null)?.Number;
            printed.Add(new PrintedHit(top.Index, top.Hit.Path, first ?? (int?)top.Hit.StartLine, top.Hit.EndLine is { } e ? (int)e : null));
        }

        sb.Append(Confidence(files, shownFloor));
        return new LineSearchResult(sb.ToString().TrimEnd(), files.Max(f => f.Max(c => c.Hit.Score)), printed);
    }

    /// <summary>The cosine similarity of two vectors.</summary>
    internal static double Cosine(ReadOnlyMemory<float> a, ReadOnlyMemory<float> b)
    {
        ReadOnlySpan<float> x = a.Span;
        ReadOnlySpan<float> y = b.Span;
        double dot = 0;
        double nx = 0;
        double ny = 0;
        for (int i = 0; i < Math.Min(x.Length, y.Length); i++)
        {
            dot += x[i] * y[i];
            nx += x[i] * x[i];
            ny += y[i] * y[i];
        }

        return nx == 0 || ny == 0 ? 0 : dot / (Math.Sqrt(nx) * Math.Sqrt(ny));
    }

    private static void AppendDropped(StringBuilder sb, IReadOnlyList<string> dropped)
    {
        if (dropped.Count > 0)
        {
            sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"# ignored words common to every page: {string.Join(", ", dropped)} (--raw-query keeps them)");
        }
    }

    private static string Confidence(List<List<Candidate>> files, double? floor)
    {
        Candidate top = files[0].OrderByDescending(c => c.Hit.Score).First();
        double topScore = files[0].Max(c => c.Hit.Score);
        double next = files.Count > 1 ? files.Skip(1).Max(f => f.Max(c => c.Hit.Score)) : floor ?? 0;
        double lead = topScore - next;
        bool decisive = top.Noise is { } noise
            ? lead / Math.Max(noise.StdDev, NoiseStats.MinStdDev) >= DecisiveLeadSigma
            : lead >= DecisiveRawLead;
        return decisive ? $"# top hit leads by {lead:0.00}; read it and stop" : "# no clear winner; reword or open two";
    }

    /// <summary>Adds keyword-ranked passages of the index's files to <paramref name="mine"/>: ranks to the ones the vector search found, new entries (scored by embedding) to the rest.</summary>
    private static async Task FuseLexicalAsync(
        IndexService service,
        IEmbeddingGenerator embeddings,
        string index,
        IndexConfig config,
        string query,
        string embeddedQuery,
        LineSearchOptions options,
        List<Candidate> mine,
        CancellationToken ct)
    {
        var filter = options.PathGlob is null ? null : new GlobFilter([options.PathGlob]);
        var shape = config.FormatVersion >= IndexFormat.Passages ? new PassageOptions(config.PassageLines, config.PassageOverlap) : PassageOptions.Default;
        IReadOnlyList<ManifestEntry> files = (await service.ListAsync(index, ct)).Files;
        IReadOnlyList<LexicalHit> lexical = Lexical.Search(
            files.Select(f => f.Path),
            query,
            shape,
            LexicalTake,
            path => filter is null || filter.Matches(Path.GetRelativePath(config.Root, path)));

        var only = new List<(int Rank, LexicalHit Hit)>();
        for (int rank = 0; rank < lexical.Count; rank++)
        {
            LexicalHit hit = lexical[rank];
            Candidate? match = mine.Where(c => string.Equals(c.Hit.Path, hit.Path, StringComparison.Ordinal) && c.Hit.StartLine <= hit.EndLine && hit.StartLine <= c.Hit.EndLine)
                .MinBy(c => c.VectorRank ?? int.MaxValue);
            if (match is not null)
            {
                match.LexicalRank = Math.Min(match.LexicalRank ?? int.MaxValue, rank);
            }
            else if (only.Count < MaxLexicalOnly)
            {
                only.Add((rank, hit));
            }
        }

        if (only.Count == 0)
        {
            return;
        }

        try
        {
            IReadOnlyList<ReadOnlyMemory<float>> vectors = await embeddings.GenerateEmbeddingsAsync(
                only.Select(o => new Passage(o.Hit.StartLine, o.Hit.EndLine, o.Hit.Text, o.Hit.Heading).Embedded).Prepend(embeddedQuery), ct);
            double[] scores = only.Select((_, i) => Math.Round(Math.Max(0, Cosine(vectors[0], vectors[i + 1])), 4)).ToArray();
            double[] vectorScores = mine.Where(c => c.VectorRank is not null).Select(c => c.Hit.Score).ToArray();
            for (int i = 0; i < only.Count; i++)
            {
                LexicalHit hit = only[i].Hit;
                var found = new Candidate(new SearchResultHit(hit.Path, null, scores[i], hit.Text, hit.Heading, hit.StartLine, hit.EndLine, index), index, config.Noise)
                {
                    LexicalRank = only[i].Rank,
                    VectorRank = vectorScores.Count(s => s > scores[i]),
                };
                mine.Add(found);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Without a score a keyword-only passage cannot be placed or filtered; the vector results stand.
        }
    }

    private static async Task<IReadOnlyList<List<Line>>> ChooseLinesAsync(
        IEmbeddingGenerator embeddings,
        string embeddedQuery,
        List<List<Candidate>> files,
        string query,
        LineSearchOptions options,
        CancellationToken ct)
    {
        List<List<Line>> perFile = files.Select(file =>
        {
            var lines = new List<Line>();
            var seen = new HashSet<int>();
            int order = 0;
            foreach (Candidate c in file)
            {
                string[] raw = c.Hit.Text.Split('\n');
                var eligible = new List<Line>();
                for (int i = 0; i < raw.Length && i < MaxLinesPerPassage; i++)
                {
                    int? number = c.Hit.StartLine is { } s ? (int)s + i : null;
                    if (number is { } n && !seen.Add(n))
                    {
                        continue;
                    }

                    eligible.Add(new Line(number, raw[i].Trim(), order++));
                }

                lines.AddRange(Pick(eligible));
            }

            return lines;
        }).ToList();

        string[] terms = Lexical.QueryTerms(query);
        double[][] scores;
        try
        {
            IReadOnlyList<ReadOnlyMemory<float>> vectors = await embeddings.GenerateEmbeddingsAsync(perFile.SelectMany(l => l).Select(l => l.Text).Prepend(embeddedQuery), ct);
            int at = 1;
            scores = perFile.Select(file => file.Select(_ => Cosine(vectors[0], vectors[at++])).ToArray()).ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // No embedding for the lines: fall back to how many query words each line holds, then to document order.
            scores = perFile.Select(file => file.Select(l => (double)Lexical.Tokens(l.Text).Count(t => terms.Contains(t, StringComparer.Ordinal))).ToArray()).ToArray();
        }

        return perFile
            .Select((file, f) => file
                .Select((line, i) => (Line: line, Score: scores[f][i]))
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Line.Order)
                .Take(options.LinesPerFile)
                .Select(x => x.Line)
                .OrderBy(l => l.Order)
                .ToList())
            .ToList();
    }

    /// <summary>The lines worth showing: real sentences, not headings, table rules or fences; any non-blank line when there are none.</summary>
    private static List<Line> Pick(List<Line> lines)
    {
        List<Line> good = lines.Where(l => l.Text.Length >= MinLineChars && !l.Text.StartsWith('#') && !l.Text.StartsWith("```", StringComparison.Ordinal) && !RuleLine().IsMatch(l.Text)).ToList();
        return good.Count > 0 ? good : lines.Where(l => l.Text.Length > 0).ToList();
    }

    private static Regex? HighlightPattern(string[] terms) =>
        terms.Length == 0
            ? null
            : new Regex($@"\b(?:{string.Join('|', terms.Select(Regex.Escape))})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static string Fit(string text, int max)
    {
        if (text.Length <= max)
        {
            return text;
        }

        int length = max > 1 && char.IsHighSurrogate(text[max - 2]) ? max - 2 : max - 1;
        return text[..length] + "…";
    }
}
