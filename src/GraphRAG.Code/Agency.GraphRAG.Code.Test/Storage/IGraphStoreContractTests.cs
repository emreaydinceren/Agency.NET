using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Storage;

namespace Agency.GraphRAG.Code.Test.Storage;

/// <summary>
/// Defines the shared operation-surface contract that every <see cref="IGraphStore"/> implementation must satisfy.
/// </summary>
public abstract class IGraphStoreContractTests
{
    /// <summary>
    /// Creates the graph store instance under test.
    /// </summary>
    /// <returns>A concrete <see cref="IGraphStore"/> implementation.</returns>
    protected abstract IGraphStore CreateGraphStore();

    [Fact]
    public void GraphStore_ExposesSchemaAndLifecycleOperations()
    {
        IGraphStore store = this.CreateGraphStore();

        Assert.IsAssignableFrom<IGraphStore>(store);
        AssertMethod(nameof(IGraphStore.InitializeSchemaAsync), typeof(Task), typeof(CancellationToken));
    }

    [Fact]
    public void GraphStore_ExposesNodeUpsertOperations()
    {
        IGraphStore store = this.CreateGraphStore();

        Assert.IsAssignableFrom<IGraphStore>(store);
        AssertMethod(nameof(IGraphStore.UpsertRepoAsync), typeof(Task), typeof(Repo), typeof(CancellationToken));
        AssertMethod(nameof(IGraphStore.UpsertProjectAsync), typeof(Task), typeof(Project), typeof(CancellationToken));
        AssertMethod(
            nameof(IGraphStore.UpsertExternalPackageBatchAsync),
            typeof(Task),
            typeof(IReadOnlyList<ExternalPackage>),
            typeof(CancellationToken));
        AssertMethod(nameof(IGraphStore.UpsertFileAsync), typeof(Task), typeof(SourceFile), typeof(CancellationToken));
        AssertMethod(nameof(IGraphStore.UpsertModuleAsync), typeof(Task), typeof(Agency.GraphRAG.Code.Domain.Module), typeof(CancellationToken));
        AssertMethod(nameof(IGraphStore.UpsertSymbolAsync), typeof(Task), typeof(Symbol), typeof(CancellationToken));
        AssertMethod(
            nameof(IGraphStore.UpsertSymbolBatchAsync),
            typeof(Task),
            typeof(IReadOnlyList<Symbol>),
            typeof(CancellationToken));
    }

    [Fact]
    public void GraphStore_ExposesEdgeAndFileMutationOperations()
    {
        IGraphStore store = this.CreateGraphStore();

        Assert.IsAssignableFrom<IGraphStore>(store);
        AssertMethod(nameof(IGraphStore.UpsertEdgeBatchAsync), typeof(Task), typeof(IReadOnlyList<Edge>), typeof(CancellationToken));
        AssertMethod(nameof(IGraphStore.DeleteSymbolsByFileAsync), typeof(Task), typeof(Guid), typeof(CancellationToken));
        AssertMethod(nameof(IGraphStore.DeleteFileAsync), typeof(Task), typeof(Guid), typeof(CancellationToken));
        AssertMethod(nameof(IGraphStore.RenameFileAsync), typeof(Task), typeof(Guid), typeof(string), typeof(CancellationToken));
    }

    [Fact]
    public void GraphStore_ExposesRepositoryCheckpointOperations()
    {
        IGraphStore store = this.CreateGraphStore();

        Assert.IsAssignableFrom<IGraphStore>(store);
        AssertMethod(nameof(IGraphStore.LoadIndexedCommitAsync), typeof(Task<string>), typeof(Guid), typeof(CancellationToken));
        AssertMethod(nameof(IGraphStore.SetIndexedCommitAsync), typeof(Task), typeof(Guid), typeof(string), typeof(CancellationToken));
    }

    [Fact]
    public void GraphStore_ExposesRetrievalOperations()
    {
        IGraphStore store = this.CreateGraphStore();

        Assert.IsAssignableFrom<IGraphStore>(store);
        AssertMethod(
            nameof(IGraphStore.VectorSearchSymbolsAsync),
            typeof(Task<IReadOnlyList<VectorSearchResult>>),
            typeof(string),
            typeof(int),
            typeof(CancellationToken));
        AssertMethod(
            nameof(IGraphStore.VectorSearchClustersAsync),
            typeof(Task<IReadOnlyList<VectorSearchResult>>),
            typeof(string),
            typeof(int),
            typeof(CancellationToken));
        AssertMethod(
            nameof(IGraphStore.TraverseFromAsync),
            typeof(Task<IReadOnlyList<TraversalHop>>),
            typeof(TraversalRequest),
            typeof(CancellationToken));
        AssertMethod(nameof(IGraphStore.GetSymbolByIdAsync), typeof(Task<Symbol>), typeof(Guid), typeof(CancellationToken));
        AssertMethod(
            nameof(IGraphStore.FindSymbolsByNameAsync),
            typeof(Task<IReadOnlyList<Symbol>>),
            typeof(string),
            typeof(CancellationToken));
    }

    [Fact]
    public void GraphStore_ExposesResolutionAndClusteringOperations()
    {
        IGraphStore store = this.CreateGraphStore();

        Assert.IsAssignableFrom<IGraphStore>(store);
        AssertMethod(
            nameof(IGraphStore.StageUnresolvedCallSiteBatchAsync),
            typeof(Task),
            typeof(IReadOnlyList<UnresolvedCallSite>),
            typeof(CancellationToken));
        AssertMethod(
            nameof(IGraphStore.DrainUnresolvedCallSitesAsync),
            typeof(Task<IReadOnlyList<UnresolvedCallSite>>),
            typeof(Guid?),
            typeof(CancellationToken));
        AssertMethod(
            nameof(IGraphStore.ApplyClusterAssignmentsAsync),
            typeof(Task),
            typeof(IReadOnlyDictionary<Guid, ValueTuple<Guid, string>>),
            typeof(CancellationToken));
        AssertMethod(
            nameof(IGraphStore.ReplaceClusterSummariesAtomicallyAsync),
            typeof(Task),
            typeof(IReadOnlyList<Agency.GraphRAG.Code.Domain.Cluster>),
            typeof(CancellationToken));
    }

    /// <summary>
    /// Asserts that a method exists on <see cref="IGraphStore"/> with the expected signature.
    /// </summary>
    /// <param name="methodName">The interface method name.</param>
    /// <param name="returnType">The expected return type.</param>
    /// <param name="parameterTypes">The expected ordered parameter types.</param>
    private static void AssertMethod(string methodName, Type returnType, params Type[] parameterTypes)
    {
        System.Reflection.MethodInfo? method = typeof(IGraphStore).GetMethod(methodName);

        Assert.NotNull(method);
        Assert.Equal(returnType, method.ReturnType);

        System.Reflection.ParameterInfo[] parameters = method.GetParameters();
        Assert.Equal(parameterTypes.Length, parameters.Length);

        for (int i = 0; i < parameterTypes.Length; i++)
        {
            Assert.Equal(parameterTypes[i], parameters[i].ParameterType);
        }
    }
}
