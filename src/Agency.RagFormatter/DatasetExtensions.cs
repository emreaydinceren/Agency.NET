
using Agency.Common;
using System.Text;

namespace Agency.RagFormatter;

/// <summary>
/// Extension methods for formatting a <see cref="Dataset"/> for consumption in
/// retrieval-augmented-generation (RAG) prompts.
/// </summary>
public static class DatasetExtensions
{
    /// <summary>
    /// Renders the <paramref name="dataset"/> as a Markdown table, with column names as the
    /// header row followed by a separator row and one row per dataset row. Null cell values are
    /// rendered as <c>NULL</c>.
    /// </summary>
    /// <param name="dataset">The dataset to render.</param>
    /// <returns>The dataset formatted as a Markdown table.</returns>
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