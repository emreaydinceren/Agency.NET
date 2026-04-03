namespace Agency.Common;

/// <summary>
/// Represents column metadata in a dataset.
/// </summary>
public interface IColumnMetadata
{
    /// <summary>
    /// Gets the name of the column.
    /// </summary>
    string? ColumnName { get; }

    /// <summary>
    /// Gets the zero-based ordinal of the column.
    /// </summary>
    int? ColumnOrdinal { get; }
}

/// <summary>
/// Represents a tabular result set with column metadata and row values.
/// </summary>
public class Dataset(IReadOnlyCollection<IColumnMetadata> columns, IReadOnlyList<object?[]> rows)
{
    /// <summary>
    /// Gets the column metadata for the dataset.
    /// </summary>
    public IReadOnlyCollection<IColumnMetadata> Columns { get; init; } = columns;

    private readonly IDictionary<string, IColumnMetadata> _columnDict = columns
        .Where(c => c.ColumnName != null)
        .ToDictionary(c => c.ColumnName!, StringComparer.OrdinalIgnoreCase);

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
            if (rowIndex < 0 || rowIndex >= this.Rows.Count)
            {
                throw new IndexOutOfRangeException($"Row index {rowIndex} is out of range.");
            }

            if (columnIndex < 0 || columnIndex >= this.Columns.Count)
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
            if (!this._columnDict.TryGetValue(columnName, out var column))
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
