# Agency.Common

#common #foundation #dataset #abstractions

## What It Is

`Agency.Common` is the foundational shared-types library for the entire Agency solution. It defines the core data structures used across the RAG pipeline — from SQL query results through to the formatter and LLM context builder. All other projects depend on this library either directly or transitively; it has no dependencies of its own (other than the .NET BCL).

## Key Types

### `Dataset`

A lightweight, immutable tabular result set returned by SQL runners. It wraps an ordered list of column descriptors (`IColumnMetadata`) and a list of rows, where each row is an `object?[]` array indexed by column ordinal.

```csharp
// Constructing a Dataset (done internally by SQL runners)
var dataset = new Dataset(columns, rows);

// Reading rows
foreach (object?[] row in dataset.Rows)
{
    string? name = row[0]?.ToString();
}

// Reading column metadata
foreach (IColumnMetadata col in dataset.Columns)
{
    Console.WriteLine($"{col.ColumnOrdinal}: {col.ColumnName}");
}
```

### `IColumnMetadata`

A thin interface describing a single column in a `Dataset`. SQL runners adapt their provider-specific `DbColumn` to this interface so consumers remain provider-agnostic.

```csharp
public interface IColumnMetadata
{
    string? ColumnName  { get; }
    int?    ColumnOrdinal { get; }
}
```

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.Sql.Postgre]] | Returns `Dataset` from `QueryAsync` |
| [[Agency.Sql.Sqlite]] | Returns `Dataset` from `QueryAsync` |
| [[Agency.RagFormatter]] | Formats a `Dataset` into Markdown using `ToMarkdownTable()` |
| [[Agency.Embeddings.Common]] | Sibling foundational library; no direct dependency |
| [[Agency.Llm.Common]] | No direct dependency; sits on a parallel branch |

## Design Notes

- **Zero dependencies** — deliberately kept dependency-free so it can be referenced by any layer without pulling in provider-specific NuGet packages.
- **`object?[]` rows** — the raw ADO.NET representation is preserved rather than being eagerly converted to `Dictionary<string, object?>`. This avoids per-row allocation overhead when only a subset of columns is needed.
- **`IColumnMetadata`** uses `int?` / `string?` (nullable) to faithfully mirror `DbColumn`, which can have null names in edge cases (e.g. computed expressions without an alias).
