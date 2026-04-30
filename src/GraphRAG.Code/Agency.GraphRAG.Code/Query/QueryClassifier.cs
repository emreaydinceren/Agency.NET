using Microsoft.Extensions.AI;

namespace Agency.GraphRAG.Code.Query;

/// <summary>
/// Uses an LLM to classify a user query into one of the supported retrieval categories.
/// </summary>
public sealed class QueryClassifier(
    IChatClient chatClient,
    QueryOptions options)
{
    private const string Instructions =
        "Classify the user's codebase question into exactly one category and return only the category name.";

    /// <summary>
    /// Classifies the supplied query.
    /// </summary>
    public async Task<QueryCategory> ClassifyAsync(string query, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        string prompt =
            """
            Choose exactly one category for this code-index query:
            - Local
            - Subsystem
            - Global
            - Impact
            - Dependency

            Return only the category name.

            Query:
            """ + Environment.NewLine + query.Trim();

        ChatResponse response = await chatClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, prompt)],
            new ChatOptions
            {
                ModelId = options.CheapestModel,
                Instructions = Instructions,
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
