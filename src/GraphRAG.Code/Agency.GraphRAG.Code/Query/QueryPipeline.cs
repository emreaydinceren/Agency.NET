using Microsoft.Extensions.AI;
using System.Diagnostics;

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

        var classifyStopwatch = Stopwatch.StartNew();
        QueryPlan plan = await planner.PlanAsync(query, cancellationToken).ConfigureAwait(false);
        classifyStopwatch.Stop();

        var retrieveStopwatch = Stopwatch.StartNew();
        QueryRetrievalResult retrieval = await retriever.RetrieveAsync(plan, cancellationToken).ConfigureAwait(false);
        retrieveStopwatch.Stop();

        var assembleStopwatch = Stopwatch.StartNew();
        QueryContextAssembly context = assembler.Assemble(plan, retrieval, options.ContextTokenBudget);
        assembleStopwatch.Stop();

        string prompt =
            $"""
            Question:
            {query.Trim()}

            Context:
            {context.ContextText}
            """;

        var answerStopwatch = Stopwatch.StartNew();
        ChatResponse response = await chatClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, prompt)],
            new ChatOptions
            {
                ModelId = options.AnswerModel,
                Instructions = BuildInstructions(retrieval),
            },
            cancellationToken).ConfigureAwait(false);
        answerStopwatch.Stop();

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
            Retrieval = retrieval,
            ClassifyDuration = classifyStopwatch.Elapsed,
            RetrieveDuration = retrieveStopwatch.Elapsed,
            AssembleDuration = assembleStopwatch.Elapsed,
            AnswerDuration = answerStopwatch.Elapsed,
            InputTokenCount = response.Usage?.InputTokenCount,
            OutputTokenCount = response.Usage?.OutputTokenCount,
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
