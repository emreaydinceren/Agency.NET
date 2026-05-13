using Microsoft.Extensions.AI;

namespace Agency.GraphRAG.Code.Query;

/// <summary>
/// Uses an LLM to classify a user query into one of the supported retrieval categories.
/// </summary>
public sealed class QueryClassifier
{

    // Defaults selected empirically via QueryClassifierEvalTests.ClassifyAsync_PromptVariantSweep_*
    // against qwen/qwen3-coder-next: the intent-tiebreaker rule lifts overall accuracy to 100.0%
    // (±0.0 pp σ across 3 runs), a +9.5 pp gain over the prior per-category-definitions prompt and
    // a +40 pp gain on Local recall. The tiebreaker fixes queries that mention a category word in
    // their text (e.g. "Impact and Dependency queries") which previously anchored the model to the
    // wrong bucket. See that test for the full comparison across 8 prompt variants.
    private readonly string instructions =
        """
        Classify the user's codebase question into exactly one category and return only the category name.

        Categories:
        - Local: a question about the concrete behavior of a specific named symbol, method, or class.
        - Subsystem: a question about a slice of the codebase that spans several types (e.g. "the X pipeline", "the X subsystem").
        - Global: a repo-wide architectural question (overview, major subsystems, top-level projects).
        - Impact: who calls or depends on a named symbol; what would break if it changed.
        - Dependency: what a named symbol depends on, imports, or references.

        Tiebreaker: if the query mentions a category word (e.g. "impact", "dependency", "subsystem", "global"), classify by the intent of the question, not by the keyword that appears. A question about HOW a value is computed is Local even if Impact or Dependency is mentioned as context.
        """;
    private readonly string queryPrompt = "Query:";

    private readonly IChatClient chatClient;
    private readonly QueryOptions options;

    /// <summary>
    /// Initializes a new instance of the QueryClassifier class with the specified chat client and query options.
    /// </summary>
    /// <param name="chatClient">
    /// The chat client used to interact with the chat service for query classification.
    /// </param>
    /// <param name="options">The options that configure the behavior of the query classification process.</param>
    public QueryClassifier(
        IChatClient chatClient,
        QueryOptions options)
    {
        this.chatClient = chatClient;
        this.options = options;
    }

    /// <summary>
    /// Initializes a new instance of the QueryClassifier class with the specified chat client, query options,
    /// instructions, and query prompt.
    /// </summary>
    /// <param name="chatClient">The chat client used to interact with the chat service for query classification.</param>
    /// <param name="options">The options that configure the behavior of the query classification process.</param>
    /// <param name="instructions">The instructions that guide how the query should be classified.</param>
    /// <param name="queryPrompt">The prompt used to initiate the query classification.</param>
    public QueryClassifier(
        IChatClient chatClient,
        QueryOptions options,
        string @instructions,
        string @queryPrompt) : this (chatClient, options)
    {   
        this.instructions = @instructions;
        this.queryPrompt = @queryPrompt;
    }

    /// <summary>
    /// Classifies the supplied query.
    /// </summary>
    public async Task<QueryCategory> ClassifyAsync(string query, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        string prompt =
            queryPrompt + Environment.NewLine + query.Trim();

        ChatResponse response = await chatClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, prompt)],
            new ChatOptions
            {
                ModelId = options.CheapestModel,
                Instructions = instructions,
            },
            cancellationToken).ConfigureAwait(false);

        string text = string.Concat(
            response.Messages
                .SelectMany(static message => message.Contents.OfType<TextContent>())
                .Select(static content => content.Text))
            .Trim();

        return TryParse(text, out QueryCategory category)
            ? category
            : throw new InvalidOperationException($"Unsupported query category '{text}'.");
    }

    internal static bool TryParse(string value, out QueryCategory category) =>
        Enum.TryParse(value?.Trim(), ignoreCase: true, out category);
}
