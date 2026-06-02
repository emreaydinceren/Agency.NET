
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

        foreach (string rawLine in lines)
        {
            string line = rawLine.TrimEnd('\r');

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
