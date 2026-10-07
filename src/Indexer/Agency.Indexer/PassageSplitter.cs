namespace Agency.Indexer;

/// <summary>How a passage-level index cuts a file: at most <paramref name="Lines"/> non-blank lines per unit, sharing <paramref name="Overlap"/> lines with the next.</summary>
/// <param name="Lines">The most non-blank lines in one passage.</param>
/// <param name="Overlap">The lines a passage shares with the one before it (never across a heading).</param>
internal sealed record PassageOptions(int Lines = 6, int Overlap = 1)
{
    /// <summary>The default: six lines, one line of overlap.</summary>
    public static PassageOptions Default { get; } = new();
}

/// <summary>A short run of whole lines of a file.</summary>
/// <param name="StartLine">The 1-based first line.</param>
/// <param name="EndLine">The 1-based last line.</param>
/// <param name="Text">The lines, joined with <c>\n</c>.</param>
/// <param name="Heading">The Markdown heading path above the passage, or <see langword="null"/>.</param>
internal sealed record Passage(int StartLine, int EndLine, string Text, string? Heading)
{
    /// <summary>The text that is embedded and stored: the heading path on its own line, then the passage, so a short passage keeps its topic.</summary>
    public string Embedded => Heading is null ? this.Text : $"{this.Heading}\n{this.Text}";

    /// <summary>The passage text of a stored value written by <see cref="Embedded"/>.</summary>
    public static string StripHeading(string stored, string? heading) =>
        heading is not null && stored.StartsWith(heading + "\n", StringComparison.Ordinal) ? stored[(heading.Length + 1)..] : stored;
}

/// <summary>
/// Cuts a text into passages of a few whole lines, each with its exact line range. A passage never crosses a Markdown heading,
/// and a blank line ends it once it holds <see cref="MinLines"/> lines, so a short paragraph is merged with the next one.
/// </summary>
internal static class PassageSplitter
{
    /// <summary>The fewest lines a passage ends on at a paragraph break.</summary>
    public const int MinLines = 3;

    /// <summary>A passage stops growing past this many characters (a single longer line stays whole).</summary>
    public const int MaxChars = 2000;

    /// <summary>The longest text embedded for one passage; only a pathological single line reaches it.</summary>
    public const int MaxEmbeddedChars = 8000;

    /// <summary>Splits <paramref name="content"/> into passages in document order.</summary>
    public static IReadOnlyList<Passage> Split(string content, bool markdown, PassageOptions options)
    {
        string normalized = content.ReplaceLineEndings("\n");
        string[] lines = normalized.Split('\n');
        (string?[] headings, HashSet<int> headingLines) = ChunkLocator.Outline(normalized, markdown);
        var result = new List<Passage>();
        int max = Math.Max(1, options.Lines);
        int overlap = Math.Clamp(options.Overlap, 0, max - 1);
        int i = 0;

        while (i < lines.Length)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                i++;
                continue;
            }

            int start = i;
            int count = 0;
            int last = i;
            int j = i;
            int chars = 0;
            bool stoppedAtHeading = false;
            while (j < lines.Length && count < max)
            {
                if (j > start && headingLines.Contains(j + 1))
                {
                    stoppedAtHeading = true;
                    break;
                }

                if (string.IsNullOrWhiteSpace(lines[j]))
                {
                    if (count >= MinLines)
                    {
                        break;
                    }

                    j++;
                    continue;
                }

                if (count > 0 && chars + lines[j].Length > MaxChars)
                {
                    break;
                }

                count++;
                chars += lines[j].Length;
                last = j;
                j++;
            }

            string text = string.Join('\n', lines[start..(last + 1)]);
            result.Add(new Passage(start + 1, last + 1, text, headings[start]));

            if (!stoppedAtHeading && lines.Skip(last + 1).All(string.IsNullOrWhiteSpace))
            {
                break;
            }

            // Continue after the passage, stepping back over `overlap` lines unless a heading starts the next one.
            int next = stoppedAtHeading ? j : Math.Max(start + 1, last + 1 - overlap);
            i = next;
        }

        return result;
    }
}
