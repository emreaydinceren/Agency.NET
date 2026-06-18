
using Spectre.Console;
using System.Text.RegularExpressions;

namespace Agency.Harness.Console;
/// <summary>Renders markdown text to the console using Spectre.Console markup.</summary>
internal static class MarkdownRenderer
{
    /// <summary>Prints a markdown string to the console, translating common constructs to Spectre markup.</summary>
    internal static void Print(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        string[] lines = text.Split('\n');
        bool inCodeBlock = false;
        string codeLang = "";

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd('\r');

            // ── fenced code block ────────────────────────────────────────────────
            if (line.StartsWith("```"))
            {
                if (!inCodeBlock)
                {
                    inCodeBlock = true;
                    codeLang = line[3..].Trim();
                    string label = string.IsNullOrEmpty(codeLang) ? "code" : codeLang;
                    AnsiConsole.MarkupLine($"[dim]── {Markup.Escape(label)} ─────────────────────────────[/]");
                }
                else
                {
                    inCodeBlock = false;
                    AnsiConsole.MarkupLine("[dim]─────────────────────────────────────────[/]");
                }

                continue;
            }

            if (inCodeBlock)
            {
                AnsiConsole.MarkupLine($"[dim]{Markup.Escape(line)}[/]");
                continue;
            }

            // ── blank line ───────────────────────────────────────────────────────
            if (string.IsNullOrEmpty(line))
            {
                AnsiConsole.MarkupLine(string.Empty);
                continue;
            }

            // ── GFM pipe table ───────────────────────────────────────────────────
            // A header row plus a delimiter row (e.g. "| --- | :---: |") on the very
            // next line. Detected before the horizontal-rule/heading cases because a
            // table is a multi-line block, not a per-line construct.
            if (TryParseTable(lines, i, out ParsedTable? table, out int next))
            {
                AnsiConsole.Write(BuildTable(table!));
                i = next - 1;   // -1 offsets the loop's i++
                continue;
            }

            // ── horizontal rule ──────────────────────────────────────────────────
            if (line is "---" or "***" or "___")
            {
                AnsiConsole.MarkupLine("[dim]─────────────────────────────────────────────[/]");
                continue;
            }

            // ── headings ─────────────────────────────────────────────────────────
            if (line.StartsWith("### "))
            {
                AnsiConsole.MarkupLine($"[bold]{ApplyInline(line[4..])}[/]");
                continue;
            }

            if (line.StartsWith("## "))
            {
                AnsiConsole.MarkupLine($"[bold underline]{ApplyInline(line[3..])}[/]");
                continue;
            }

            if (line.StartsWith("# "))
            {
                AnsiConsole.MarkupLine($"[bold underline]{ApplyInline(line[2..])}[/]");
                continue;
            }

            // ── blockquote ───────────────────────────────────────────────────────
            if (line.StartsWith("> "))
            {
                AnsiConsole.MarkupLine($"[dim]│[/] {ApplyInline(line[2..])}");
                continue;
            }

            // ── unordered list item ──────────────────────────────────────────────
            if (line.Length > 2 && line[1] == ' ' && (line[0] is '-' or '*' or '+'))
            {
                AnsiConsole.MarkupLine($"  [yellow]•[/] {ApplyInline(line[2..])}");
                continue;
            }

            // ── ordered list item (e.g. "1. ") ──────────────────────────────────
            int dotIdx = line.IndexOf(". ", StringComparison.Ordinal);
            if (dotIdx > 0 && dotIdx < 4 && line[..dotIdx].All(char.IsAsciiDigit))
            {
                string number = line[..dotIdx];
                AnsiConsole.MarkupLine($"  [yellow]{Markup.Escape(number)}.[/] {ApplyInline(line[(dotIdx + 2)..])}");
                continue;
            }

            // ── regular paragraph line ───────────────────────────────────────────
            AnsiConsole.MarkupLine(ApplyInline(line));
        }
    }

    /// <summary>A parsed GFM pipe table: its header cells and zero or more body rows.</summary>
    internal sealed record ParsedTable(
        IReadOnlyList<string> Headers,
        IReadOnlyList<IReadOnlyList<string>> Rows);

    /// <summary>
    /// Attempts to parse a GFM pipe table beginning at <paramref name="start"/>.
    /// Succeeds only when the line at <paramref name="start"/> is a header row and the
    /// line immediately after it is a delimiter row (cells of optional colons and dashes).
    /// On success, <paramref name="next"/> is the index of the first line after the table.
    /// </summary>
    internal static bool TryParseTable(string[] lines, int start, out ParsedTable? table, out int next)
    {
        table = null;
        next = start;

        if (start + 1 >= lines.Length)
        {
            return false;
        }

        string header = lines[start].TrimEnd('\r');
        if (!header.Contains('|') || !IsDelimiterRow(lines[start + 1].TrimEnd('\r')))
        {
            return false;
        }

        var rows = new List<IReadOnlyList<string>>();
        int i = start + 2;
        while (i < lines.Length)
        {
            string row = lines[i].TrimEnd('\r');
            if (row.Length == 0 || !row.Contains('|'))
            {
                break;
            }

            rows.Add(SplitRow(row));
            i++;
        }

        table = new ParsedTable(SplitRow(header), rows);
        next = i;
        return true;
    }

    /// <summary>Builds a Spectre <see cref="Table"/> from a parsed table, normalising ragged rows to the header width.</summary>
    internal static Table BuildTable(ParsedTable table)
    {
        var rendered = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);

        foreach (string headerCell in table.Headers)
        {
            rendered.AddColumn(new TableColumn(ApplyInline(headerCell)));
        }

        int columns = table.Headers.Count;
        foreach (IReadOnlyList<string> row in table.Rows)
        {
            string[] cells = new string[columns];
            for (int c = 0; c < columns; c++)
            {
                cells[c] = ApplyInline(c < row.Count ? row[c] : string.Empty);
            }

            rendered.AddRow(cells);
        }

        return rendered;
    }

    /// <summary>True when every cell of a row is a GFM delimiter token (e.g. <c>---</c>, <c>:--:</c>).</summary>
    private static bool IsDelimiterRow(string line)
    {
        if (!line.Contains('|') && !line.Contains('-'))
        {
            return false;
        }

        List<string> cells = SplitRow(line);
        return cells.Count > 0 && cells.All(static cell => Regex.IsMatch(cell, @"^:?-+:?$"));
    }

    /// <summary>Splits a table row into trimmed cells, ignoring the optional leading/trailing pipes.</summary>
    private static List<string> SplitRow(string line)
    {
        string s = line.Trim();
        if (s.StartsWith('|'))
        {
            s = s[1..];
        }

        if (s.EndsWith('|'))
        {
            s = s[..^1];
        }

        return s.Split('|').Select(static cell => cell.Trim()).ToList();
    }

    /// <summary>Applies inline markdown spans (bold, italic, code) to a single line, returning Spectre markup.</summary>
    private static string ApplyInline(string text)
    {
        // Escape Spectre's bracket syntax first so literal [ ] in the source
        // don't get misinterpreted as markup tags.
        string s = Markup.Escape(text);

        // Bold + italic: ***text*** or ___text___
        s = Regex.Replace(s, @"\*\*\*(.+?)\*\*\*", "[bold italic]$1[/]");
        s = Regex.Replace(s, @"___(.+?)___", "[bold italic]$1[/]");

        // Bold: **text** or __text__
        s = Regex.Replace(s, @"\*\*(.+?)\*\*", "[bold]$1[/]");
        s = Regex.Replace(s, @"__(.+?)__", "[bold]$1[/]");

        // Italic: *text* or _text_
        s = Regex.Replace(s, @"\*([^\*\n]+?)\*", "[italic]$1[/]");
        s = Regex.Replace(s, @"_([^_\n]+?)_", "[italic]$1[/]");

        // Inline code: `text`
        s = Regex.Replace(s, @"`([^`\n]+?)`", "[cyan]$1[/]");

        return s;
    }
}
