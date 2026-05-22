using System.Management.Automation;
using System.Text;

namespace Agency.Agentic.Tools;

internal static class Extensions
{
    private static bool IsDisplayableProperty(PSPropertyInfo property)
        => property.IsGettable && property is not PSScriptProperty;

    private static bool TryGetDisplayValue(PSPropertyInfo property, out object? value)
    {
        try
        {
            value = property.Value;
            return true;
        }
        catch
        {
            value = null;
            return false;
        }
    }


    /// <summary>
    /// Converts a collection of PSObjects into a GitHub-flavored Markdown table.
    /// Returns an empty string if the collection is null or empty.
    /// </summary>
    internal static string ToMarkdownTable(this IEnumerable<PSObject> results)
    {
        if (results is null)
        {
            return string.Empty;
        }

        var list = results as IList<PSObject> ?? results.ToList();
        if (list.Count == 0)
        {
            return string.Empty;
        }

        // Preserve column order as they first appear across objects.
        var columns = new List<string>();
        var seen = new HashSet<string>();
        foreach (var obj in list)
        {
            if (obj?.Properties is null)
            {
                continue;
            }

            foreach (var prop in obj.Properties)
            {
                if (seen.Add(prop.Name))
                {
                    columns.Add(prop.Name);
                }
            }
        }

        if (columns.Count == 0)
        {
            return string.Empty;
        }

        // Build row data as strings, tracking max width per column for pretty alignment.
        var rows = new List<string[]>(list.Count);
        var widths = columns.Select(c => c.Length).ToArray();

        foreach (var obj in list)
        {
            var row = new string[columns.Count];

            for (int i = 0; i < columns.Count; i++)
            {
                var value = obj?.Properties[columns[i]]?.Value;
                row[i] = EscapeCell(value?.ToString() ?? string.Empty);
                if (row[i].Length > widths[i])
                {
                    widths[i] = row[i].Length;
                }
            }
            rows.Add(row);
        }

        var sb = new StringBuilder();

        // Header
        sb.Append('|');
        for (int i = 0; i < columns.Count; i++)
        {
            sb.Append(' ').Append(columns[i].PadRight(widths[i])).Append(" |");
        }

        sb.AppendLine();

        // Separator
        sb.Append('|');
        for (int i = 0; i < columns.Count; i++)
        {
            sb.Append(' ').Append(new string('-', widths[i])).Append(" |");
        }

        sb.AppendLine();

        // Rows
        foreach (var row in rows)
        {
            sb.Append('|');
            for (int i = 0; i < columns.Count; i++)
            {
                sb.Append(' ').Append(row[i].PadRight(widths[i])).Append(" |");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string EscapeCell(string value)
    {
        // Markdown tables break on pipes and newlines. Replace them to keep the table intact.
        return value
            .Replace("|", "\\|")
            .Replace("\r\n", " ")
            .Replace("\n", " ")
            .Replace("\r", " ");
    }


    internal static string ToMarkdown(this PSObject result)
    {
        var sb = new StringBuilder();

        foreach (var prop in result.Properties)
        {
            if (!IsDisplayableProperty(prop))
            {
                continue;
            }

            if (TryGetDisplayValue(prop, out object? value))
            {
                sb.AppendLine($"- **{prop.Name}**: {value}");
            }
        }
        return sb.ToString();
    }
}