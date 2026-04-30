# Agency.Common
#common #foundation #dataset #abstractions

## What It Is

Agency.Common is the shared contract library that defines tabular dataset types used by other projects.

Namespace: Agency.Common

## API Surface

```csharp
using Agency.Common;
// File: src/Agency.Common/Dataset.cs

namespace Agency.Common;

public interface IColumnMetadata
{
    string? ColumnName { get; }
    int? ColumnOrdinal { get; }
}
```

| Type | Member | Signature |
|---|---|---|
| Dataset | Constructor | Dataset(IReadOnlyCollection<IColumnMetadata> columns, IReadOnlyList<object?[]> rows) |
| Dataset | Constructor | Dataset(IReadOnlyCollection<IColumnMetadata> columns) |
| Dataset | Property | IReadOnlyCollection<IColumnMetadata> Columns { get; init; } |
| Dataset | Property | IReadOnlyList<object?[]> Rows { get; init; } |
| Dataset | Method | void AddRow(object?[] values) |
| Dataset | Indexer | object? this[int columnIndex, int rowIndex] { get; } |
| Dataset | Indexer | object? this[string columnName, int rowIndex] { get; } |

```csharp
using Agency.Common;
// File: src/Agency.Common/Dataset.cs

namespace Agency.Common;

public class Dataset
{
    public Dataset(IReadOnlyCollection<IColumnMetadata> columns, IReadOnlyList<object?[]> rows);
    public Dataset(IReadOnlyCollection<IColumnMetadata> columns);

    public IReadOnlyCollection<IColumnMetadata> Columns { get; init; }
    public IReadOnlyList<object?[]> Rows { get; init; }

    public void AddRow(object?[] values);

    public object? this[int columnIndex, int rowIndex] { get; }
    public object? this[string columnName, int rowIndex] { get; }
}
```

## How It Works

`Dataset` stores column metadata and row values together.

- The two-argument constructor accepts prebuilt rows.
- The one-argument constructor starts with an empty internal row list that `AddRow` appends to.
- The integer indexer validates row and column bounds and then returns `Rows[rowIndex][columnIndex]`.
- The string indexer resolves a case-insensitive column name to `IColumnMetadata.ColumnOrdinal` and delegates to the integer indexer.

## How It Relates to Other Projects

- [[Agency.Common]] defines reusable contracts in namespace `Agency.Common`.
- [[Agency.Common]] has no project-to-project references declared in its project file.

## Design Notes

- `IColumnMetadata` keeps both `ColumnName` and `ColumnOrdinal` nullable, so callers can represent incomplete metadata.
- `Dataset` supports both batch initialization and incremental row appends via separate constructors.
- `Dataset` uses a case-insensitive column dictionary internally for name-based lookups.
