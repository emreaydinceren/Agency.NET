using System.Data.Common;

namespace Agency.Common;

public class Dataset(IReadOnlyCollection<DbColumn> columns, IReadOnlyList<object?[]> rows)
{
    public IReadOnlyCollection<DbColumn> Columns { get; init; } = columns;

    private readonly IDictionary<string, DbColumn> _columnDict = columns.ToDictionary(c => c.ColumnName, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<object?[]> Rows { get; init; } = rows;

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
