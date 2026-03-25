using System.Data.Common;

namespace Agency.Common;

/// <summary>
/// Represents a tabular result set with column metadata and row values.
/// </summary>
public class Dataset(IReadOnlyCollection<DbColumn> columns, IReadOnlyList<object?[]> rows)
{
    /// <summary>
    /// Gets the column metadata for the dataset.
    /// </summary>
    public IReadOnlyCollection<DbColumn> Columns { get; init; } = columns;

    private readonly IDictionary<string, DbColumn> _columnDict = columns.ToDictionary(c => c.ColumnName, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the row values for the dataset.
    /// </summary>
    public IReadOnlyList<object?[]> Rows { get; init; } = rows;

    /// <summary>
    /// Gets a value by zero-based column and row index.
    /// </summary>
    public object? this[int columnIndex, int rowIndex]
    {
        get
        {
            if (rowIndex < 0 || rowIndex >= Rows.Count)
            {
                throw new IndexOutOfRangeException($"Row index {rowIndex} is out of range.");
            }

            if (columnIndex < 0 || columnIndex >= Columns.Count)
            {
                throw new IndexOutOfRangeException($"Column index {columnIndex} is out of range.");
            }

            return this.Rows[rowIndex][columnIndex];
        }
    }

    /// <summary>
    /// Gets a value by column name and zero-based row index.
    /// </summary>
    public object? this[string columnName, int rowIndex]
    {
        get
        {
            if (!_columnDict.TryGetValue(columnName, out var column))
            {
                throw new ArgumentException($"Column name '{columnName}' does not exist.", nameof(columnName));
            }

            if (column.ColumnOrdinal is null)
            {
                throw new ArgumentException($"Column name '{columnName}' does not have a valid ordinal.", nameof(columnName));
            }

            return this[column.ColumnOrdinal.Value, rowIndex];
        }
    }
}
