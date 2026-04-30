using System.Text.Json;
using Agency.Llm.Common.Tools;

namespace Agency.GraphRAG.Code.Agentic;

/// <summary>
/// Exposes indexed code search as an Agentic tool.
/// </summary>
public sealed class CodeIndexAgentTool(ICodeIndex codeIndex) : ITool
{
    private static readonly JsonElement _inputSchema = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "question": {
              "type": "string",
              "description": "The code question to answer from the graph index."
            },
            "topK": {
              "type": "integer",
              "description": "Maximum number of results to retrieve."
            }
          },
          "required": ["question"]
        }
        """).RootElement;

    /// <inheritdoc />
    public ToolDefinition Definition { get; } = new(
        "code_index_query",
        "Answers questions using the indexed code graph.",
        _inputSchema);

    /// <inheritdoc />
    public async Task<ToolResult> InvokeAsync(JsonElement input, CancellationToken ct)
    {
        string? question = input.TryGetProperty("question", out JsonElement questionElement)
            ? questionElement.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(question))
        {
            return new ToolResult("Question is required.", IsError: true);
        }

        int topK = input.TryGetProperty("topK", out JsonElement topKElement) && topKElement.TryGetInt32(out int parsedTopK)
            ? parsedTopK
            : 5;

        string answer = await codeIndex.AskAsync(question, topK, ct).ConfigureAwait(false);
        return new ToolResult(answer);
    }
}
