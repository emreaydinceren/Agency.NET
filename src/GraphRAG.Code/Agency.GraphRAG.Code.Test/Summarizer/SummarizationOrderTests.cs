using Agency.GraphRAG.Code.Chunker;
using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Summarizer;
using Agency.GraphRAG.Code.Walker;

namespace Agency.GraphRAG.Code.Test.Summarizer;

/// <summary>
/// Tests for <see cref="SummarizationOrder"/>.
/// </summary>
public sealed class SummarizationOrderTests
{
    [Fact]
    public void Order_InterfaceFirstChain_PlacesInterfacesBeforeImplementations()
    {
        Chunk contract = CreateTypeChunk(
            path: @"src\Contracts.cs",
            line: 0,
            name: "IWorker",
            fullyQualifiedName: "Sample.Contracts.IWorker",
            symbolKind: SymbolKind.Interface);
        Chunk baseClass = CreateTypeChunk(
            path: @"src\Contracts.cs",
            line: 10,
            name: "WorkerBase",
            fullyQualifiedName: "Sample.Contracts.WorkerBase",
            implements: ["IWorker"]);
        Chunk concrete = CreateTypeChunk(
            path: @"src\Contracts.cs",
            line: 20,
            name: "ConcreteWorker",
            fullyQualifiedName: "Sample.Contracts.ConcreteWorker",
            inherits: ["WorkerBase"],
            implements: ["IWorker"]);

        IReadOnlyList<Chunk> ordered = SummarizationOrder.Order([concrete, baseClass, contract]);

        Assert.Equal(
            ["Sample.Contracts.IWorker", "Sample.Contracts.WorkerBase", "Sample.Contracts.ConcreteWorker"],
            ordered.Select(static chunk => chunk.FullyQualifiedName).ToArray());
    }

    [Fact]
    public void Order_MultiLevelInheritance_PlacesBaseTypesBeforeDerivedTypes()
    {
        Chunk root = CreateTypeChunk(
            path: @"src\Models.cs",
            line: 0,
            name: "RootModel",
            fullyQualifiedName: "Sample.Models.RootModel");
        Chunk middle = CreateTypeChunk(
            path: @"src\Models.cs",
            line: 10,
            name: "IntermediateModel",
            fullyQualifiedName: "Sample.Models.IntermediateModel",
            inherits: ["RootModel"]);
        Chunk leaf = CreateTypeChunk(
            path: @"src\Models.cs",
            line: 20,
            name: "LeafModel",
            fullyQualifiedName: "Sample.Models.LeafModel",
            inherits: ["IntermediateModel"]);

        IReadOnlyList<Chunk> ordered = SummarizationOrder.Order([leaf, root, middle]);

        Assert.Equal(
            ["Sample.Models.RootModel", "Sample.Models.IntermediateModel", "Sample.Models.LeafModel"],
            ordered.Select(static chunk => chunk.FullyQualifiedName).ToArray());
    }

    [Fact]
    public void Order_ParentAndDependencyEdges_KeepContainerBeforeMembers()
    {
        Chunk type = CreateTypeChunk(
            path: @"src\Service.cs",
            line: 0,
            name: "ConcreteWorker",
            fullyQualifiedName: "Sample.Service.ConcreteWorker",
            inherits: ["WorkerBase"]);
        Chunk method = ChunkBuilder.Build(
            path: @"src\Service.cs",
            language: Language.CSharp,
            granularity: ChunkGranularity.Member,
            name: "Run",
            fullyQualifiedName: "Sample.Service.ConcreteWorker.Run",
            signature: "void Run()",
            content: "void Run() { }",
            range: new ChunkSourceRange(5, 4, 7, 5),
            symbolKind: SymbolKind.Method,
            importsInScope: [],
            parentId: type.Id);
        Chunk baseType = CreateTypeChunk(
            path: @"src\Base.cs",
            line: 0,
            name: "WorkerBase",
            fullyQualifiedName: "Sample.Service.WorkerBase");

        IReadOnlyList<Chunk> ordered = SummarizationOrder.Order([method, type, baseType]);

        Assert.Equal(
            ["Sample.Service.WorkerBase", "Sample.Service.ConcreteWorker", "Sample.Service.ConcreteWorker.Run"],
            ordered.Select(static chunk => chunk.FullyQualifiedName).ToArray());
    }

    [Fact]
    public void Order_Cycle_FallsBackToFileOrderAndEmitsWarning()
    {
        Chunk first = CreateTypeChunk(
            path: @"src\Cycle.cs",
            line: 0,
            name: "First",
            fullyQualifiedName: "Sample.Cycle.First",
            inherits: ["Second"]);
        Chunk second = CreateTypeChunk(
            path: @"src\Cycle.cs",
            line: 10,
            name: "Second",
            fullyQualifiedName: "Sample.Cycle.Second",
            inherits: ["First"]);
        List<string> warnings = [];

        IReadOnlyList<Chunk> ordered = SummarizationOrder.Order([second, first], warnings.Add);

        Assert.Equal(
            ["Sample.Cycle.First", "Sample.Cycle.Second"],
            ordered.Select(static chunk => chunk.FullyQualifiedName).ToArray());
        Assert.Single(warnings);
        Assert.Contains("cycle detected", warnings[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Sample.Cycle.First", warnings[0], StringComparison.Ordinal);
        Assert.Contains("Sample.Cycle.Second", warnings[0], StringComparison.Ordinal);
    }

    private static Chunk CreateTypeChunk(
        string path,
        int line,
        string name,
        string fullyQualifiedName,
        SymbolKind symbolKind = SymbolKind.Class,
        IReadOnlyList<string>? inherits = null,
        IReadOnlyList<string>? implements = null)
    {
        return ChunkBuilder.Build(
            path: path,
            language: Language.CSharp,
            granularity: ChunkGranularity.Type,
            name: name,
            fullyQualifiedName: fullyQualifiedName,
            signature: null,
            content: $"type {name}",
            range: new ChunkSourceRange(line, 0, line + 1, 0),
            symbolKind: symbolKind,
            importsInScope: [],
            inherits: inherits,
            implements: implements);
    }
}
