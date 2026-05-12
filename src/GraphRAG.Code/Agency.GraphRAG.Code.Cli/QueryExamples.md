# Query Examples

Examples for the `query` command in `Agency.GraphRAG.Code.Cli`, organized around how the pipeline interprets each question.

## How a query is processed

1. **Classify** — `QueryClassifier` asks the cheapest LLM to label the question as one of five categories: `Local`, `Subsystem`, `Global`, `Impact`, `Dependency`.
2. **Plan** — `QueryPlanner` maps that category to a `QueryPlan` (which retrieval channels to use, how many hops to traverse, which edge kinds to follow, which cluster types to prefer).
3. **Retrieve** — `HybridRetriever` runs some combination of: symbol vector search, name-based seeding, graph traversal, and cluster vector search against `IGraphStore`.
4. **Assemble** — `ContextAssembler` orders results (cluster summaries → symbol summaries → raw code → infrastructure footer → confidence notes), trimming to the `ContextTokenBudget` (default 600 tokens).
5. **Answer** — `QueryPipeline` sends the budgeted context plus the question to the answer model.

Phrase questions so the classifier picks the bucket whose plan matches your intent. The sections below show what each bucket optimizes for, with sample invocations.

## Invocation form

```powershell
dotnet run --project src\GraphRAG.Code\Agency.GraphRAG.Code.Cli -- query "<question>" --store sqlite
```

For PostgreSQL, add `--store postgres --connection "Host=…;Database=…;Username=…;Password=…"`. The `--top-k` flag is accepted but currently not forwarded into the pipeline — vary `SymbolTopK` / `ClusterTopK` in code if you need to.

---

## Local — "what does this specific thing do?"

Use when you want a precise answer about a small number of symbols. The plan does symbol vector search (`SymbolTopK=5`), then a **1-hop outgoing** traversal over `References`, `Defines`, `MemberOf`, `Contains` to pull in immediate collaborators.

Good when the question names a concrete behavior, method, or class:

```powershell
dotnet run --project src\GraphRAG.Code\Agency.GraphRAG.Code.Cli -- query "How does QueryPipeline assemble context?"
dotnet run --project src\GraphRAG.Code\Agency.GraphRAG.Code.Cli -- query "What does HybridRetriever do when SymbolVectorSearch returns no results?"
dotnet run --project src\GraphRAG.Code\Agency.GraphRAG.Code.Cli -- query "How does ContextAssembler enforce the token budget?"
dotnet run --project src\GraphRAG.Code\Agency.GraphRAG.Code.Cli -- query "How is the FocusTerm computed for Impact and Dependency queries?"
dotnet run --project src\GraphRAG.Code\Agency.GraphRAG.Code.Cli -- query "What does SuppressThinkingPipelinePolicy do?"
```

## Subsystem — "explain this slice of the codebase"

Use for mid-scope questions that span several types but not the whole repo. The plan turns on **both** symbol and cluster vector search (`SymbolTopK=6`, `ClusterTopK=3`), then runs a **2-hop outgoing** traversal that also follows `DependsOn` edges. You get a small set of seed symbols, their neighborhood, and the cluster summaries those symbols live in.

Good for "the X subsystem" or "the X pipeline":

```powershell
dotnet run --project src\GraphRAG.Code\Agency.GraphRAG.Code.Cli -- query "Walk me through the query pipeline from CLI input to LLM response"
dotnet run --project src\GraphRAG.Code\Agency.GraphRAG.Code.Cli -- query "Explain how the indexing pipeline turns source files into graph nodes"
dotnet run --project src\GraphRAG.Code\Agency.GraphRAG.Code.Cli -- query "How does the summarizer subsystem produce one-line and full summaries?"
dotnet run --project src\GraphRAG.Code\Agency.GraphRAG.Code.Cli -- query "How does the hydration phase populate Phase1 writes?"
dotnet run --project src\GraphRAG.Code\Agency.GraphRAG.Code.Cli -- query "Describe the scope resolver and how it disambiguates references"
```

