# Agency.Common

Core abstractions for the Agency AI Toolkit — shared data types used across all Agency packages.

## Install

```
dotnet add package Agency.Common
```

## Types

- **`Dataset`** — tabular result set (rows + column metadata) returned by SQL runners and consumed by `Agency.RagFormatter`.
- **`IColumnMetadata`** — describes a single column: name and ordinal position.

## Usage

```csharp
// Dataset is typically produced by Agency.Sql.* runners, not constructed directly.
// Use Agency.RagFormatter to render it for LLM context:
string markdownTable = dataset.ToMarkdownTable();
```

Part of the [Agency AI Toolkit](https://github.com/emre/Agency) — an open-source .NET RAG pipeline.
