# Agency.Console

#console #stub #demo

## What It Is

`Agency.Console` is the original (non-agentic) console application stub for the Agency solution. It currently contains a minimal entry point and serves as a placeholder for a RAG demo harness that wires together the full pipeline: embed a query → run a vector similarity search → format results → send to an LLM.

## Current State

The project currently only prints `Hello, World!` — it is a scaffold waiting to be built into a full RAG demonstration.

## Intended Purpose

The planned usage pattern is:

```csharp
// 1. Generate embedding for the user's question
var embedding = await embeddingGenerator.GenerateEmbeddingAsync("What is pgvector?");

// 2. Query the vector store
var hits = await kvStore.SearchAsync<Doc>(new Query(Value: "What is pgvector?", Limit: 5));

// 3. Format results as Markdown context
Dataset dataset = hits.ToDataset();
string context = dataset.ToMarkdownTable();

// 4. Send to LLM with the context
string answer = await llmClient.SendAsync(model, $"""
    Answer the following question using only the provided context.
    Context:
    {context}
    Question: What is pgvector?
    """);

Console.WriteLine(answer);
```

## How It Differs from `Agency.Agentic.Console`

| | `Agency.Console` | [[Agency.Agentic.Console]] |
|---|---|---|
| Purpose | One-shot RAG demo | Multi-turn agentic REPL chat |
| Loop | None (single request) | Full agent loop with tools |
| State | Stateless | Persistent `Context` across turns |
| Status | Stub | Fully implemented |

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.Embeddings.OpenAI]] | Would generate query embeddings |
| [[Agency.VectorStore.Sql.Postgre]] | Would retrieve relevant documents |
| [[Agency.RagFormatter]] | Would format retrieved `Dataset` as Markdown |
| [[Agency.Llm.OpenAI]] | Would send the RAG prompt to the LLM |
| [[Agency.Agentic.Console]] | The more complete agentic successor |
