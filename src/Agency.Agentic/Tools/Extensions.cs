using Agency.Common;
using System.Management.Automation;
using System.Text;

namespace Agency.Agentic.Tools;

internal static class Extensions
{
    internal static string ToMarkdownTable(this IEnumerable<PSObject> results)
    {
        var list = results.ToList();
        if (list.Count == 0)
        {
            return "_(no results)_";
        }

        // 1. Collect all property names across all objects
        var allProps = new SortedSet<string>(
            list.SelectMany(r => r.Properties)
                .Where(p => p.IsGettable)
                .Select(p => p.Name)).ToList();

        var columns = allProps.Select((name, index) => new ColumnMetadata(name, index)).ToList();

        var columnsDict = columns.ToDictionary(c => c.ColumnName ?? string.Empty, c => c.ColumnOrdinal ?? 0);

        var dataset = new Dataset(columns);
        foreach(var psObject in list)
        {
            object?[] values = new object?[columns.Count];
            foreach(var prop in psObject.Properties)
            {
                values[columnsDict[prop.Name]] = prop.Value;
                dataset.AddRow(values);
            }
        }

        return dataset.ToMarkdownTable();
    }

    internal static string ToMarkdown(this PSObject result)
    {
        var sb = new StringBuilder();

        foreach (var prop in result.Properties)
        {
            if (prop.IsGettable == false)
            {
                continue;
            }

            sb.AppendLine($"- **{prop.Name}**: {prop.Value}");
            sb.AppendLine(sb.ToString());
        }
        return sb.ToString();
    }

    public static string ToMarkdownTable(this Dataset dataset)
    {
        var sb = new StringBuilder();
        // Header
        sb.Append("| ");
        foreach (var column in dataset.Columns)
        {
            sb.Append(column.ColumnName).Append(" | ");
        }
        sb.AppendLine();
        // Separator
        sb.Append("| ");
        foreach (var _ in dataset.Columns)
        {
            sb.Append("--- | ");
        }
        sb.AppendLine();
        // Rows
        foreach (var row in dataset.Rows)
        {
            sb.Append("| ");
            for (int i = 0; i < dataset.Columns.Count; i++)
            {
                sb.Append(row[i]?.ToString() ?? "NULL").Append(" | ");
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    internal record class ColumnMetadata(string name, int ordinal) : IColumnMetadata
    {
        public string? ColumnName => name;

        public int? ColumnOrdinal => ordinal;
    }
}