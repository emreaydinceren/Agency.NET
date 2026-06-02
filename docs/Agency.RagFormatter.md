# Agency.RagFormatter
#rag #formatter #markdown #dataset

## What It Is

Agency.RagFormatter is the formatting layer that converts a [[Agency.Common]] `Dataset` into a Markdown table string ready for injection into an LLM prompt as RAG context.

**Namespace:** `Agency.RagFormatter`

## API Surface

```csharp
// File: src/Agency.RagFormatter/DatasetExtensions.cs
using Agency.Common;
using Agency.RagFormatter;

public static class DatasetExtensions
{
    public static string ToMarkdownTable(this Dataset dataset);
}
```

## How It Works

`ToMarkdownTable()` builds a standard Markdown table in three passes over the `Dataset`:

1. **Header row** — appends each `column.ColumnName` separated by `|`.
2. **Separator row** — appends `--- |` for every column.
3. **Data rows** — iterates `dataset.Rows`; each cell value is rendered via `ToString()`, with `null` rendered as `"NULL"`.

The result is returned as a single `string`.

**Typical RAG pattern:**

```csharp
using Agency.Common;
using Agency.RagFormatter;

// 1. Query the vector store or SQL database
Dataset results = await sqlRunner.QueryAsync(
    "SELECT title, summary FROM docs ORDER BY embedding <-> vectorize('search query') LIMIT 5");

// 2. Format as Markdown table
string context = results.ToMarkdownTable();

// 3. Inject into the LLM system prompt
string systemPrompt = $"""
    You are a helpful assistant. Use the following documents as context:

    {context}
    """;
```

**Example output:**

```
| title | summary |
| --- | --- |
| My Doc | A short description. |
| Another Doc | NULL |
```

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.Common]] | Extends `Dataset`; column metadata comes from `IColumnMetadata` |
| [[Agency.Sql.Common]] | `Dataset` is produced by SQL runner implementations in this layer |
| [[Agency.Sql.Postgres]] | `PostgreSqlRunner.QueryAsync` returns a `Dataset` that this formats |
| [[Agency.Sql.Sqlite]] | `SqliteRunner.QueryAsync` returns a `Dataset` that this formats |
| [[Agency.Harness]] | Formatted table can be injected into agent context as factual knowledge |
| [[Agency.Console]] | The console demo wires RAG query results through this formatter |

## Design Notes

- **Null sentinel is `"NULL"` not empty string** — this makes missing data visible in the LLM prompt, reducing silent hallucinations caused by blank cells that the model may ignore.
- **No abstraction layer** — the library is intentionally a single static extension method with no interface or configuration; callers that need a different output format should provide their own formatter rather than adding options here.
