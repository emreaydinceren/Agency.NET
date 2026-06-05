# Agency.Ingestion.SemanticKernel
#ingestion #semantickernel #chunking #textsplitter

## What It Is

`Agency.Ingestion.SemanticKernel` is the `ITextSplitter` implementation that splits documents into overlapping token-bounded chunks using Microsoft Semantic Kernel's `TextChunker`, with automatic Markdown-aware splitting when the document carries a `.md` or `.markdown` file extension in its metadata.

Namespace: `Agency.Ingestion.SemanticKernel`

## API Surface

```csharp
using Agency.Ingestion;
using Microsoft.SemanticKernel.Text;

namespace Agency.Ingestion.SemanticKernel;
```

| Type | Member | Signature |
|---|---|---|
| `SemanticKernelTextSplitter` | Constructor | `SemanticKernelTextSplitter(int maxTokens, int overlapTokens, TextChunker.TokenCounter? tokenCounter = null)` |
| `SemanticKernelTextSplitter` | Method | `IEnumerable<Document> Split(Document document)` |

## Dependencies

- `Agency.Ingestion` (ProjectReference)
- `Microsoft.SemanticKernel.Core` (PackageReference)

## Related

- [[Agency.Ingestion]]
- [[Agency.Ingestion.FileSystem]]
