# Agency.RagFormatter

#rag #formatter #markdown #dataset

## What It Is

`Agency.RagFormatter` converts a [[Agency.Common]] `Dataset` (SQL query result) into a formatted Markdown table string suitable for inclusion in an LLM prompt as RAG (Retrieval-Augmented Generation) context. It is a single-file library with no external dependencies.

## How It Works

The entry point is the `ToMarkdownTable()` extension method on `Dataset`:

```csharp
using Agency.RagFormatter;

// Assume `dataset` came from PostgreSqlRunner or SqliteRunner
string markdownTable = dataset.ToMarkdownTable();
```

Output format:

```markdown
| column_a | column_b | column_c |
|---|---|---|
| value1   | value2   | value3   |
| value4   | value5   | value6   |
```

The method:
1. Reads column names from `dataset.Columns` (using the ordinal as a fallback if `ColumnName` is null).
2. Iterates `dataset.Rows` and formats each `object?` value with `ToString()`, treating `null` as an empty string.
3. Returns the complete table as a single `string`.

### Typical RAG Pattern

```csharp
// 1. Query the vector store or SQL database
Dataset results = await sqlRunner.QueryAsync(
    "SELECT title, summary FROM docs ORDER BY embedding <-> vectorize('RAG query') LIMIT 5");

// 2. Format as Markdown
string context = results.ToMarkdownTable();

// 3. Inject into the LLM system prompt
string systemPrompt = $"""
    You are a helpful assistant. Use the following documents as context:

    {context}
    """;
```

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.Common]] | Operates on `Dataset`; depends on `IColumnMetadata` |
| [[Agency.Sql.Postgre]] | `PostgreSqlRunner.QueryAsync` returns `Dataset` that this formats |
| [[Agency.Sql.Sqlite]] | `SqliteRunner.QueryAsync` returns `Dataset` that this formats |
| [[Agency.Agentic]] | Formatted output can be injected into `Context.Knowledge.Facts` |
| [[Agency.Console]] | The original console demo wires RAG results through this formatter |
