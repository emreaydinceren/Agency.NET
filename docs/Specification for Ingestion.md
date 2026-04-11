The following specification defines **Agency.Ingestion**, a library designed to bridge the gap between raw data sources and your existing `IKVStore` implementations. It follows the "Load-Transform-Embed-Store" pattern popularized by LangChain but optimized for the .NET 10 TPL (Task Parallel Library) and `IAsyncEnumerable` patterns.

---

## Project: Agency.Ingestion

### 1. Text Chunking 
Logic for partitioning large text into semantic or structural chunks is in https://learn.microsoft.com/en-us/dotnet/api/microsoft.semantickernel.text?view=semantic-kernel-dotnet via the nuget library `Microsoft.SemanticKernel.Core` TextChunker and TokenCounter classes.

Unlike manual character-based splitting, the TextChunker supports structural awareness (e.g., markdown headers, paragraph boundaries).

#### Implementation Details:

Token Counting: Utilizes a TokenCounter delegate (standardized to GPT-4o or cl100k_base by default) to ensure chunks fit within LLM context windows.

Methodology: Wraps TextChunker.SplitPlainTextParagraphs or TextChunker.SplitMarkdownParagraphs based on the document's metadata (e.g., file extension).

```c#
public class SemanticKernelTextSplitter : ITextSplitter
{
    private readonly int _maxTokens;
    private readonly int _overlapTokens;

    public IEnumerable<Document> Split(Document document)
    {
        // Leverage Microsoft.SemanticKernel.Text.TextChunker
        var lines = TextChunker.SplitPlainTextLines(document.Content, _maxTokens);
        var paragraphs = TextChunker.SplitPlainTextParagraphs(lines, _maxTokens, _overlapTokens);

        return paragraphs.Select((text, index) => document with 
        { 
            Content = text,
            Metadata = AddChunkMetadata(document.Metadata, index) 
        });
    }
}
```

