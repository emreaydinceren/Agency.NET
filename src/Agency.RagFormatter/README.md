# Agency.RagFormatter

Formats `Dataset` query results as Markdown tables ready to embed in LLM prompts.

## Install

```
dotnet add package Agency.RagFormatter
```

## Usage

```csharp
using Agency.RagFormatter;

// dataset comes from Agency.Sql.* runners
string context = dataset.ToMarkdownTable();

// embed in a prompt
string prompt = $"Answer based on this data:\n\n{context}\n\nQuestion: {question}";
```

`ToMarkdownTable()` produces a GitHub-flavoured Markdown table with a header row and a separator, making it easy for LLMs to parse column boundaries.

Part of the [Agency AI Toolkit](https://github.com/emre/Agency) — an open-source .NET RAG pipeline.