## Global — "give me the big picture"

Use for repo-wide architectural questions. The plan **skips per-symbol search entirely** and only retrieves clusters (`ClusterTopK=6`), preferring `Business` clusters and pushing `Infrastructure` clusters into a footer (`AggregateInfrastructureClusters=true`). `Mixed` clusters are still allowed through (`IncludeMixedClusters=true`).

Output is a high-level map, not a code-level answer. Don't expect raw source in the context.

```powershell
dotnet run --project src\GraphRAG.Code\Agency.GraphRAG.Code.Cli -- query "What are the major subsystems in this repository?"
dotnet run --project src\GraphRAG.Code\Agency.GraphRAG.Code.Cli -- query "Give me an architectural overview of the codebase"
dotnet run --project src\GraphRAG.Code\Agency.GraphRAG.Code.Cli -- query "What are the main business domains in this solution?"
dotnet run --project src\GraphRAG.Code\Agency.GraphRAG.Code.Cli -- query "Which clusters are responsible for storage, and which for orchestration?"
dotnet run --project src\GraphRAG.Code\Agency.GraphRAG.Code.Cli -- query "Summarize the responsibilities of each top-level project"
```

## Impact — "what breaks if I change X?"

Use to find **callers/dependents** of a symbol. The plan runs a **2-hop incoming** traversal over `References`, `MemberOf`, `Contains`, seeded by `FocusTerm` (the last token of the question, with trailing punctuation stripped). No vector search is performed.

**Phrasing matters:** end the sentence with the name you want analyzed.

```powershell
dotnet run --project src\GraphRAG.Code\Agency.GraphRAG.Code.Cli -- query "What breaks if I change RepoWalker?"
dotnet run --project src\GraphRAG.Code\Agency.GraphRAG.Code.Cli -- query "Who depends on QueryPipeline?"
dotnet run --project src\GraphRAG.Code\Agency.GraphRAG.Code.Cli -- query "What are the callers of IncrementalHydrator?"
dotnet run --project src\GraphRAG.Code\Agency.GraphRAG.Code.Cli -- query "What would be affected by renaming SymbolSummarizer?"
dotnet run --project src\GraphRAG.Code\Agency.GraphRAG.Code.Cli -- query "Show me the call sites that would break if I removed Phase1Writer"
```

If the retriever sees edges with low confidence or `Unresolved` signals on the traversal, the context picks up a *Confidence notes* section and the answer prompt is upgraded to flag uncertainty.

## Dependency — "what does X depend on?"

Mirror of `Impact`. The plan runs a **2-hop outgoing** traversal over `DependsOn`, `Imports`, `References`, again seeded by the last-token `FocusTerm`.

```powershell
dotnet run --project src\GraphRAG.Code\Agency.GraphRAG.Code.Cli -- query "What does QueryPipeline depend on?"
dotnet run --project src\GraphRAG.Code\Agency.GraphRAG.Code.Cli -- query "Which packages does Agency.GraphRAG.Code.Postgres import?"
dotnet run --project src\GraphRAG.Code\Agency.GraphRAG.Code.Cli -- query "List the modules referenced by IndexingPipeline"
dotnet run --project src\GraphRAG.Code\Agency.GraphRAG.Code.Cli -- query "What does HybridRetriever transitively depend on?"
dotnet run --project src\GraphRAG.Code\Agency.GraphRAG.Code.Cli -- query "What does SqliteGraphStore depend on?"
```

---

## Tips for phrasing

- **Name the symbol explicitly** — vector search and name lookup both work better with the exact identifier than with a paraphrase.
- **Put the target at the end** for `Impact` / `Dependency` queries — `FocusTerm` is literally the last whitespace-separated token after stripping `? . !`.
- **Use architectural verbs** ("overview", "subsystems", "responsibilities") to push the classifier toward `Global`; use concrete verbs ("how does", "what does X do") to push it toward `Local`.
- **Watch for the confidence note** in the answer — it means the traversal walked over unresolved or low-confidence edges and the answer should be treated as a lead rather than ground truth.
