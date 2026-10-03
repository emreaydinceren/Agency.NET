using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace Agency.Indexer;

/// <summary>
/// Strips an HTML document down to its readable text: drops scripts, styles and other non-content elements,
/// decodes entities, and keeps block elements on separate lines.
/// </summary>
internal static partial class HtmlTextExtractor
{
    private static readonly HashSet<string> RemovedElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "noscript", "template", "svg", "head",
    };

    private static readonly HashSet<string> BlockElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "address", "article", "aside", "blockquote", "br", "dd", "div", "dl", "dt", "figcaption", "figure",
        "footer", "h1", "h2", "h3", "h4", "h5", "h6", "header", "hr", "li", "main", "nav", "ol", "p", "pre",
        "section", "table", "td", "th", "tr", "ul",
    };

    /// <summary>Returns the readable text of <paramref name="html"/>, prefixed by the document title when present.</summary>
    public static string Extract(string html)
    {
        using IDocument document = new HtmlParser().ParseDocument(html);
        var text = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(document.Title))
        {
            text.Append(document.Title).Append('\n');
        }

        Append(document.Body ?? document.DocumentElement, text);
        return Normalize(text.ToString());
    }

    private static void Append(INode node, StringBuilder text)
    {
        foreach (INode child in node.ChildNodes)
        {
            if (child is IText textNode)
            {
                text.Append(textNode.Data);
            }
            else if (child is IElement element && !RemovedElements.Contains(element.LocalName))
            {
                bool block = BlockElements.Contains(element.LocalName);
                if (block)
                {
                    text.Append('\n');
                }

                Append(element, text);

                if (block)
                {
                    text.Append('\n');
                }
            }
        }
    }

    /// <summary>Collapses runs of spaces within lines and runs of blank lines to a single blank line.</summary>
    private static string Normalize(string raw)
    {
        IEnumerable<string> lines = raw.Split('\n').Select(line => InlineWhitespace().Replace(line, " ").Trim());
        return BlankLines().Replace(string.Join('\n', lines), "\n\n").Trim();
    }

    [GeneratedRegex(@"[ \t\r\f\v ]+")]
    private static partial Regex InlineWhitespace();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLines();
}
