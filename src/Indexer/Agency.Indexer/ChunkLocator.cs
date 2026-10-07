namespace Agency.Indexer;

/// <summary>Where a chunk sits in its source file.</summary>
/// <param name="Heading">The nearest Markdown heading at or above the chunk, or <see langword="null"/>.</param>
/// <param name="StartLine">The 1-based first line of the chunk, or <see langword="null"/> when it could not be located.</param>
/// <param name="EndLine">The 1-based last line of the chunk, or <see langword="null"/> when it could not be located.</param>
internal sealed record ChunkSpan(string? Heading, int? StartLine, int? EndLine);

/// <summary>
/// Maps chunks back to line spans of the text they were cut from. The text splitter reports no offsets and
/// may re-join source lines with spaces, so each chunk is matched as a whitespace-normalized substring,
/// scanning forward from the previous chunk's start; a chunk that cannot be matched gets no span rather than
/// a wrong one.
/// </summary>
internal static class ChunkLocator
{
    /// <summary>Locates every chunk of <paramref name="content"/>, in order.</summary>
    /// <param name="content">The full text the chunks were split from.</param>
    /// <param name="chunks">The chunk texts, in document order.</param>
    /// <param name="markdown">Whether to track <c>#</c> headings.</param>
    public static IReadOnlyList<ChunkSpan> Locate(string content, IReadOnlyList<string> chunks, bool markdown)
    {
        string text = content.ReplaceLineEndings("\n");
        string[] lines = text.Split('\n').Select(l => l.Trim()).ToArray();
        string?[] headings = markdown ? HeadingPerLine(lines) : new string?[lines.Length];
        (string normalized, List<int> lineOf) = Normalize(text);

        var spans = new List<ChunkSpan>(chunks.Count);
        int cursor = 0;
        foreach (string chunk in chunks)
        {
            string wanted = Normalize(chunk.ReplaceLineEndings("\n")).Text;
            int at = wanted.Length == 0 ? -1 : normalized.IndexOf(wanted, cursor, StringComparison.Ordinal);
            if (at < 0)
            {
                spans.Add(new ChunkSpan(null, null, null));
                continue;
            }

            // Overlap lets the next chunk begin before this one ends, so only step past this chunk's start.
            cursor = at + 1;
            int startLine = lineOf[at];
            spans.Add(new ChunkSpan(headings[startLine], startLine + 1, lineOf[at + wanted.Length - 1] + 1));
        }

        return spans;
    }

    /// <summary>Collapses whitespace runs to one space, trims the ends, and records the 0-based source line of each remaining character.</summary>
    private static (string Text, List<int> LineOf) Normalize(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        var lineOf = new List<int>(text.Length);
        int line = 0;
        bool pendingSpace = false;
        int spaceLine = 0;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!pendingSpace)
                {
                    pendingSpace = true;
                    spaceLine = line;
                }
            }
            else
            {
                if (pendingSpace && sb.Length > 0)
                {
                    sb.Append(' ');
                    lineOf.Add(spaceLine);
                }

                pendingSpace = false;
                sb.Append(c);
                lineOf.Add(line);
            }

            if (c == '\n')
            {
                line++;
            }
        }

        return (sb.ToString(), lineOf);
    }

    private static string?[] HeadingPerLine(string[] lines)
    {
        var result = new string?[lines.Length];
        string? current = null;
        bool inFence = false;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("```", StringComparison.Ordinal) || lines[i].StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
            }
            else if (!inFence && lines[i].StartsWith('#') && lines[i].TrimStart('#') is { Length: > 0 } rest && rest[0] == ' ')
            {
                current = rest.Trim();
            }

            result[i] = current;
        }

        return result;
    }
}
