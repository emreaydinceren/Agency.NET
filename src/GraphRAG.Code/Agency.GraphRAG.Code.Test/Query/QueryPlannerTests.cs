using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Query;
using Agency.GraphRAG.Code.Storage;
using Microsoft.Extensions.AI;

namespace Agency.GraphRAG.Code.Test.Query;

/// <summary>
/// Tests for <see cref="QueryPlanner"/>.
/// </summary>
public sealed class QueryPlannerTests
{
    [Theory]
    [InlineData(QueryCategory.Local, true, false, true, TraversalDirection.Outgoing)]
    [InlineData(QueryCategory.Subsystem, true, true, true, TraversalDirection.Outgoing)]
    [InlineData(QueryCategory.Global, false, true, false, TraversalDirection.Outgoing)]
    [InlineData(QueryCategory.Impact, false, false, true, TraversalDirection.Incoming)]
    [InlineData(QueryCategory.Dependency, false, false, true, TraversalDirection.Outgoing)]
    public async Task PlanAsync_RoutesEachCategoryToExpectedStrategy(
        QueryCategory category,
        bool useSymbolVectorSearch,
        bool useClusterVectorSearch,
        bool useTraversal,
        TraversalDirection direction)
    {
        QueryPlanner planner = new(new QueryClassifier(new ConstantChatClient(category.ToString()), new QueryOptions()));

        QueryPlan plan = await planner.PlanAsync("How does auth work?", TestContext.Current.CancellationToken);

        Assert.Equal(category, plan.Category);
        Assert.Equal(useSymbolVectorSearch, plan.UseSymbolVectorSearch);
        Assert.Equal(useClusterVectorSearch, plan.UseClusterVectorSearch);
        Assert.Equal(useTraversal, plan.UseTraversal);
        Assert.Equal(direction, plan.TraversalDirection);
    }

    [Fact]
    public async Task PlanAsync_GlobalQuery_PrefersBusinessClusters_ByDefault()
    {
        QueryPlanner planner = new(new QueryClassifier(new ConstantChatClient("Global"), new QueryOptions()));

        QueryPlan plan = await planner.PlanAsync("What does this codebase do?", TestContext.Current.CancellationToken);

        Assert.Equal([ClusterType.Business], plan.PreferredClusterTypes);
        Assert.True(plan.IncludeMixedClusters);
        Assert.True(plan.AggregateInfrastructureClusters);
    }

    private sealed class ConstantChatClient(string responseText) : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("ConstantChatClient", null, null);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, responseText)]));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? key = null) => null;

        public void Dispose()
        {
        }
    }
}
