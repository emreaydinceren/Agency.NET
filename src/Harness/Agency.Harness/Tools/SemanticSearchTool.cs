using System.Text.Json;
using Agency.RagFormatter;
using Agency.VectorStore.Common;
using Agency.Harness;

namespace Agency.Harness.Tools;

public class SemanticSearchTool(IVectorStore vectorStore, IProjectSessionState sessionState, int topK = 5) : ITool
{
    static JsonElement InputSchema => JsonDocument.Parse(@"{
        ""type"": ""object"",
        ""properties"": {
            ""search_text"": { ""type"": ""string"" }
        },
        ""required"": [""search_text""]
    }").RootElement;

    public ToolDefinition Definition =>
        new ToolDefinition(
            "semantic_search",
            "Searches ingested documents using semantic similarity. Searches across all accessible scopes: global, current session, and all loaded projects.",
            InputSchema);

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
