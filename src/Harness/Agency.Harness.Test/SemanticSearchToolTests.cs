using System.Text.Json;
using Agency.Harness;
using Agency.VectorStore.Common;
using Moq;

namespace Agency.Harness.Test;

public sealed class SemanticSearchToolTests
{
    private readonly Mock<IVectorStore> _storeMock = new();
    private readonly Mock<IProjectSessionState> _stateMock = new();

    public SemanticSearchToolTests()
    {
        _stateMock.Setup(s => s.UserId).Returns("user1");
        _stateMock.Setup(s => s.SessionId).Returns("sess-abc");
        _stateMock.Setup(s => s.LoadedProjects).Returns(Array.Empty<string>());
    }

    private SemanticSearchTool CreateTool(int topK = 5) =>
        new(_storeMock.Object, _stateMock.Object, topK);

    private static JsonElement BuildInput(string searchText) =>
        JsonDocument.Parse("{\"search_text\": \"" + searchText + "\"}").RootElement;

    private void SetupSearchReturns(params SearchHit<string>[] hits)
    {
        _storeMock
            .Setup(s => s.SearchAsync<string>(It.IsAny<Query>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<SearchHit<string>>)hits);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyResults_ReturnsNoDocumentsMessage()
    {
        SetupSearchReturns();
        SemanticSearchTool tool = CreateTool();

        ToolResult result = await tool.InvokeAsync(BuildInput("hello"), CancellationToken.None);

        Assert.Equal("No matching documents found.", result.Content);
    }

    [Fact]
    public async Task ExecuteAsync_WithResults_ReturnsMarkdownTable()
    {
        SearchHit<string> hit = new("user1", "sess-abc", "doc-1", "some content about the project", null, 0.1, DateTimeOffset.UtcNow);
        SetupSearchReturns(hit);
        SemanticSearchTool tool = CreateTool();

        ToolResult result = await tool.InvokeAsync(BuildInput("hello"), CancellationToken.None);

        Assert.NotEmpty(result.Content);
        Assert.NotEqual("No matching documents found.", result.Content);
    }

    [Fact]
    public async Task ExecuteAsync_BuildsQueryWithCorrectUserId()
    {
        Query? capturedQuery = null;
        _storeMock
            .Setup(s => s.SearchAsync<string>(It.IsAny<Query>(), It.IsAny<CancellationToken>()))
            .Callback<Query, CancellationToken>((q, _) => capturedQuery = q)
            .ReturnsAsync(Array.Empty<SearchHit<string>>());
        SemanticSearchTool tool = CreateTool();

        await tool.InvokeAsync(BuildInput("test"), CancellationToken.None);

        Assert.NotNull(capturedQuery);
        Assert.Equal("user1", capturedQuery.UserId);
    }

    [Fact]
    public async Task ExecuteAsync_BuildsQueryWithCorrectSessionId()
    {
        Query? capturedQuery = null;
        _storeMock
            .Setup(s => s.SearchAsync<string>(It.IsAny<Query>(), It.IsAny<CancellationToken>()))
            .Callback<Query, CancellationToken>((q, _) => capturedQuery = q)
            .ReturnsAsync(Array.Empty<SearchHit<string>>());
        SemanticSearchTool tool = CreateTool();

        await tool.InvokeAsync(BuildInput("test"), CancellationToken.None);

        Assert.NotNull(capturedQuery);
        Assert.Equal("sess-abc", capturedQuery.SessionId);
    }

    [Fact]
    public async Task ExecuteAsync_BuildsQueryWithLoadedProjects()
    {
        List<string> projects = new() { "projA", "projB" };
        _stateMock.Setup(s => s.LoadedProjects).Returns(projects);
        Query? capturedQuery = null;
        _storeMock
            .Setup(s => s.SearchAsync<string>(It.IsAny<Query>(), It.IsAny<CancellationToken>()))
            .Callback<Query, CancellationToken>((q, _) => capturedQuery = q)
            .ReturnsAsync(Array.Empty<SearchHit<string>>());
        SemanticSearchTool tool = CreateTool();

        await tool.InvokeAsync(BuildInput("test"), CancellationToken.None);

        Assert.NotNull(capturedQuery);
        Assert.NotNull(capturedQuery.ProjectIds);
        Assert.Contains("projA", capturedQuery.ProjectIds);
        Assert.Contains("projB", capturedQuery.ProjectIds);
    }

    [Fact]
    public async Task ExecuteAsync_RespectsTopK()
    {
        Query? capturedQuery = null;
        _storeMock
            .Setup(s => s.SearchAsync<string>(It.IsAny<Query>(), It.IsAny<CancellationToken>()))
            .Callback<Query, CancellationToken>((q, _) => capturedQuery = q)
            .ReturnsAsync(Array.Empty<SearchHit<string>>());
        SemanticSearchTool tool = CreateTool(topK: 3);

        await tool.InvokeAsync(BuildInput("test"), CancellationToken.None);

        Assert.NotNull(capturedQuery);
        Assert.Equal(3, capturedQuery.Limit);
    }
}
