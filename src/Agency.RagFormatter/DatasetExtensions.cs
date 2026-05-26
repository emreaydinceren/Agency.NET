
using Agency.Common;
using System.Text;

namespace Agency.RagFormatter;
public static class DatasetExtensions
{
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
}