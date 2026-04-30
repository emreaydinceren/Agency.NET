using System.Text.Json;
using Agency.Agentic.Tools;
using Agency.GraphRAG.Code.Agentic;

namespace Agency.GraphRAG.Code.Test.Agentic;

/// <summary>
/// Verifies the Agentic tool adapter for the code index.
/// </summary>
public sealed class CodeIndexAgentToolTests
{
    [Fact]
    public void Definition_HasExpectedShape()
    {
        CodeIndexAgentTool tool = new(new FakeCodeIndex());

        Assert.Equal("code_index_query", tool.Definition.Name);
        Assert.Contains("indexed code graph", tool.Definition.Description, StringComparison.OrdinalIgnoreCase);
        Assert.True(tool.Definition.InputSchema.TryGetProperty("required", out JsonElement required));
        Assert.Contains(required.EnumerateArray().Select(static value => value.GetString()), static value => value == "question");
    }

    [Fact]
    public async Task ToolRegistry_RegistersAndForwardsToCodeIndex()
    {
        FakeCodeIndex codeIndex = new();
        ToolRegistry registry = new([new CodeIndexAgentTool(codeIndex)]);
        using JsonDocument payload = JsonDocument.Parse("""{ "question": "find Agent", "topK": 4 }""");

        var result = await registry.InvokeAsync("code_index_query", payload.RootElement, TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        Assert.Equal("find Agent", codeIndex.LastQuestion);
        Assert.Equal(4, codeIndex.LastTopK);
        Assert.Equal("answer:find Agent:4", result.Content);
    }

    private sealed class FakeCodeIndex : ICodeIndex
    {
        public string? LastQuestion { get; private set; }

        public int LastTopK { get; private set; }

        public Task<string> AskAsync(string question, int topK = 5, CancellationToken cancellationToken = default)
        {
            LastQuestion = question;
            LastTopK = topK;
            return Task.FromResult($"answer:{question}:{topK}");
        }
    }
}