| [SplitMarkDownLines(String, Int32, TextChunker+TokenCounter)](microsoft.semantickernel.text.textchunker.splitmarkdownlines#microsoft-semantickernel-text-textchunker-splitmarkdownlines%28system-string-system-int32-microsoft-semantickernel-text-textchunker-tokencounter%29) | Split markdown text into lines. |
| --- | --- |
| [SplitMarkdownParagraphs(IEnumerable&lt;String&gt;, Int32, Int32, String, TextChunker+TokenCounter)](microsoft.semantickernel.text.textchunker.splitmarkdownparagraphs#microsoft-semantickernel-text-textchunker-splitmarkdownparagraphs%28system-collections-generic-ienumerable%28%28system-string%29%29-system-int32-system-int32-system-string-microsoft-semantickernel-text-textchunker-tokencounter%29) | Split markdown text into paragraphs. |
| [SplitPlainTextLines(String, Int32, TextChunker+TokenCounter)](microsoft.semantickernel.text.textchunker.splitplaintextlines#microsoft-semantickernel-text-textchunker-splitplaintextlines%28system-string-system-int32-microsoft-semantickernel-text-textchunker-tokencounter%29) | Split plain text into lines. |
| [SplitPlainTextParagraphs(IEnumerable&lt;String&gt;, Int32, Int32, String, TextChunker+TokenCounter)](microsoft.semantickernel.text.textchunker.splitplaintextparagraphs#microsoft-semantickernel-text-textchunker-splitplaintextparagraphs%28system-collections-generic-ienumerable%28%28system-string%29%29-system-int32-system-int32-system-string-microsoft-semantickernel-text-textchunker-tokencounter%29) | Split plain text into paragraphs. |

### 2. Core Abstractions

To maintain the decoupled architecture of the Agency solution, three primary interfaces are required:

| Interface | Responsibility |
| :--- | :--- |
| `IDocumentLoader` | Streams raw content from sources (File, Blob, Web) into `Document` objects. |
| `ITextSplitter` | A wrapper around TextChunker to provide injectable, configurable chunking strategies. |
| `IIngestionPipeline` | The orchestrator that chains loading, splitting, embedding, and storage. |

### 3. Data Models
A common `Document` record is needed to carry both content and lineage (metadata).

```csharp
public record Document(
    string Content, 
    string SourceId, 
    Dictionary<string, object>? Metadata = null);
```

---

### 4. Interface Specifications

#### `IDocumentLoader`
Standardizes how documents are pulled into the pipeline.
* **Method**: `IAsyncEnumerable<Document> LoadAsync(CancellationToken ct = default);`
* **Proposed Implementations**: 
    * `FileLoader`: Takes a file path or glob pattern.
    * `DirectoryLoader`: Recursively crawls directories for `.txt`, `.md`, or `.pdf`.

#### `IIngestionPipeline`
The glue component. It should be generic to support the `TValue` expected by your `IKVStore`.

```csharp
public interface IIngestionPipeline<TValue>
{
    Task ExecuteAsync(
        IDocumentLoader loader,
        ITextSplitter splitter,
        IKVStore store, 
        CancellationToken ct = default);
}
```

---

### 5. Integration with IKVStore

The pipeline remains responsible for converting Document chunks into the TValue format required by IKVStore.UpsertAsync.

#### Data Flow:

1. **Source Acquisition**: IDocumentLoader yields Document objects containing raw file text and initial metadata (e.g., filepath).
2. **Semantic Chunking**: SemanticKernelTextSplitter processes the text into overlapping segments using the TextChunker logic.
3. **Persistence**: The pipeline iterates through chunks and calls IKVStore.UpsertAsync<string>(key, chunk.Content, chunk.Metadata).

### 6. Implementation Strategy: `DefaultIngestionPipeline`

The pipeline must handle the orchestration of your existing `IEmbeddingGenerator` (from `Agency.Embeddings.Common`) and `IKVStore` (from `Agency.VectorStore.Common`).

#### Execution Flow
1.  **Load**: Invoke `loader.LoadAsync()` to get a stream of raw documents.
2.  **Split**: For each document, apply the `splitter` to generate chunks.
3.  **Metadata Enrichment**: Automatically inject metadata like `source_file`, `chunk_index`, and `ingested_at`.
4.  **Store**: Call `store.UpsertAsync<TValue>(key, value, metadata)`.
    * *Note*: Since your `IKVStore` implementations (like PostgreSQL/pgvector) likely handle embedding internally via a decorator or within the `Upsert` logic, the pipeline should focus on providing the `TValue` (the text chunk).
---

### 7. Technical Requirements & Observability

Consistent with your current `Agency.Llm.Claude` and `Agency.Llm.OpenAI` implementations, the ingestion library must include:

* **Extensibility**: The ITextSplitter implementation must allow for a custom TokenCounter delegate to support different LLM providers (Anthropic vs. OpenAI).
* **OpenTelemetry**: 
    * `Counter`: `documents_ingested_total`
    * `Histogram`: `ingestion_duration_seconds`
    * `ActivitySource`: To trace the lifecycle from "File Read" to "Vector Store Write."
* **Concurrency**: Use `Parallel.ForEachAsync` when processing multiple files to maximize throughput, especially for IO-bound loading.
* **Error Handling**: Implement a `PartialSuccess` result if one document in a batch fails to ingest.
* **Metadata Integrity**: Ensure that SearchHit<TValue> results correctly reflect the UpdatedOn timestamp and original source IDs after chunking.

### 8. Updated Project Structure

- **`Agency.Ingestion`**: Contains IDocumentLoader, Document record, and ITextSplitter.
- **`Agency.Ingestion.SemanticKernel`**: Concrete implementation of ITextSplitter utilizing the Microsoft.SemanticKernel.Text namespace.
- **`Agency.Ingestion.Test`**: Integration tests verifying that TextChunker output remains compatible with the pgvector distance functions in Agency.VectorStore.Sql.Postgre.

### 9. Example Usage

```csharp
var loader = new DirectoryLoader("./docs", "*.md");
var splitter = new RecursiveCharacterTextSplitter(chunkSize: 1000, chunkOverlap: 200);
var pipeline = new IngestionPipeline<string>(logger, meter);

// Ingests documents into your existing Postgre or Sqlite store
await pipeline.ExecuteAsync(loader, splitter, pgVectorStore);
```

### 10. Proposed Project Structure
* `Agency.Ingestion`: Interfaces and base logic.
* `Agency.Ingestion.FileSystem`: Implementations for local file loading.
* `Agency.Ingestion.Test`: Functional tests using `TimeProvider` for `UpdatedOn` validation in `SearchHit`.

