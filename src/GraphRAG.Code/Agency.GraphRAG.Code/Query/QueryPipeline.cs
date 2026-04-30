using Microsoft.Extensions.AI;

namespace Agency.GraphRAG.Code.Query;

/// <summary>
/// Orchestrates planning, retrieval, context assembly, and final answer synthesis.
/// </summary>
public sealed class QueryPipeline(
    QueryPlanner planner,
    HybridRetriever retriever,
    ContextAssembler assembler,
    IChatClient chatClient,
    QueryOptions options)
{
    /// <summary>
    /// Executes a user query against the indexed code graph.
    /// </summary>
    public async Task<QueryResponse> ExecuteAsync(string query, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        QueryPlan plan = await planner.PlanAsync(query, cancellationToken).ConfigureAwait(false);
        QueryRetrievalResult retrieval = await retriever.RetrieveAsync(plan, cancellationToken).ConfigureAwait(false);
        QueryContextAssembly context = assembler.Assemble(plan, retrieval, options.ContextTokenBudget);

        string prompt =
            $"""
            Question:
            {query.Trim()}

            Context:
            {context.ContextText}
            """;

        ChatResponse response = await chatClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, prompt)],
            new ChatOptions
            {
                ModelId = options.AnswerModel,
                Instructions = BuildInstructions(retrieval),
            },
            cancellationToken).ConfigureAwait(false);

        string answer = string.Concat(
            response.Messages
                .SelectMany(static message => message.Contents.OfType<TextContent>())
                .Select(static content => content.Text))
            .Trim();

        return new QueryResponse
        {
            Answer = answer,
            Plan = plan,
            Context = context,
        };
    }

    private static string BuildInstructions(QueryRetrievalResult retrieval)
    {
        string instructions =
            "You answer questions about a codebase using context from a fuzzy index. Be precise, cite uncertainty, and do not overstate unsupported conclusions.";
        return retrieval.HasLowConfidenceReferences
            ? $"{instructions} Some retrieved references are low confidence; explicitly flag uncertainty when relying on them."
            : instructions;
    }
}
