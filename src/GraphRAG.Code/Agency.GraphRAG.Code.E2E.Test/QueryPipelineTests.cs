using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Query;

namespace Agency.GraphRAG.Code.E2E.Test;

/// <summary>
/// End-to-end query-pipeline tests against a SQLite index of the Agency repository.
/// </summary>
[Trait("Category", "Functional")]
public sealed class QueryPipelineTests : IAsyncLifetime
{
    private SqliteHarness? _harness;
    private QueryPipeline? _pipeline;

    public async ValueTask InitializeAsync()
    {
        this._harness = E2ETestInfrastructure.CreateSqliteHarness();
        Repo repo = new()
        {
            Id = Guid.Parse("36578ad2-16b4-4446-ac4f-7e0415f53a21"),
            LocalPath = E2ETestInfrastructure.RepoRoot,
            IsShallow = false,
            IndexedCommit = null,
            IndexedAt = null,
            RemoteUrl = null,
        };

        AgencyRepoIndexer indexer = new(this._harness.Store, this._harness.EmbeddingGenerator);
        IndexArtifacts artifacts = await indexer.IndexAsync(repo, TestContext.Current.CancellationToken);
        repo = repo with { IndexedCommit = artifacts.IndexedCommit };

        Dictionary<Guid, string> symbolTexts = artifacts.Files
            .SelectMany(static file => file.Symbols)
            .GroupBy(static symbol => symbol.Symbol.Id)
            .ToDictionary(static group => group.Key, static group => group.First().Chunk.Content);
        this._pipeline = E2ETestInfrastructure.CreateQueryPipeline(
            this._harness.Store,
            new MockChatClient(),
            new SymbolTextProvider(symbolTexts),
            new InMemoryClusterSource(artifacts.Clusters));
    }

    public async ValueTask DisposeAsync()
    {
        if (this._harness is not null)
        {
            await this._harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task ChatSubsystemQuestion_ClassifiesAsSubsystem_AndMentionsCoreSymbols()
    {
        QueryResponse response = await this._pipeline!.ExecuteAsync("How does chat with agent work?", TestContext.Current.CancellationToken);
        Assert.Equal(QueryCategory.Subsystem, response.Plan.Category);
        int mentioned = AgencyRepoExpectations.ChatAgentSymbols.Count(symbol => response.Answer.Contains(symbol, StringComparison.OrdinalIgnoreCase));
        Assert.True(mentioned >= 3);
    }

    [Fact]
    public async Task ClaudeDependencyQuestion_ClassifiesAsDependency_AndMentionsAnthropic()
    {
        QueryResponse response = await this._pipeline!.ExecuteAsync("What does Agency.Llm.Claude depend on?", TestContext.Current.CancellationToken);
        Assert.Equal(QueryCategory.Dependency, response.Plan.Category);
        Assert.Contains(AgencyRepoExpectations.ClaudeDependencyPackage, response.Answer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConversationManagerImpactQuestion_ClassifiesAsImpact_AndMentionsConsumers()
    {
        QueryResponse response = await this._pipeline!.ExecuteAsync("What calls IConversationManager?", TestContext.Current.CancellationToken);
        Assert.Equal(QueryCategory.Impact, response.Plan.Category);
        Assert.Contains("InMemoryConversationManager", response.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(AgencyRepoExpectations.ConversationManagerConsumer, response.Answer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CodebaseTourQuestion_ClassifiesAsGlobal_AndKeepsInfrastructureInFooter()
    {
        QueryResponse response = await this._pipeline!.ExecuteAsync("Give me a tour of the codebase.", TestContext.Current.CancellationToken);
        Assert.Equal(QueryCategory.Global, response.Plan.Category);
        Assert.StartsWith("business:", response.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Infrastructure footer:", response.Answer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ChatSessionQuestion_ClassifiesAsLocal_AndMentionsSendAsyncBehavior()
    {
        QueryResponse response = await this._pipeline!.ExecuteAsync("What does ChatSession.SendAsync do?", TestContext.Current.CancellationToken);
        Assert.Equal(QueryCategory.Local, response.Plan.Category);
        Assert.Contains("ChatSession.SendAsync", response.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Agent.ChatAsync", response.Answer, StringComparison.OrdinalIgnoreCase);
    }
}
