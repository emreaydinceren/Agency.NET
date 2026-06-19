using System.Management.Automation;
using System.Text;

namespace Agency.Harness.Tools;

internal static class Extensions
{
    private static bool IsDisplayableProperty(PSPropertyInfo property)
        => property.IsGettable && property is not PSScriptProperty;

    /// <summary>
    /// True when the object is a bare scalar (string or primitive) rather than a structured object.
    /// Native commands (tasklist, git, ipconfig, …) emit their output as scalar strings; reflecting
    /// over their properties yields only <c>String.Length</c>, so they must be rendered verbatim.
    /// </summary>
    private static bool IsScalar(PSObject? obj)
    {
        object? baseObject = obj?.BaseObject;
        return baseObject is string or decimal || (baseObject is not null && baseObject.GetType().IsPrimitive);
    }

    /// <summary>
    /// Returns the property names PowerShell itself would display by default — the
    /// <c>DefaultDisplayPropertySet</c> from the object's type data (e.g. Id, Handles, CPU, SI, Name
    /// for a Process). Honoring it produces console-native output and, crucially, avoids ever probing
    /// throwing getters that PowerShell never shows: Process.ExitTime/ExitCode raise
    /// GetValueInvocationException while the process is running. Returns null for objects with no
    /// declared set — notably the PSCustomObjects that <c>Select-Object Foo, Bar</c> projects — so
    /// callers fall back to enumerating every property.
    /// </summary>
    private static IReadOnlyList<string>? DefaultDisplayColumns(PSObject obj)
    {
        // PSObject.Members is the real member collection (equivalent to $o.PSObject.Members in script,
        // not the adapted $o.Members which hides PSStandardMembers). The indexer returns null when a
        // member is absent rather than throwing.
        if (obj.Members["PSStandardMembers"]?.Value is not PSMemberSet standardMembers)
        {
            return null;
        }

        if (standardMembers.Members["DefaultDisplayPropertySet"]?.Value is not PSPropertySet displaySet)
        {
            return null;
        }

        return displaySet.ReferencedPropertyNames is { Count: > 0 } names ? names : null;
    }

    /// <summary>
    /// The columns to render for an object: its default display set when one exists, otherwise every
    /// property (the prior behavior, preserved for projected/custom objects).
    /// </summary>
    private static IEnumerable<string> ColumnNamesFor(PSObject obj)
        => DefaultDisplayColumns(obj) ?? obj.Properties.Select(static p => p.Name);

    private static bool TryGetDisplayValue(PSPropertyInfo property, out object? value)
    {
        try
        {
            // Some adapted .NET properties have a getter (IsGettable == true) but throw
            // when invoked — e.g. Process.ExitCode on a still-running process raises
            // GetValueInvocationException. There is no way to detect this without invoking,
            // so we swallow and skip the property.
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

        // Native-command output (tasklist, git, ipconfig, …) arrives as bare strings. Reflecting over
        // their .Properties yields only String.Length, producing a useless "| Length |" table — so
        // when every item is a scalar, emit the text verbatim instead of tabulating.
        if (list.All(IsScalar))
        {
            return string.Join(Environment.NewLine, list.Select(static o => o.BaseObject?.ToString() ?? string.Empty));
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

            foreach (var name in ColumnNamesFor(obj))
            {
                if (seen.Add(name))
                {
                    columns.Add(name);
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
                string cell = string.Empty;

                try
                {
                    var property = obj?.Properties[columns[i]];
                    if (property is not null && TryGetDisplayValue(property, out object? value))
                    {
                        cell = value?.ToString() ?? string.Empty;
                    }
                }
                catch
                {
                    cell = string.Empty;
                }

                row[i] = EscapeCell(cell);
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
        // A scalar (e.g. a single line of native-command output) has no meaningful properties
        // beyond String.Length — render it verbatim rather than as a "- **Length**: N" bullet.
        if (IsScalar(result))
        {
            return result.BaseObject?.ToString() ?? string.Empty;
        }

        var sb = new StringBuilder();

        // Prefer the default display set (console-native, skips throwing getters like Process.ExitTime);
        // these names may include script properties (e.g. CPU), so read them directly rather than
        // filtering through IsDisplayableProperty.
        if (DefaultDisplayColumns(result) is { } defaults)
        {
            foreach (var name in defaults)
            {
                if (result.Properties[name] is { } prop && TryGetDisplayValue(prop, out object? value))
                {
                    sb.AppendLine($"- **{name}**: {value}");
                }
            }
            return sb.ToString();
        }

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