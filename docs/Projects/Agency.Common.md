# Agency.Common
#common #foundation #dataset #abstractions

## What It Is

Agency.Common is the zero-dependency shared contract library for the Agency AI Toolkit. It defines the tabular dataset types (`Dataset` and `IColumnMetadata`) consumed by SQL runners, the RAG formatter, and other downstream LLM components.

Namespace: `Agency.Common`

## API Surface

```csharp
using Agency.Common;
```

| Type | Member | Signature |
|---|---|---|
| `IColumnMetadata` | Property | `string? ColumnName { get; }` |
| `IColumnMetadata` | Property | `int? ColumnOrdinal { get; }` |
| `Dataset` | Constructor | `Dataset(IReadOnlyCollection<IColumnMetadata> columns, IReadOnlyList<object?[]> rows)` |
| `Dataset` | Constructor | `Dataset(IReadOnlyCollection<IColumnMetadata> columns)` |
| `Dataset` | Property | `IReadOnlyCollection<IColumnMetadata> Columns { get; init; }` |
| `Dataset` | Property | `IReadOnlyList<object?[]> Rows { get; init; }` |
| `Dataset` | Method | `void AddRow(object?[] values)` |
| `Dataset` | Indexer | `object? this[int columnIndex, int rowIndex] { get; }` |
| `Dataset` | Indexer | `object? this[string columnName, int rowIndex] { get; }` |

## Dependencies

None — Agency.Common has no project or package references.

## Related

- [[Agency.Harness]]
- [[Home]]
