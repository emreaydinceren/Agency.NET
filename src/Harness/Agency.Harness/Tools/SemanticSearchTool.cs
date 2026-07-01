using System.Text.Json;
using Agency.RagFormatter;
using Agency.VectorStore.Common;
using Agency.Harness;

namespace Agency.Harness.Tools;

/// <summary>
/// <see cref="ITool"/> that performs a semantic-similarity search over ingested documents, scoped to
/// the global store, the current session, and every project currently loaded into scope.
/// </summary>
/// <param name="vectorStore">The vector store searched for matching documents.</param>
/// <param name="sessionState">Supplies the user id, session id, and loaded project ids used to scope the search.</param>
/// <param name="topK">The maximum number of hits to return. Defaults to 5.</param>
public sealed class SemanticSearchTool(IVectorStore vectorStore, IProjectSessionState sessionState, int topK = 5) : ITool
{
    static JsonElement InputSchema => JsonDocument.Parse(@"{
        ""type"": ""object"",
        ""properties"": {
            ""search_text"": { ""type"": ""string"" }
        },
        ""required"": [""search_text""]
    }").RootElement;

    /// <summary>Gets the <c>semantic_search</c> definition: JSON schema accepting a single required <c>search_text</c> string.</summary>
    public ToolDefinition Definition =>
        new ToolDefinition(
            "semantic_search",
            "Searches ingested documents using semantic similarity. Searches across all accessible scopes: global, current session, and all loaded projects.",
            InputSchema);

    /// <summary>
    /// Runs a semantic search for <c>search_text</c> across the global store, current session, and all
    /// projects loaded for the current user, returning up to <c>topK</c> hits as a Markdown table.
    /// </summary>
    /// <param name="input">JSON object expected to contain a <c>search_text</c> string field.</param>
    /// <param name="ct">Token used to cancel the underlying vector-store search.</param>
    /// <returns>
    /// A <see cref="ToolResult"/> containing a Markdown table of hits, a "no matches" message when
    /// none are found, or an error when <c>search_text</c> is missing.
    /// </returns>
    public async Task<ToolResult> InvokeAsync(JsonElement input, CancellationToken ct)
    {
        dynamic accessor = new JsonDynamicAccessor(input);
        string? searchText = accessor.search_text;
        if (string.IsNullOrEmpty(searchText))
        {
            return new ToolResult("search_text is required.", IsError: true);
        }

        var query = new Query(
            UserId: sessionState.UserId,
            SessionId: sessionState.SessionId,
            Key: null,
            Value: searchText,
            Limit: topK,
            ProjectIds: sessionState.LoadedProjects);

        IReadOnlyList<SearchHit<string>> hits = await vectorStore.SearchAsync<string>(query, ct);

        if (hits.Count == 0)
        {
            return new ToolResult("No matching documents found.");
        }

        string table = hits.ToDataset().ToMarkdownTable();
        return new ToolResult(table);
    }
}
