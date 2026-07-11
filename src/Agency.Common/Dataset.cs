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
public sealed class Dataset
{
    /// <summary>
    /// Gets the column metadata for the dataset.
    /// </summary>
    public IReadOnlyCollection<IColumnMetadata> Columns { get; init; }

    private readonly Dictionary<string, IColumnMetadata> _columnDict;

    private readonly List<object?[]> rows = new();

    /// <summary>
    /// Gets the row values for the dataset.
    /// </summary>
    public IReadOnlyList<object?[]> Rows { get; init; }

    /// <summary>
    /// Initializes a new dataset with the given column metadata and row values.
    /// </summary>
    /// <param name="columns">The column metadata for the dataset.</param>
    /// <param name="rows">The row values for the dataset.</param>
    public Dataset(IReadOnlyCollection<IColumnMetadata> columns, IReadOnlyList<object?[]> rows)
    {
        this.Columns = columns;
        this._columnDict = columns
        .Where(c => c.ColumnName != null)
        .ToDictionary(c => c.ColumnName!, StringComparer.OrdinalIgnoreCase);
        this.Rows = rows;
    }

    /// <summary>
    /// Initializes a new, empty dataset with the given column metadata. Rows can be added
    /// afterward via <see cref="AddRow"/>.
    /// </summary>
    /// <param name="columns">The column metadata for the dataset.</param>
    public Dataset(IReadOnlyCollection<IColumnMetadata> columns)
    {
        this.Columns = columns;
        this._columnDict = columns
       .Where(c => c.ColumnName != null)
       .ToDictionary(c => c.ColumnName!, StringComparer.OrdinalIgnoreCase);

        this.Rows = rows;
    }

    /// <summary>
    /// Appends a row of values to the dataset.
    /// </summary>
    /// <param name="values">The values for the new row, one per column.</param>
    public void AddRow(object?[] values) => this.rows.Add(values);

    /// <summary>
    /// Gets a value by zero-based column and row index.
    /// </summary>
    /// <remarks>
    /// Out-of-range indexes throw <see cref="ArgumentOutOfRangeException"/> rather than
    /// <see cref="IndexOutOfRangeException"/>, which CA2201 reserves for the runtime's own
    /// array/string indexers. This choice is deliberate, not an oversight (RT13).
    /// </remarks>
    public object? this[int columnIndex, int rowIndex]
    {
        get
        {
            if (rowIndex < 0 || rowIndex >= this.Rows.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(rowIndex), rowIndex, $"Row index {rowIndex} is out of range.");
            }

            if (columnIndex < 0 || columnIndex >= this.Columns.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(columnIndex), columnIndex, $"Column index {columnIndex} is out of range.");
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
