using System.Text.RegularExpressions;

namespace Agency.Indexer;

/// <summary>Where a chunk sits in its source file.</summary>
/// <param name="Heading">The Markdown heading path above the chunk (<c>Guide &gt; Install</c>), or <see langword="null"/>.</param>
/// <param name="StartLine">The 1-based first line of the chunk, or <see langword="null"/> when it could not be located.</param>
/// <param name="EndLine">The 1-based last line of the chunk, or <see langword="null"/> when it could not be located.</param>
internal sealed record ChunkLocation(string? Heading, int? StartLine, int? EndLine);

/// <summary>
/// Finds each chunk's line range and heading path in the text it was cut from, so a search hit lets an agent jump to the
/// section. The splitter does not report offsets, so a chunk is found by its first and last line; a chunk whose whitespace
/// the splitter rewrote is reported without a location rather than a wrong one.
/// </summary>
internal static partial class ChunkLocator
{
    [GeneratedRegex(@"^ {0,3}(#{1,6})[ \t]+(.+?)[ \t]*#*[ \t]*$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex HeadingLine();

    /// <summary>Locates <paramref name="chunks"/> (in document order) within <paramref name="content"/>.</summary>
    /// <param name="content">The text the chunks were cut from.</param>
    /// <param name="chunks">The chunk texts.</param>
    /// <param name="markdown">Whether to track Markdown headings.</param>
    public static IReadOnlyList<ChunkLocation> Locate(string content, IReadOnlyList<string> chunks, bool markdown)
    {
        List<int> lineStarts = LineStarts(content);
        List<(int Line, int Level, string Text)> headings = markdown ? Headings(content, lineStarts) : [];
        var result = new List<ChunkLocation>(chunks.Count);
        int cursor = 0;

        foreach (string chunk in chunks)
        {
            string[] lines = chunk.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            int start = lines.Length == 0 ? -1 : IndexOfFrom(content, lines[0], cursor);
            if (start < 0)
            {
                result.Add(new ChunkLocation(null, null, null));
                continue;
            }

            // Chunks overlap, so the next one may begin before this one ends; only move past this start.
            cursor = start + 1;
            int end = IndexOfFrom(content, lines[^1], start);
            int startLine = LineOf(lineStarts, start);
            int endLine = end < 0 ? startLine : Math.Max(startLine, LineOf(lineStarts, end));
            result.Add(new ChunkLocation(HeadingPath(headings, startLine), startLine, endLine));
        }

        return result;
    }

    /// <summary>The heading path in force at every line, and which lines are headings.</summary>
    /// <param name="content">The text, with <c>\n</c> line endings.</param>
    /// <param name="markdown">Whether to track Markdown headings.</param>
    /// <returns>The heading path of line <c>n</c> at index <c>n - 1</c> (or <see langword="null"/>), and the 1-based heading lines.</returns>
    internal static (string?[] PathByLine, HashSet<int> HeadingLines) Outline(string content, bool markdown)
    {
        List<int> lineStarts = LineStarts(content);
        List<(int Line, int Level, string Text)> headings = markdown ? Headings(content, lineStarts) : [];
        var paths = new string?[lineStarts.Count];
        var stack = new List<(int Level, string Text)>();
        int next = 0;
        for (int line = 1; line <= lineStarts.Count; line++)
        {
            while (next < headings.Count && headings[next].Line <= line)
            {
                stack.RemoveAll(p => p.Level >= headings[next].Level);
                stack.Add((headings[next].Level, headings[next].Text));
                next++;
            }

            paths[line - 1] = stack.Count == 0 ? null : string.Join(" > ", stack.Select(p => p.Text));
        }

        return (paths, headings.Select(h => h.Line).ToHashSet());
    }

    private static int IndexOfFrom(string content, string needle, int from)
    {
        int found = content.IndexOf(needle, from, StringComparison.Ordinal);
        return found >= 0 || from == 0 ? found : content.IndexOf(needle, StringComparison.Ordinal);
    }

    private static List<int> LineStarts(string content)
    {
        var starts = new List<int> { 0 };
        for (int i = 0; i < content.Length; i++)
        {
            if (content[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }

        return starts;
    }

    private static int LineOf(List<int> lineStarts, int offset)
    {
        int index = lineStarts.BinarySearch(offset);
        return (index >= 0 ? index : ~index - 1) + 1;
    }

    private static List<(int Line, int Level, string Text)> Headings(string content, List<int> lineStarts)
    {
        var headings = new List<(int, int, string)>();
        bool inFence = false;
        for (int i = 0; i < lineStarts.Count; i++)
        {
            int from = lineStarts[i];
            int to = i + 1 < lineStarts.Count ? lineStarts[i + 1] : content.Length;
            string line = content[from..to].TrimEnd('\r', '\n');
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal) || line.TrimStart().StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (!inFence && HeadingLine().Match(line) is { Success: true } match)
            {
                headings.Add((i + 1, match.Groups[1].Length, match.Groups[2].Value));
            }
        }

        return headings;
    }

    /// <summary>The path of the headings in force at <paramref name="line"/>: the last heading at or before it, and its ancestors.</summary>
    private static string? HeadingPath(List<(int Line, int Level, string Text)> headings, int line)
    {
        var path = new List<(int Level, string Text)>();
        foreach ((int headingLine, int level, string text) in headings)
        {
            if (headingLine > line)
            {
                break;
            }

            path.RemoveAll(p => p.Level >= level);
            path.Add((level, text));
        }

        return path.Count == 0 ? null : string.Join(" > ", path.Select(p => p.Text));
    }
}
